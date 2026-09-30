# Takes the README screenshots on a virtual display inside a container, so nothing on the
# host's screen ends up in them and the host's routing is untouched. Build the client image
# first: docker build -t vpn-control-plane-desktop -f tools/desktop-container/Dockerfile .
FROM vpn-control-plane-desktop
RUN apt-get update \
 && apt-get install -y --no-install-recommends xvfb xdotool imagemagick curl \
 && rm -rf /var/lib/apt/lists/*
COPY shoot.sh /shoot.sh
ENTRYPOINT ["/shoot.sh"]
