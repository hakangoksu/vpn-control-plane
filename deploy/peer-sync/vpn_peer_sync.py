#!/usr/bin/env python3
"""Keep a gateway's WireGuard peers in step with the control plane.

Runs on each gateway as a long-lived systemd service. It long-polls the control plane:
each request says which version of the peer list the gateway last applied and is held
until a newer version exists, so a new client is admitted within one round trip. The
list is complete every time; it is validated and applied with `wg syncconf`, which adds
new peers, removes old ones and leaves unchanged ones alone without dropping their
sessions. The next request carries the version just applied, which is how the control
plane learns the key is in place and can tell the waiting client to go ahead.

It logs only when something changes, so a quiet gateway writes nothing to the journal.

The agent does not trust the control plane blindly. A peer is admitted only if its key is
a well formed X25519 public key and its addresses are single host addresses inside this
gateway's tunnel subnets. Without that check, a compromised control plane could hand a
peer `0.0.0.0/0` and have the gateway route every client's traffic to it. The agent is
the last point where the gateway can say no, so it says no there.

If the control plane cannot be reached, or answers with something unusable, nothing is
changed: existing tunnels keep working and the next run tries again.

Standard library only, so it runs on a stock Debian install with nothing added.
"""

from __future__ import annotations

import base64
import binascii
import ipaddress
import json
import logging
import argparse
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

LOG = logging.getLogger("vpn-peer-sync")

DEFAULT_CONFIG_PATH = Path("/etc/vpn-peer-sync/config.json")
# The control plane holds a poll for at most this long; the HTTP timeout is longer so a
# held request is never mistaken for a dead one.
LONG_POLL_SECONDS = 10
REQUEST_TIMEOUT_SECONDS = LONG_POLL_SECONDS + 10
RETRY_DELAY_SECONDS = 2
MAX_RESPONSE_BYTES = 1024 * 1024
MAX_PEERS = 4096


class SyncError(Exception):
    """A run could not complete. Nothing was changed on the interface."""


@dataclass(frozen=True)
class Settings:
    """Agent settings, read from a root-only JSON file written at deployment."""

    api_url: str
    gateway_id: str
    interface: str
    wg_quick_config: Path
    private_key_file: Path
    token_file: Path
    ipv4_subnet: ipaddress.IPv4Network
    ipv6_subnet: ipaddress.IPv6Network | None

    @staticmethod
    def load(path: Path) -> Settings:
        raw = json.loads(path.read_text(encoding="utf-8"))

        api_url = str(raw["api_url"]).rstrip("/")
        if not api_url.startswith("https://") and not raw.get("allow_insecure_http", False):
            # The token travels in a header. Over plain HTTP anyone on the path could copy
            # it, so HTTPS is required unless a test explicitly opts out.
            raise SyncError("api_url must use https")

        ipv6 = raw.get("ipv6_subnet") or None
        return Settings(
            api_url=api_url,
            gateway_id=str(raw["gateway_id"]),
            interface=str(raw.get("interface", "wg0")),
            wg_quick_config=Path(raw.get("wg_quick_config", "/etc/wireguard/wg0.conf")),
            private_key_file=Path(raw.get("private_key_file", "/etc/wireguard/wg0.key")),
            token_file=Path(raw.get("token_file", "/etc/vpn-peer-sync/token")),
            ipv4_subnet=ipaddress.IPv4Network(raw["ipv4_subnet"], strict=True),
            ipv6_subnet=ipaddress.IPv6Network(ipv6, strict=True) if ipv6 else None,
        )

    @property
    def gateway_addresses(self) -> set[ipaddress.IPv4Address | ipaddress.IPv6Address]:
        """The gateway's own tunnel addresses: the first host of each subnet."""
        addresses: set[ipaddress.IPv4Address | ipaddress.IPv6Address] = {
            self.ipv4_subnet.network_address + 1
        }
        if self.ipv6_subnet is not None:
            addresses.add(self.ipv6_subnet.network_address + 1)
        return addresses


@dataclass(frozen=True)
class Peer:
    """One validated peer, ready to be written as a [Peer] section."""

    public_key: str
    allowed_ips: tuple[str, ...]


def is_valid_public_key(value: object) -> bool:
    """A WireGuard public key is 32 bytes in standard base64, 44 characters with padding."""
    if not isinstance(value, str) or len(value) != 44:
        return False
    try:
        return len(base64.b64decode(value, validate=True)) == 32
    except (binascii.Error, ValueError):
        return False


