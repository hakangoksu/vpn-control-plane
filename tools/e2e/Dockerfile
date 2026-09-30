# A throwaway WireGuard client for end-to-end checks and measurements.
# It runs in its own network namespace, so the machine running it keeps its routing.
FROM debian:trixie-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      wireguard-tools iproute2 nftables openresolv iputils-ping \
      curl ca-certificates dnsutils jq procps \
 && rm -rf /var/lib/apt/lists/*
# /proc/sys is read-only inside a container. wg-quick sets src_valid_mark itself, which
# `docker run --sysctl` has already done, so its sysctl call is made a no-op.
RUN printf '#!/bin/sh\nexit 0\n' > /usr/local/sbin/sysctl && chmod +x /usr/local/sbin/sysctl
COPY client.sh /usr/local/bin/vpn-e2e
ENTRYPOINT ["/usr/local/bin/vpn-e2e"]
