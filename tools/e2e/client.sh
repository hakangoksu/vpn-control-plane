#!/bin/bash
# End-to-end check and measurement against a real deployment.
#
#   vpn-e2e <gateway-id> [<gateway-id> ...]
#
# Environment:
#   API    control plane base URL, for example https://vpn.example.org
#   TOKEN  a device token issued with `admin device add`
#   MTU    tunnel MTU (default 1420; lower it when this machine is itself behind a tunnel)
#
# With one gateway it connects, checks, and disconnects. With several it connects to the
# first and then switches through the rest, timing each switch. Every figure printed is
# measured on this run; nothing is estimated.
set -uo pipefail

: "${API:?set API}" "${TOKEN:?set TOKEN}"
MTU=${MTU:-1420}
ms() { date +%s%3N; }
umask 077

register() {
  curl -sS --fail-with-body -X POST "$API/api/peers" \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"serverId\":\"$1\",\"publicKey\":\"$2\"}"
}

release() {
  curl -s -o /dev/null -w '%{http_code}' -X DELETE "$API/api/peers/$1" -H "Authorization: Bearer $TOKEN"
}

write_config() {
  local cfg=$1 priv=$2 addr6
  addr6=$(jq -r '.assignedAddressV6 // empty' <<<"$cfg")
  cat > /etc/wireguard/wg0.conf <<CONF
[Interface]
PrivateKey = $priv
Address = $(jq -r .assignedAddress <<<"$cfg")${addr6:+, $addr6}
DNS = $(jq -r '.dnsServers | join(", ")' <<<"$cfg")
MTU = $MTU

[Peer]
PublicKey = $(jq -r .serverPublicKey <<<"$cfg")
AllowedIPs = $(jq -r '.allowedIps | join(", ")' <<<"$cfg")
Endpoint = $(jq -r .endpoint <<<"$cfg")
PersistentKeepalive = 25
CONF
}

wait_for_handshake() {
  local gw_addr=$1
  for _ in $(seq 1 300); do
    ping -c1 -W1 "$gw_addr" >/dev/null 2>&1 || true
    [ "$(wg show wg0 latest-handshakes 2>/dev/null | awk '{print $2}')" -gt 0 ] 2>/dev/null && return 0
    sleep 0.05
  done
  return 1
}

# Connects to one gateway. Prints timings; leaves the tunnel up.
connect() {
  local gw=$1 priv pub cfg t0 t1 t2 t3
  priv=$(wg genkey); pub=$(printf %s "$priv" | wg pubkey)

  t0=$(ms)
  cfg=$(register "$gw" "$pub") || { echo "$gw register_failed $cfg"; return 1; }
  t1=$(ms)
  PEER_ID=$(jq -r .peerId <<<"$cfg")
  GW_ADDR="$(jq -r .assignedAddress <<<"$cfg" | cut -d. -f1-2).0.1"
  write_config "$cfg" "$priv"

  wg-quick up wg0 >/tmp/wg-up.log 2>&1 || { echo "$gw wg_quick_up_failed"; cat /tmp/wg-up.log; release "$PEER_ID" >/dev/null; return 1; }
  t2=$(ms)
  wait_for_handshake "$GW_ADDR" || { echo "$gw no_handshake"; return 1; }
  t3=$(ms)

  echo "$gw register_ms=$((t1 - t0)) active_on_gateway=$(jq -r .activeOnGateway <<<"$cfg") interface_up_ms=$((t2 - t1)) first_handshake_ms=$((t3 - t2)) connect_total_ms=$((t3 - t0))"
}

disconnect() {
  wg-quick down wg0 >/dev/null 2>&1
  echo "  release=$(release "$PEER_ID")"
}

check() {
  local gw=$1 s v6
  echo "  egress_ipv4=$(curl -4 -s --max-time 8 https://api.ipify.org || echo none)"
  s=$(ms); v6=$(curl -6 -s --max-time 8 https://api64.ipify.org || true)
  echo "  egress_ipv6=${v6:-none} ipv6_attempt_ms=$(( $(ms) - s ))"
  echo "  resolver_egress=$(dig +short +time=3 TXT o-o.myaddr.l.google.com | tr -d '"' | head -1)"
  echo "  tunnel_rtt_ms=$(ping -c10 -i0.2 -q "$GW_ADDR" | awk -F/ '/rtt/{print $5}')"
  echo "  cloud_metadata_reachable=$(curl -s --max-time 3 -o /dev/null -w '%{http_code}' http://169.254.169.254/ | sed 's/^000$/no/')"
  echo "  smtp_25_reachable=$(timeout 4 bash -c 'exec 3<>/dev/tcp/gmail-smtp-in.l.google.com/25' 2>/dev/null && echo yes || echo no)"
}

[ $# -ge 1 ] || { echo "usage: vpn-e2e <gateway-id> [<gateway-id> ...]"; exit 2; }

connect "$1" || exit 1
check "$1"
shift

# Each switch is what the desktop client does: release, bring the old interface down,
# register on the new gateway, bring the new interface up, wait for the handshake.
for next in "$@"; do
  s=$(ms)
  wg-quick down wg0 >/dev/null 2>&1
  release "$PEER_ID" >/dev/null
  connect "$next" || exit 1
  echo "  switch_ms=$(( $(ms) - s ))"
  check "$next"
done

disconnect
