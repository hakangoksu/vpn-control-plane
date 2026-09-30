#!/bin/sh
# Runs the desktop client.
#
#   ./client.sh          real tunnel, using the device token setup.sh wrote
#   ./client.sh --demo   simulated tunnel and fictional gateways, no servers needed
#
# The client runs in a container with its own network namespace, so a real tunnel does not
# change this machine's routing, and nothing needs root or a .NET SDK on the host.
set -eu
cd "$(dirname "$0")"

settings="src/VpnControl.Desktop/appsettings.Local.json"
mode=real
[ "${1:-}" = "--demo" ] && mode=demo

if [ "$mode" = real ] && [ ! -f "$settings" ]; then
  echo "No device token yet. Run ./setup.sh first, or ./client.sh --demo to try it without servers." >&2
  exit 1
fi

if [ -z "${DISPLAY:-}" ]; then
  echo "No X display found (DISPLAY is empty). On Wayland, XWayland provides one." >&2
  exit 1
fi

docker build -q -t vpn-control-plane-desktop -f tools/desktop-container/Dockerfile . >/dev/null

set -- --rm --name vpn-control-plane-desktop \
  --cap-add NET_ADMIN \
  --sysctl net.ipv4.conf.all.src_valid_mark=1 --sysctl net.ipv6.conf.all.disable_ipv6=0 \
  -e DISPLAY="$DISPLAY" -v /tmp/.X11-unix:/tmp/.X11-unix:ro

if [ -n "${XAUTHORITY:-}" ] && [ -f "$XAUTHORITY" ]; then
  set -- "$@" -e XAUTHORITY=/tmp/.Xauthority -v "$XAUTHORITY:/tmp/.Xauthority:ro"
fi

if [ "$mode" = real ]; then
  set -- "$@" -v "$PWD/$settings:/app/appsettings.Local.json:ro"
fi

exec docker run "$@" vpn-control-plane-desktop
