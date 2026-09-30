"""Tests for the peer sync agent's validation and rendering.

Run with: python3 -m unittest discover -s deploy/peer-sync
"""

from __future__ import annotations

import base64
import ipaddress
import os
import unittest
from pathlib import Path

import vpn_peer_sync as agent

KEY_A = base64.b64encode(os.urandom(32)).decode()
KEY_B = base64.b64encode(os.urandom(32)).decode()

SETTINGS = agent.Settings(
    api_url="https://control.example.invalid",
    gateway_id="gw-1",
    interface="wg0",
    wg_quick_config=Path("/etc/wireguard/wg0.conf"),
    private_key_file=Path("/etc/wireguard/wg0.key"),
    token_file=Path("/etc/vpn-peer-sync/token"),
    ipv4_subnet=ipaddress.IPv4Network("10.99.0.0/16"),
    ipv6_subnet=ipaddress.IPv6Network("fd4c:7a2e:91b3::/64"),
)


def payload(*peers: dict, gateway_id: str = "gw-1") -> dict:
    return {"gatewayId": gateway_id, "version": 7, "peers": list(peers)}


def peer(key: str = KEY_A, *ips: str) -> dict:
    return {"publicKey": key, "allowedIps": list(ips or ("10.99.0.2/32", "fd4c:7a2e:91b3::2/128"))}


class ValidatePeersTests(unittest.TestCase):
    def test_a_well_formed_peer_is_admitted(self) -> None:
        peers = agent.validate_peers(payload(peer()), SETTINGS)

        self.assertEqual(peers, [agent.Peer(KEY_A, ("10.99.0.2/32", "fd4c:7a2e:91b3::2/128"))])

    def test_an_ipv4_only_peer_is_admitted(self) -> None:
        peers = agent.validate_peers(payload(peer(KEY_A, "10.99.0.2/32")), SETTINGS)

        self.assertEqual(len(peers), 1)

    def test_a_default_route_is_refused(self) -> None:
        # The attack this agent exists to stop: a compromised control plane routing every
        # client's traffic to a peer it controls.
        for hostile in ("0.0.0.0/0", "::/0", "10.99.0.0/16", "10.99.0.0/31"):
            with self.subTest(hostile=hostile):
                self.assertEqual(agent.validate_peers(payload(peer(KEY_A, hostile)), SETTINGS), [])

    def test_an_address_outside_the_tunnel_subnet_is_refused(self) -> None:
        for outside in ("192.168.1.10/32", "10.100.0.2/32", "8.8.8.8/32"):
            with self.subTest(outside=outside):
                self.assertEqual(agent.validate_peers(payload(peer(KEY_A, outside)), SETTINGS), [])

    def test_the_gateways_own_address_is_refused(self) -> None:
        self.assertEqual(agent.validate_peers(payload(peer(KEY_A, "10.99.0.1/32")), SETTINGS), [])
        self.assertEqual(
            agent.validate_peers(payload(peer(KEY_A, "10.99.0.2/32", "fd4c:7a2e:91b3::1/128")), SETTINGS),
            [],
        )

    def test_an_address_already_taken_by_an_earlier_peer_is_refused(self) -> None:
        peers = agent.validate_peers(
            payload(peer(KEY_A, "10.99.0.2/32"), peer(KEY_B, "10.99.0.2/32")), SETTINGS
        )

        self.assertEqual([p.public_key for p in peers], [KEY_A])

    def test_a_duplicate_key_is_refused(self) -> None:
        peers = agent.validate_peers(
            payload(peer(KEY_A, "10.99.0.2/32"), peer(KEY_A, "10.99.0.3/32")), SETTINGS
        )

        self.assertEqual(len(peers), 1)

    def test_malformed_keys_are_refused(self) -> None:
        for bad in ("", "short", "A" * 44, base64.b64encode(os.urandom(31)).decode(), None, 42):
            with self.subTest(bad=bad):
                self.assertEqual(agent.validate_peers(payload(peer(bad)), SETTINGS), [])  # type: ignore[arg-type]

    def test_ipv6_is_refused_when_the_gateway_has_no_ipv6_subnet(self) -> None:
        settings = agent.Settings(**{**SETTINGS.__dict__, "ipv6_subnet": None})

        self.assertEqual(agent.validate_peers(payload(peer()), settings), [])

    def test_two_addresses_of_one_family_are_refused(self) -> None:
        self.assertEqual(
            agent.validate_peers(payload(peer(KEY_A, "10.99.0.2/32", "10.99.0.3/32")), SETTINGS), []
        )

    def test_a_peer_with_only_ipv6_is_refused(self) -> None:
        self.assertEqual(
            agent.validate_peers(payload(peer(KEY_A, "fd4c:7a2e:91b3::2/128")), SETTINGS), []
        )

    def test_one_bad_peer_does_not_block_the_others(self) -> None:
        peers = agent.validate_peers(
            payload(peer(KEY_A, "0.0.0.0/0"), peer(KEY_B, "10.99.0.3/32")), SETTINGS
        )

        self.assertEqual([p.public_key for p in peers], [KEY_B])

    def test_a_response_for_another_gateway_is_rejected_whole(self) -> None:
        with self.assertRaises(agent.SyncError):
            agent.validate_peers(payload(peer(), gateway_id="gw-2"), SETTINGS)

    def test_a_response_of_the_wrong_shape_is_rejected_whole(self) -> None:
        for bad in ([], "text", {"gatewayId": "gw-1"}, {"gatewayId": "gw-1", "peers": {}}):
            with self.subTest(bad=bad), self.assertRaises(agent.SyncError):
                agent.validate_peers(bad, SETTINGS)

    def test_an_oversized_peer_list_is_rejected_whole(self) -> None:
        with self.assertRaises(agent.SyncError):
            agent.validate_peers(payload(*([peer()] * (agent.MAX_PEERS + 1))), SETTINGS)


