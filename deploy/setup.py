#!/usr/bin/env python3
"""Set up your own deployment: gateways, the control plane, and a first device.

Run it from the repository root as ./setup.sh. It asks for your domain and servers,
generates every secret itself, checks DNS, runs the Ansible playbooks, enrolls a device
for the desktop client, and finishes with an end-to-end check.

Everything it knows is kept in a configuration directory outside the repository
(default ~/.config/vpn-control-plane, mode 0700):

  setup.json          your answers, nothing secret
  inventory.yml       generated from setup.json
  group_vars/all/     vars.yml, and vault.yml encrypted with ansible-vault

Run it again at any time. Answers already given are reused, secrets are never
regenerated, and a server added to setup.json is set up on the next run.
"""

from __future__ import annotations

import argparse
import base64
import getpass
import ipaddress
import json
import os
import re
import secrets
import shutil
import socket
import string
import subprocess
import sys
import tempfile
import time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEPLOY = REPO / "deploy"
DEFAULT_CONFIG = Path.home() / ".config" / "vpn-control-plane"
KEYRING = ("service", "vpn-control-plane", "key", "ansible-vault")
SSH_KEY = Path.home() / ".ssh" / "vpn_gateways"
# Where the desktop client looks for its device token, outside the repository.
CLIENT_SETTINGS = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "vpn-control-plane" / "client.json"

# ── output ─────────────────────────────────────────────────────────────────────

_TTY = sys.stdout.isatty()


def _style(code: str, text: str) -> str:
    return f"\033[{code}m{text}\033[0m" if _TTY else text


def step(title: str) -> None:
    print("\n" + _style("1", title))


def ok(text: str) -> None:
    print("  " + _style("32", "✓") + " " + text)


def warn(text: str) -> None:
    print("  " + _style("33", "!") + " " + text)


def fail(text: str) -> None:
    print("  " + _style("31", "✗") + " " + text)


def info(text: str) -> None:
    print("  " + text)


class SetupError(Exception):
    """Stops the run with a message the user can act on."""


# ── questions ──────────────────────────────────────────────────────────────────

def ask(prompt: str, default: str | None = None, pattern: str | None = None, hint: str = "") -> str:
    while True:
        suffix = f" [{default}]" if default else ""
        value = input(f"  {prompt}{suffix}: ").strip() or (default or "")
        if value and (pattern is None or re.fullmatch(pattern, value)):
            return value
        info(hint or "That does not look right, try again.")


def ask_yes(prompt: str, default: bool) -> bool:
    answer = ask(f"{prompt} (y/n)", "y" if default else "n", r"[yYnN]")
    return answer.lower() == "y"


def ask_secret(prompt: str) -> str:
    while True:
        value = getpass.getpass(f"  {prompt}: ")
        if value:
            return value


# ── secrets ────────────────────────────────────────────────────────────────────

def new_password() -> str:
    alphabet = string.ascii_letters + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(32))


def new_gateway_token() -> str:
    return "vpg_" + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")


def new_ula_prefix() -> str:
    g = secrets.token_hex(5)
    return f"fd{g[:2]}:{g[2:6]}:{g[6:]}:0"