def validate_peers(payload: object, settings: Settings) -> list[Peer]:
    """Turns the control plane's answer into peers this gateway is willing to admit.

    The response as a whole is rejected if it is the wrong shape or names another gateway,
    because then nothing in it can be trusted. A single bad entry is skipped and logged, so
    one malformed registration cannot keep every other client off the gateway.
    """
    if not isinstance(payload, dict):
        raise SyncError("response is not a JSON object")

    if payload.get("gatewayId") != settings.gateway_id:
        raise SyncError(
            f"response is for gateway {payload.get('gatewayId')!r}, expected {settings.gateway_id!r}"
        )

    entries = payload.get("peers")
    if not isinstance(entries, list):
        raise SyncError("response has no peer list")

    if len(entries) > MAX_PEERS:
        raise SyncError(f"response lists {len(entries)} peers, more than the limit of {MAX_PEERS}")

    reserved = settings.gateway_addresses
    seen_keys: set[str] = set()
    seen_addresses: set[ipaddress.IPv4Network | ipaddress.IPv6Network] = set()
    peers: list[Peer] = []

    for index, entry in enumerate(entries):
        problem = _check_entry(entry, settings, reserved, seen_keys, seen_addresses)
        if problem is not None:
            LOG.warning("skipping peer %d: %s", index, problem)
            continue

        networks = [ipaddress.ip_network(value, strict=True) for value in entry["allowedIps"]]
        seen_keys.add(entry["publicKey"])
        seen_addresses.update(networks)
        peers.append(Peer(entry["publicKey"], tuple(str(n) for n in networks)))

    return peers


def _check_entry(
    entry: object,
    settings: Settings,
    reserved: set[ipaddress.IPv4Address | ipaddress.IPv6Address],
    seen_keys: set[str],
    seen_addresses: set[ipaddress.IPv4Network | ipaddress.IPv6Network],
) -> str | None:
    """Returns why an entry is unacceptable, or None when it is fine."""
    if not isinstance(entry, dict):
        return "not an object"

    key = entry.get("publicKey")
    if not is_valid_public_key(key):
        return "public key is not 32 bytes of base64"
    if key in seen_keys:
        return "duplicate public key"

    allowed = entry.get("allowedIps")
    if not isinstance(allowed, list) or not 1 <= len(allowed) <= 2:
        return "allowedIps must hold one or two addresses"

    families: set[int] = set()
    for value in allowed:
        if not isinstance(value, str):
            return "allowedIps entry is not a string"
        try:
            network = ipaddress.ip_network(value, strict=True)
        except ValueError:
            return f"{value!r} is not a network"

        # A single host only. A wider prefix would route other addresses, possibly the
        # whole internet, to this peer.
        if network.num_addresses != 1:
            return f"{value} is not a single host address"

        subnet = settings.ipv4_subnet if network.version == 4 else settings.ipv6_subnet
        if subnet is None or not network.subnet_of(subnet):  # type: ignore[arg-type]
            return f"{value} is outside this gateway's tunnel subnet"
        if network.network_address in reserved:
            return f"{value} is the gateway's own address"
        if network in seen_addresses:
            return f"{value} is already assigned to another peer"
        if network.version in families:
            return "more than one address of the same family"
        families.add(network.version)

    if 4 not in families:
        return "no IPv4 address"

    return None


def render(base_config: str, private_key: str, peers: list[Peer]) -> str:
    """Builds the full `wg syncconf` input: interface section, private key, then peers.

    The base comes from `wg-quick strip` and keeps the listen port and any peers written by
    hand, so an operator's own static peer survives every sync. The private key is not in
    that file: wg-quick loads it from a separate root-only file in PostUp, so the key never
    passes through the deployment tooling. It is spliced in here, straight after the
    [Interface] header, because `wg syncconf` would otherwise clear it.
    """
    base = base_config.rstrip("\n")
    header = "[Interface]"
    if header not in base:
        raise SyncError("base configuration has no [Interface] section")
    base = base.replace(header, f"{header}\nPrivateKey = {private_key}", 1)

    lines = [base, ""]
    for peer in peers:
        lines += ["[Peer]", f"PublicKey = {peer.public_key}", f"AllowedIPs = {', '.join(peer.allowed_ips)}", ""]
    return "\n".join(lines)


