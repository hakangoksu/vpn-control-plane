# Deploying your own gateways

This directory turns a handful of Linux servers into WireGuard gateways and runs the control
plane API on one of them. Nothing in it names a real host: you bring the servers, the domain and
the secrets, and they stay in a directory outside this repository.

The reference deployment this was written against has four gateways on Debian 13: three fresh
virtual servers and one existing server that already ran a mail server, nginx and Docker. The
existing server also hosts the control plane.

## What you end up with

- A client registers its public key over HTTPS with its own device token, and gets back an
  address and the gateway's key. Traffic then goes straight to the chosen gateway; the control
  plane is not on the data path.
- Each gateway runs a small agent that long-polls the API with its own token and applies the
  peer list with `wg syncconf`. The API holds no credential for any gateway and never connects to
  one.
- A registration answers only after the gateway has confirmed it admitted the key, so the
  client's first handshake is not dropped.

## Prerequisites

- Control machine: Ansible (`ansible-core`, with `passlib` for password hashing), Docker (to
  build the API image), `sshpass` for the one bootstrap run, and the collections in
  `requirements.yml`:

  ```bash
  uv tool install ansible-core --with passlib   # or pipx, or your package manager
  ansible-galaxy collection install -r requirements.yml
  ```

- Servers: Debian 12 or 13 with a public IPv4 address. IPv6 is optional per gateway.
- A domain you control, with DNS records you can add.

## 1. DNS

One record for the API, one per gateway. If your DNS provider offers a proxy, turn it off for
all of them: a proxy does not carry WireGuard's UDP.

| Name | Type | Value |
|---|---|---|
| `vpn.example.org` | A (and AAAA if it has IPv6) | the control plane host |
| `gw1.vpn.example.org` | A, AAAA | gateway 1 |
| `gw2.vpn.example.org` | A, AAAA | gateway 2 |

## 2. Inventory and secrets, outside the repository

```bash
mkdir -p ~/vpn-secrets && chmod 700 ~/vpn-secrets
cp -r deploy/example ~/vpn-secrets/ansible
```

Edit `~/vpn-secrets/ansible/inventory.yml` and `group_vars/all/vars.yml`. Hosts this project
owns go under `fresh_hosts`; a server that already runs other services goes under
`existing_hosts`, which skips the hardening role and leaves its firewall in place.

Generate a unique local IPv6 prefix instead of keeping the example one:

```bash
python3 -c "import secrets; g=secrets.token_hex(5); print(f'fd{g[:2]}:{g[2:6]}:{g[6:]}:0')"
```

Fill `group_vars/all/vault.yml` with generated values (the file shows the commands), then
encrypt it. The vault password lives in the desktop keyring rather than in a file next to the
vault:

```bash
openssl rand -base64 48 | tr -d '\n' | \
  secret-tool store --label="vpn-control-plane ansible vault" service vpn-control-plane key ansible-vault
ansible-vault encrypt ~/vpn-secrets/ansible/group_vars/all/vault.yml \
  --vault-password-file deploy/scripts/vault-password-from-keyring.sh
```

Create the SSH key the admin account will use:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/vpn_gateways -C vpn-gateways-admin
```

## 3. Bootstrap fresh servers

The only play that logs in as root with the provider's password. It creates the admin account,
authorises the key, checks that the key works, and only then locks the root password.

```bash
cd deploy
ansible-playbook -i ~/vpn-secrets/ansible/inventory.yml bootstrap.yml \
  --vault-password-file scripts/vault-password-from-keyring.sh
```

## 4. Converge everything

```bash
ansible-playbook -i ~/vpn-secrets/ansible/inventory.yml site.yml \
  --vault-password-file scripts/vault-password-from-keyring.sh
```

Tags run one layer at a time: `harden`, `gateway`, `control_plane`, `peer_sync`. The playbook is
idempotent; a second run on an unchanged deployment reports no changes.

## 5. Enroll a device

Tokens are issued on the control plane host, shown once, and stored only as a hash:

```bash
ssh <control-plane-host> sudo docker compose -f /opt/vpn-control-plane/compose.yml \
  exec -T api dotnet VpnControl.Api.dll admin device add laptop