class Vault:
    """The encrypted secrets file, opened with a password from the keyring or a local file."""

    def __init__(self, config: Path) -> None:
        self.path = config / "group_vars" / "all" / "vault.yml"
        self.password_script = config / "vault-password.sh"

    def ensure_password(self) -> None:
        if self.password_script.exists():
            return
        password = base64.b64encode(secrets.token_bytes(36)).decode()
        if shutil.which("secret-tool") and _run(["secret-tool", "lookup", *KEYRING], check=False).returncode != 0:
            stored = subprocess.run(
                ["secret-tool", "store", "--label=vpn-control-plane ansible vault", *KEYRING],
                input=password, text=True, capture_output=True,
            )
            if stored.returncode == 0:
                self._write_script("#!/bin/sh\nexec secret-tool lookup " + " ".join(KEYRING) + "\n")
                ok("vault password stored in the login keyring")
                return
        elif shutil.which("secret-tool"):
            self._write_script("#!/bin/sh\nexec secret-tool lookup " + " ".join(KEYRING) + "\n")
            ok("using the vault password already in the login keyring")
            return

        # No keyring: keep the password in a file readable only by you. The vault still
        # keeps secrets out of backups and screen shares, but not from someone with your
        # account; say so rather than pretend otherwise.
        secret_file = self.password_script.with_name(".vault-password")
        secret_file.write_text(password + "\n")
        secret_file.chmod(0o600)
        self._write_script(f"#!/bin/sh\nexec cat '{secret_file}'\n")
        warn(f"no desktop keyring found; the vault password is in {secret_file} (mode 0600)")

    def _write_script(self, content: str) -> None:
        self.password_script.write_text(content)
        self.password_script.chmod(0o700)

    def read(self) -> dict:
        if not self.path.exists():
            return {}
        result = _run(["ansible-vault", "view", "--vault-password-file", str(self.password_script), str(self.path)])
        return _parse_simple_yaml(result.stdout)

    def write(self, data: dict) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        plaintext = "---\n# Generated by setup. Every value is random; none was typed.\n" + _to_yaml(data)
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "vault.yml"
            source.write_text(plaintext)
            source.chmod(0o600)
            _run([
                "ansible-vault", "encrypt", "--vault-password-file", str(self.password_script),
                "--output", str(self.path), str(source),
            ])
        self.path.chmod(0o600)


# ── tiny YAML, enough for the files this script writes ──────────────────────────

def _to_yaml(data: dict, indent: int = 0) -> str:
    lines = []
    pad = "  " * indent
    for key, value in data.items():
        if isinstance(value, dict):
            lines.append(f"{pad}{key}:")
            lines.append(_to_yaml(value, indent + 1).rstrip("\n"))
        elif isinstance(value, bool):
            lines.append(f"{pad}{key}: {'true' if value else 'false'}")
        else:
            lines.append(f"{pad}{key}: {json.dumps(value)}")
    return "\n".join(line for line in lines if line) + "\n"


def _parse_simple_yaml(text: str) -> dict:
    """Parses the two-level mapping this script writes. Values are JSON-quoted strings."""
    root: dict = {}
    current: dict | None = None
    for raw in text.splitlines():
        if not raw.strip() or raw.lstrip().startswith("#") or raw.startswith("---"):
            continue
        key, _, value = raw.strip().partition(":")
        value = value.strip()
        if raw.startswith("  ") and current is not None:
            current[key] = _unquote(value)
        elif value == "":
            current = root.setdefault(key, {})
        else:
            root[key] = _unquote(value)
            current = None
    return root


def _unquote(value: str) -> str:
    if value.startswith('"'):
        return json.loads(value)
    return value.strip("'")


# ── commands ───────────────────────────────────────────────────────────────────

def _run(command: list[str], check: bool = True, cwd: Path | None = None, input_text: str | None = None,
         env: dict | None = None) -> subprocess.CompletedProcess:
    result = subprocess.run(
        command, cwd=cwd, input=input_text, text=True, capture_output=True,
        env={**os.environ, **(env or {})},
    )
    if check and result.returncode != 0:
        raise SetupError(f"{' '.join(command[:3])} failed:\n{result.stderr.strip() or result.stdout.strip()}")
    return result


def playbook(config: Path, name: str, limit: list[str] | None = None, extra: list[str] | None = None) -> None:
    command = [
        "ansible-playbook", "-i", str(config / "inventory.yml"), name,
        "--vault-password-file", str(config / "vault-password.sh"),
    ]
    if limit:
        command += ["--limit", ",".join(limit)]
    command += extra or []
    info(f"running {name}" + (f" on {', '.join(limit)}" if limit else "") + ", this takes a few minutes")
    log = config / "logs" / f"{name.removesuffix('.yml')}-{time.strftime('%Y%m%d-%H%M%S')}.log"
    log.parent.mkdir(exist_ok=True)
    with log.open("w") as handle:
        result = subprocess.run(
            command, cwd=DEPLOY, stdout=handle, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
            env={**os.environ, "ANSIBLE_CONFIG": str(DEPLOY / "ansible.cfg"), "ANSIBLE_NOCOLOR": "1"},
        )
    recap = [line for line in log.read_text().splitlines() if re.search(r"\bok=\d+.*failed=", line)]
    for line in recap:
        info(line.strip())
    if result.returncode != 0:
        raise SetupError(f"{name} failed. The full log is {log}")
    ok(f"{name} finished; log in {log}")