class RenderTests(unittest.TestCase):
    def test_the_private_key_and_peers_are_added_to_the_interface_section(self) -> None:
        base = "[Interface]\nListenPort = 51820\n"

        text = agent.render(base, KEY_B, [agent.Peer(KEY_A, ("10.99.0.2/32", "fd4c:7a2e:91b3::2/128"))])

        self.assertTrue(text.startswith(f"[Interface]\nPrivateKey = {KEY_B}\nListenPort = 51820\n"))
        self.assertIn(f"[Peer]\nPublicKey = {KEY_A}\nAllowedIPs = 10.99.0.2/32, fd4c:7a2e:91b3::2/128\n", text)

    def test_an_empty_list_leaves_only_the_interface(self) -> None:
        text = agent.render("[Interface]\nListenPort = 51820\n", KEY_B, [])

        self.assertNotIn("[Peer]", text)

    def test_a_base_without_an_interface_section_is_refused(self) -> None:
        with self.assertRaises(agent.SyncError):
            agent.render("ListenPort = 51820\n", KEY_B, [])


class SettingsTests(unittest.TestCase):
    def test_plain_http_is_refused(self) -> None:
        path = Path(self._write('{"api_url": "http://x", "gateway_id": "g", "ipv4_subnet": "10.99.0.0/16"}'))

        with self.assertRaises(agent.SyncError):
            agent.Settings.load(path)

    def test_gateway_addresses_are_the_first_host_of_each_subnet(self) -> None:
        self.assertEqual(
            SETTINGS.gateway_addresses,
            {ipaddress.ip_address("10.99.0.1"), ipaddress.ip_address("fd4c:7a2e:91b3::1")},
        )

    def _write(self, text: str) -> str:
        import tempfile

        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False)
        handle.write(text)
        handle.close()
        self.addCleanup(os.unlink, handle.name)
        return handle.name


if __name__ == "__main__":
    unittest.main()