```

Put the token and the API address in the client's git-ignored
`src/VpnControl.Desktop/appsettings.Local.json`:

```json
{
  "Desktop": { "UseControlPlaneApi": true, "UseRealTunnel": true, "UseRealLatencyProbe": true },
  "ServerCatalog": { "BaseAddress": "https://vpn.example.org/", "DeviceToken": "vpd_..." }
}
```

`admin device list` shows devices; `admin device revoke <id>` stops the token at once and makes
every gateway drop the device's peer on its next poll, within ten seconds.

## 6. Check it end to end

`tools/e2e` is a WireGuard client in a container. It runs in its own network namespace, so the
machine running it keeps its routing:

```bash
docker build -t vpn-e2e tools/e2e
docker run --rm --cap-add NET_ADMIN \
  --sysctl net.ipv4.conf.all.src_valid_mark=1 --sysctl net.ipv6.conf.all.disable_ipv6=0 \
  -e API=https://vpn.example.org -e TOKEN=vpd_... \
  vpn-e2e gw1 gw2
```

It connects to the first gateway, switches to each following one, and prints for every step the
registration time, the time to the first handshake, the switch time, the tunnel round trip, the
public IPv4 and IPv6 addresses traffic leaves from, the address DNS queries leave from, and
whether the cloud metadata service and outbound SMTP are reachable (they should not be).

If the machine you run it on is itself behind a tunnel, pass `-e MTU=1340` or lower, or large
packets will be fragmented and some paths drop fragments.

## What the roles do

| Role | Hosts | Changes |
|---|---|---|
| `base_hardening` | fresh | Full upgrade; unattended security upgrades with a 04:30 reboot when a kernel needs it; sshd drop-in read before the provider's (key only, no root, one allowed user, no forwarding); ICMP redirect and source route handling off; root password locked. |
| `host_firewall` | fresh | One nftables table, default drop on input: SSH with a per-source rate limit, WireGuard, and DNS and ping from the tunnel only. No `flush ruleset`, so it never removes another table. |
| `wireguard_gateway` | all | `wg0` with the gateway's addresses. The private key is generated on the gateway and never leaves it; `wg0.conf` does not contain it. IPv4 and IPv6 forwarding, with router advertisements still accepted on the uplink so the host keeps its IPv6 route. |
| `gateway_firewall` | all | Its own nftables table, loaded by its own unit, that only looks at `wg0` traffic: clients cannot reach each other, private and link-local ranges (including the cloud metadata service), or SMTP; only addresses the control plane hands out may leave; NAT for IPv4, and for IPv6 where the gateway has it. Without IPv6 egress, tunnel IPv6 is refused with an immediate ICMPv6 error, so it can neither leak around the tunnel nor make applications wait. On existing hosts it also adds three ufw rules and nothing else. |
| `dns_resolver` | all | unbound with DNSSEC validation, listening only on the tunnel addresses and answering only tunnel clients. The package's hook that rewrites the host's own resolver is masked. |
| `control_plane` | control plane | Builds the API image on the control machine and ships it as an archive, so the server needs no SDK or registry credentials. Runs it with a read-only root filesystem, no capabilities, and a loopback-only port. Adds an nginx site with TLS, HSTS and a per-address rate limit, gets a certificate, and registers every gateway with its public key and agent token (the token goes in on standard input, never on a command line). |
| `peer_sync` | all | The agent, its root-only token and settings, and a systemd service that keeps only `CAP_NET_ADMIN` and runs under a strict sandbox. Before starting it, the role runs it once against the live API so a bad token or TLS chain fails the play. |

## Security notes

- Secrets on the control machine: the vault is encrypted, its password is in the keyring, the
  inventory directory is `0700`. Nothing secret is in this repository; `git grep` for your domain
  and addresses before every push.
- Tokens: 256 bits from the operating system's CSPRNG, stored as SHA-256 hashes, sent only in the
  `Authorization` header over TLS. Device tokens can do nothing but manage that device's own
  registration; gateway tokens can read only that gateway's peer list.
- A compromised API cannot take over a gateway: it has no credential for one, and the agent
  refuses any peer whose addresses are not single hosts in the gateway's tunnel range.
- The client's kill switch is still routing only, not a firewall rule. See the main README.