# ── the steps ──────────────────────────────────────────────────────────────────

def check_prerequisites() -> None:
    step("Checking what this machine needs")
    missing = []
    for tool, hint in [
        ("ansible-playbook", "pipx install ansible-core  (or: uv tool install ansible-core --with passlib)"),
        ("ansible-galaxy", "comes with ansible-core"),
        ("docker", "https://docs.docker.com/engine/install/"),
        ("ssh-keygen", "your distribution's openssh package"),
        ("sshpass", "your distribution's sshpass package, only for the first login to a new server"),
    ]:
        if shutil.which(tool):
            ok(tool)
        else:
            fail(f"{tool} is missing. Install: {hint}")
            missing.append(tool)
    if missing:
        raise SetupError("Install the missing tools and run ./setup.sh again.")

    probe = _run(
        ["ansible", "localhost", "-m", "debug", "-a", "msg={{ 'x' | password_hash('sha512', 'saltsalt', rounds=5000) }}"],
        check=False, env={"ANSIBLE_LOCALHOST_WARNING": "False", "ANSIBLE_INVENTORY_UNPARSED_WARNING": "False"},
    )
    if probe.returncode != 0:
        raise SetupError("Ansible cannot hash passwords. Add passlib to it: pipx inject ansible-core passlib")
    ok("ansible can hash passwords")

    _run(["ansible-galaxy", "collection", "install", "-r", str(DEPLOY / "requirements.yml")], cwd=DEPLOY)
    ok("ansible collections installed")


def gather_answers(config: Path) -> dict:
    state_file = config / "setup.json"
    state = json.loads(state_file.read_text()) if state_file.exists() else {}

    if state.get("domain") and state.get("gateways"):
        step("Using your saved answers")
        info(f"domain: {state['domain']}   gateways: {', '.join(g['id'] for g in state['gateways'])}")
        info(f"to change them, edit {state_file} and run this again")
        return state

    step("Your deployment")
    info("You need: a domain you can add DNS records to, and one or more Linux servers")
    info("(Debian 12 or 13) with a public IPv4 address. One of them also runs the control plane.")
    state["domain"] = ask("Domain for the control plane API, e.g. vpn.example.org",
                          pattern=r"(?=.{4,253}$)([a-z0-9-]+\.)+[a-z]{2,}", hint="A host name like vpn.example.org.")
    state["gateways"] = []

    while True:
        index = len(state["gateways"]) + 1
        step(f"Gateway {index}")
        city = ask("City it is in, e.g. Paris")
        country = ask("Two-letter country code, e.g. FR", pattern=r"[A-Za-z]{2}").upper()
        gateway_id = ask("Short id", f"{country.lower()}{index}", pattern=r"[a-z0-9-]{2,20}")
        address = ask("Public IPv4 address", pattern=r"[0-9.]{7,15}")
        try:
            ipaddress.IPv4Address(address)
        except ValueError as error:
            raise SetupError(f"{address} is not an IPv4 address") from error
        fresh = ask_yes("Is this a fresh server that only this project will use?", True)
        entry = {
            "id": gateway_id, "name": f"{city} 1", "city": city, "country": country,
            "address": address, "fresh": fresh,
            "ipv6_egress": ask_yes("Does it have a public IPv6 address?", True),
            "endpoint": f"{gateway_id}.{state['domain']}",
        }
        if not fresh:
            info("An existing server keeps its firewall and services; only the gateway parts are added.")
            entry["ssh_user"] = ask("SSH user with passwordless sudo")
            entry["ssh_key"] = ask("SSH private key for that user", str(Path.home() / ".ssh" / "id_ed25519"))
        state["gateways"].append(entry)
        if not ask_yes("Add another gateway?", False):
            break

    ids = [g["id"] for g in state["gateways"]]
    state["control_plane"] = ask(f"Which gateway also runs the control plane ({', '.join(ids)})", ids[0],
                                 pattern="|".join(map(re.escape, ids)))
    config.mkdir(parents=True, exist_ok=True)
    state_file.write_text(json.dumps(state, indent=2) + "\n")
    state_file.chmod(0o600)
    ok(f"saved to {state_file}")
    return state


