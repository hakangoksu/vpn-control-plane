#!/bin/bash
# Runs inside the screenshot container: a virtual display, the real client, scripted clicks,
# and one screenshot per state, written to /out. The clicks assume the window's default
# layout: the second location is selected first, then the first one.
#
#   docker build -t vpn-desktop-shots tools/screenshots
#   docker run --rm --cap-add NET_ADMIN \
#     --sysctl net.ipv4.conf.all.src_valid_mark=1 --sysctl net.ipv6.conf.all.disable_ipv6=0 \
#     -v ~/.config/vpn-control-plane/client.json:/root/.config/vpn-control-plane/client.json:ro \
#     -v "$PWD/shots":/out vpn-desktop-shots
set -u
Xvfb :99 -screen 0 1000x680x24 -nolisten tcp >/dev/null 2>&1 &
export DISPLAY=:99
sleep 1
/app/VpnControl.Desktop >/out/client.log 2>&1 &
for i in $(seq 1 60); do xdotool search --name "VPN Control Plane" >/dev/null 2>&1 && break; sleep 0.5; done
WID=$(xdotool search --name "VPN Control Plane" | head -1)
xdotool windowmove $WID 0 0; xdotool windowsize $WID 1000 680
shot() { import -window root "/out/$1.png"; echo "shot $1"; }
click() { xdotool mousemove "$1" "$2" click 1; }
traffic() { curl -s -o /dev/null --max-time 20 "https://speed.cloudflare.com/__down?bytes=$1" || true; }
until grep -q "Refreshed catalog" /out/client.log; do sleep 0.5; done; sleep 2
xdotool mousemove 999 679
shot 01-disconnected
click 500 149; sleep 0.6; click 179 158; sleep 0.25; xdotool mousemove 999 679; shot 02-connecting
sleep 5; traffic 25000000; sleep 3; xdotool mousemove 999 679; shot 03-connected
click 500 96; sleep 0.8; xdotool mousemove 999 679; shot 04-switch-offered
click 179 343; sleep 0.25; xdotool mousemove 999 679; shot 05-switching
sleep 6; traffic 5000000; click 298 605; sleep 7; traffic 3000000; sleep 2; xdotool mousemove 999 679; shot 06-kill-switch
click 500 640; sleep 1; xdotool mousemove 999 679; shot 07-diagnostics