def fetch(settings: Settings, applied_version: int = 0, wait_seconds: int = 0) -> object:
    """Fetches this gateway's peer list. Raises SyncError on any failure.

    applied_version is the version the gateway has in place; wait_seconds asks the
    control plane to hold the request until there is something newer.
    """
    token = settings.token_file.read_text(encoding="utf-8").strip()
    if not token.startswith("vpg_"):
        raise SyncError(f"{settings.token_file} does not hold a gateway token")

    request = urllib.request.Request(
        f"{settings.api_url}/api/gateway/peers?applied={int(applied_version)}&wait={int(wait_seconds)}",
        headers={"Authorization": f"Bearer {token}", "Accept": "application/json"},
    )

    try:
        # The default context verifies the certificate chain and the host name.
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:  # noqa: S310
            body = response.read(MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as error:
        raise SyncError(f"control plane answered {error.code}") from error
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise SyncError(f"control plane unreachable: {error}") from error

    if len(body) > MAX_RESPONSE_BYTES:
        raise SyncError("response is larger than the limit")

    try:
        return json.loads(body)
    except json.JSONDecodeError as error:
        raise SyncError("response is not valid JSON") from error


def current_peers(interface: str) -> set[str]:
    """Public keys currently configured on the interface."""
    output = _run(["wg", "show", interface, "peers"])
    return {line.strip() for line in output.splitlines() if line.strip()}


def apply(settings: Settings, peers: list[Peer]) -> None:
    """Applies the peer list with `wg syncconf`, passing the private key on stdin only."""
    base = _run(["wg-quick", "strip", str(settings.wg_quick_config)])
    private_key = settings.private_key_file.read_text(encoding="utf-8").strip()
    if not is_valid_public_key(private_key):
        # Same encoding as a public key: 32 bytes of base64.
        raise SyncError(f"{settings.private_key_file} does not hold a WireGuard key")
    _run(["wg", "syncconf", settings.interface, "/dev/stdin"], stdin=render(base, private_key, peers))


def _run(command: list[str], stdin: str | None = None) -> str:
    try:
        result = subprocess.run(  # noqa: S603
            command,
            input=stdin,
            capture_output=True,
            text=True,
            check=True,
            timeout=30,
        )
    except subprocess.CalledProcessError as error:
        raise SyncError(f"{command[0]} {command[1]} failed: {error.stderr.strip()}") from error
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SyncError(f"{command[0]} could not be run: {error}") from error
    return result.stdout


def sync_once(settings: Settings, applied_version: int = 0, wait_seconds: int = 0) -> tuple[int, int, int, int]:
    """One fetch, validate and apply. Returns (version, peers, added, removed)."""
    payload = fetch(settings, applied_version, wait_seconds)
    peers = validate_peers(payload, settings)
    version = payload.get("version") if isinstance(payload, dict) else None
    if not isinstance(version, int) or version < 0:
        raise SyncError("response has no version")

    before = current_peers(settings.interface)
    wanted = {peer.public_key for peer in peers}

    # Nothing to do is the common case. Skipping the apply keeps a quiet gateway quiet.
    if before != wanted:
        apply(settings, peers)

    return version, len(peers), len(wanted - before), len(before - wanted)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("config", nargs="?", type=Path, default=DEFAULT_CONFIG_PATH)
    parser.add_argument(
        "--follow",
        action="store_true",
        help="keep running and long-poll for changes; without it, sync once and exit",
    )
    args = parser.parse_args(argv[1:])
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    try:
        settings = Settings.load(args.config)
    except (SyncError, OSError, KeyError, ValueError) as error:
        LOG.error("cannot load settings: %s", error)
        return 1

    applied = 0
    last_error: str | None = None
    while True:
        try:
            wait = LONG_POLL_SECONDS if args.follow and applied else 0
            applied, count, added, removed = sync_once(settings, applied, wait)
            if added or removed:
                LOG.info("applied %d peers (%d added, %d removed)", count, added, removed)
            if last_error is not None:
                LOG.info("sync recovered")
            last_error = None
        except (SyncError, OSError, KeyError, ValueError) as error:
            # Logged once per distinct failure rather than once per attempt, so an outage
            # of the control plane is one line in the journal, not thousands.
            if str(error) != last_error:
                LOG.error("sync failed, interface left unchanged: %s", error)
            last_error = str(error)
            if not args.follow:
                return 1
            time.sleep(RETRY_DELAY_SECONDS)
            continue

        if not args.follow:
            return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