def ensure_secrets(config: Path, state: dict) -> None:
    step("Generating secrets")
    vault = Vault(config)
    vault.ensure_password()
    data = vault.read()
    changed = False

    if "vault_admin_sudo_password" not in data:
        data["vault_admin_sudo_password"] = new_password()
        changed = True
    tokens = data.setdefault("vault_agent_tokens", {})
    roots = data.setdefault("vault_bootstrap_root_passwords", {})
    for gateway in state["gateways"]:
        if gateway["id"] not in tokens:
            tokens[gateway["id"]] = new_gateway_token()
            changed = True
        if gateway["fresh"] and not gateway.get("bootstrapped") and gateway["id"] not in roots:
            roots[gateway["id"]] = ask_secret(f"Root password the provider gave you for {gateway['id']} ({gateway['address']})")
            changed = True
    # A provider's root password is only needed for the first login. Once a server is
    # bootstrapped, its root password is locked and useless, so it leaves the vault.
    for gateway in state["gateways"]:
        if gateway.get("bootstrapped") and roots.pop(gateway["id"], None) is not None:
            changed = True
    if not roots:
        data.pop("vault_bootstrap_root_passwords", None)

    if changed:
        vault.write(data)
        ok("secrets up to date and encrypted; you never need to see them")
    else:
        ok("secrets already in place")

    if not SSH_KEY.exists():
        _run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", "vpn-gateways-admin", "-f", str(SSH_KEY)])
        ok(f"SSH key created: {SSH_KEY}")
    else:
        ok(f"SSH key: {SSH_KEY}")

    if "ipv6_prefix" not in state:
        state["ipv6_prefix"] = new_ula_prefix()
        (config / "setup.json").write_text(json.dumps(state, indent=2) + "\n")


def write_inventory(config: Path, state: dict) -> None:
    fresh = {g["id"]: g for g in state["gateways"] if g["fresh"]}
    existing = {g["id"]: g for g in state["gateways"] if not g["fresh"]}

    def host(g: dict) -> dict:
        entry = {
            "ansible_host": g["address"], "gateway_id": g["id"], "gateway_name": g["name"],
            "gateway_city": g["city"], "gateway_country": g["country"],
            "gateway_endpoint_host": g["endpoint"], "ipv6_egress": g["ipv6_egress"],
        }
        if not g["fresh"]:
            entry["ansible_user"] = g["ssh_user"]
            entry["ansible_ssh_private_key_file"] = g["ssh_key"]
        return entry

    inventory = {"all": {"children": {
        "gateways": {"children": {
            "fresh_hosts": {
                "hosts": {k: host(v) for k, v in fresh.items()},
                "vars": {
                    "ansible_user": "gwadmin",
                    "ansible_ssh_private_key_file": str(SSH_KEY),
                    "ansible_become_password": "{{ vault_admin_sudo_password }}",
                },
            },
            "existing_hosts": {"hosts": {k: host(v) for k, v in existing.items()}},
        }},
        "control_plane": {"hosts": {state["control_plane"]: None}},
    }}}
    (config / "inventory.yml").write_text(json.dumps(inventory, indent=2) + "\n")  # JSON is valid YAML

    group_vars = config / "group_vars" / "all"
    group_vars.mkdir(parents=True, exist_ok=True)
    (group_vars / "vars.yml").write_text(_to_yaml({
        "admin_user": "gwadmin",
        "admin_ssh_public_key_file": str(SSH_KEY) + ".pub",
        "wireguard_port": "51820",
        "tunnel_ipv4_prefix": "10.99",
        "tunnel_ipv6_prefix": state["ipv6_prefix"],
        "control_plane_domain": state["domain"],
        "control_plane_url": "https://{{ control_plane_domain }}",
    }).replace('"51820"', "51820"))
    for path in (config / "inventory.yml", group_vars / "vars.yml"):
        path.chmod(0o600)
    ok("inventory written")


