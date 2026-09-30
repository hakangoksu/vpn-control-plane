#!/bin/sh
# Prints the Ansible Vault password from the desktop login keyring (libsecret).
#
# Keeping the vault password in the keyring rather than in a file next to the vault means
# the encrypted secrets and the key that opens them never sit on disk together in plain
# form. Store it once with:
#   openssl rand -base64 48 | tr -d '\n' | secret-tool store --label="vpn-control-plane ansible vault" service vpn-control-plane key ansible-vault
exec secret-tool lookup service vpn-control-plane key ansible-vault