def check_dns(state: dict) -> None:
    step("Checking DNS")
    control = next(g for g in state["gateways"] if g["id"] == state["control_plane"])
    wanted = [(state["domain"], control["address"])] + [(g["endpoint"], g["address"]) for g in state["gateways"]]

    while True:
        missing = []
        for name, address in wanted:
            try:
                found = {info[4][0] for info in socket.getaddrinfo(name, None, socket.AF_INET)}
            except socket.gaierror:
                found = set()
            if address in found:
                ok(f"{name} → {address}")
            else:
                fail(f"{name} should point to {address}" + (f", it points to {', '.join(sorted(found))}" if found else ""))
                missing.append(name)
        if not missing:
            return
        info("Add the A records above at your DNS provider (and AAAA records for IPv6, if you have them).")
        info("If the provider offers a proxy, turn it off for these names: it does not carry WireGuard's UDP.")
        if not ask_yes("Check again?", True):
            raise SetupError("DNS is not ready yet. Run ./setup.sh again once the records exist.")


def deploy(config: Path, state: dict) -> None:
    to_bootstrap = [g["id"] for g in state["gateways"] if g["fresh"] and not g.get("bootstrapped")]
    if to_bootstrap:
        step("First login to new servers")
        playbook(config, "bootstrap.yml", limit=to_bootstrap)
        for gateway in state["gateways"]:
            if gateway["id"] in to_bootstrap:
                gateway["bootstrapped"] = True
        (config / "setup.json").write_text(json.dumps(state, indent=2) + "\n")

        # The provider's root passwords are useless now: root's password is locked and
        # password login is being turned off. They are removed from the vault.
        vault = Vault(config)
        data = vault.read()
        if data.pop("vault_bootstrap_root_passwords", None) is not None:
            vault.write(data)
            ok("provider root passwords removed from the vault")

    step("Setting up gateways and the control plane")
    playbook(config, "site.yml")


def admin(state: dict, config: Path, *args: str) -> str:
    """Runs an admin command on the control plane host over SSH."""
    host = next(g for g in state["gateways"] if g["id"] == state["control_plane"])
    user = host.get("ssh_user", "gwadmin")
    key = str(Path(host.get("ssh_key", str(SSH_KEY))).expanduser())
    remote = "sudo -S -p '' docker compose -f /opt/vpn-control-plane/compose.yml exec -T api dotnet VpnControl.Api.dll admin " + " ".join(args)
    sudo_input = None
    if host["fresh"]:
        sudo_input = Vault(config).read()["vault_admin_sudo_password"] + "\n"
    else:
        remote = remote.replace("sudo -S -p ''", "sudo -n")
    result = _run(["ssh", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-i", key, f"{user}@{host['address']}", remote],
                  input_text=sudo_input)
    return result.stdout.strip()


def enroll_desktop(config: Path, state: dict) -> None:
    step("Connecting the desktop client")
    settings_path = CLIENT_SETTINGS
    if settings_path.exists() and "vpd_" in settings_path.read_text():
        ok(f"the desktop client already has a device token ({settings_path})")
        return

    name = ask("Name for this device", socket.gethostname(), pattern=r"[A-Za-z0-9._-]{1,64}")
    token = next(line for line in admin(state, config, "device", "add", name).splitlines() if line.startswith("vpd_"))
    settings = {
        "Desktop": {"UseControlPlaneApi": True, "UseRealTunnel": True, "UseRealLatencyProbe": True},
        "VpnConnection": {"DeviceName": name, "Mtu": client_mtu()},
        "ServerCatalog": {"BaseAddress": f"https://{state['domain']}/", "DeviceToken": token},
    }
    settings_path.parent.mkdir(parents=True, exist_ok=True)
    settings_path.parent.chmod(0o700)
    settings_path.write_text(json.dumps(settings, indent=2) + "\n")
    settings_path.chmod(0o600)
    ok(f"device enrolled; token written to {settings_path} (mode 0600, outside the repository)")


def client_mtu() -> int:
    """The tunnel MTU this machine can carry.

    1420 fits a 1500 byte path. If this machine's own traffic already goes through a tunnel
    (another VPN, PPPoE), its route has a smaller MTU, and a 1420 byte inner packet would be
    fragmented on the way out; some paths drop fragments and large transfers stall. The
    WireGuard overhead is 80 bytes with an IPv6 outer header, so that is subtracted.
    """
    try:
        route = _run(["ip", "route", "get", "1.1.1.1"]).stdout
        device = re.search(r"\bdev (\S+)", route).group(1)  # type: ignore[union-attr]
        path_mtu = int(Path(f"/sys/class/net/{device}/mtu").read_text())
    except (SetupError, AttributeError, OSError, ValueError):
        return 1420
    return max(1280, min(1420, path_mtu - 80))


def end_to_end(config: Path, state: dict) -> None:
    step("Checking every gateway end to end")
    _run(["docker", "build", "-q", "-t", "vpn-e2e", str(REPO / "tools" / "e2e")])
    token = next(line for line in admin(state, config, "device", "add", "setup-check").splitlines() if line.startswith("vpd_"))
    try:
        ids = [g["id"] for g in state["gateways"]]
        result = _run([
            "docker", "run", "--rm", "--cap-add", "NET_ADMIN",
            "--sysctl", "net.ipv4.conf.all.src_valid_mark=1", "--sysctl", "net.ipv6.conf.all.disable_ipv6=0",
            "-e", f"API=https://{state['domain']}", "-e", f"TOKEN={token}", "-e", f"MTU={client_mtu()}",
            "vpn-e2e", *ids,
        ], check=False)
        by_gateway = {g["id"]: g for g in state["gateways"]}
        current = None
        for line in result.stdout.splitlines():
            first = line.split()[0] if line.split() else ""
            if first in by_gateway:
                current = first
                total = re.search(r"connect_total_ms=(\d+)", line)
                ok(f"{by_gateway[first]['city']:<14} connected in {int(total.group(1)) if total else '?'} ms")
            elif current and "egress_ipv4=" in line:
                egress = line.split("=", 1)[1]
                if egress != by_gateway[current]["address"]:
                    fail(f"{by_gateway[current]['city']}: traffic left from {egress}, expected {by_gateway[current]['address']}")
            elif "failed" in line or "no_handshake" in line:
                fail(line.strip())
        if result.returncode != 0:
            raise SetupError("The end-to-end check failed. Details:\n" + result.stdout[-2000:])
    finally:
        for line in admin(state, config, "device", "list").splitlines():
            parts = line.split()
            if len(parts) > 2 and parts[1] == "setup-check" and parts[2] == "active":
                admin(state, config, "device", "revoke", parts[0])


def main() -> int:
    parser = argparse.ArgumentParser(description="Set up your own VPN deployment.")
    parser.add_argument("--config-dir", type=Path, default=DEFAULT_CONFIG,
                        help=f"where answers and secrets are kept (default {DEFAULT_CONFIG})")
    parser.add_argument("--check-only", action="store_true",
                        help="skip deployment; only check DNS and run the end-to-end check")
    args = parser.parse_args()
    config = args.config_dir.expanduser()

    # Progress should appear as it happens, also when the output goes to a file or a pipe.
    sys.stdout.reconfigure(line_buffering=True)

    try:
        config.mkdir(parents=True, exist_ok=True)
        config.chmod(0o700)
        check_prerequisites()
        state = gather_answers(config)
        ensure_secrets(config, state)
        write_inventory(config, state)
        check_dns(state)
        if not args.check_only:
            deploy(config, state)
            enroll_desktop(config, state)
        end_to_end(config, state)
    except SetupError as error:
        print()
        fail(str(error))
        return 1
    except KeyboardInterrupt:
        print()
        warn("stopped; run ./setup.sh again to continue where you left off")
        return 130

    step("Done")
    info(f"API:      https://{state['domain']}")
    info("Client:   ./client.sh        (a real tunnel, in a container, without touching this machine's routing)")
    info("          dotnet run --project src/VpnControl.Desktop   (on the host; the tunnel then needs root)")
    info(f"Devices:  see docs in deploy/README.md; secrets live in {config}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
