# The desktop client in a container with its own network namespace and CAP_NET_ADMIN.
# A real WireGuard tunnel comes up inside the container; the host's routing is untouched.
# The window is drawn on the host's X server (or XWayland) through the mounted socket.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/VpnControl.Desktop/VpnControl.Desktop.csproj \
      --configuration Release --runtime linux-x64 --self-contained true --output /app

FROM debian:trixie-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      wireguard-tools iproute2 nftables openresolv iputils-ping ca-certificates \
      libx11-6 libice6 libsm6 libfontconfig1 fonts-noto-core libicu76 \
      libxrandr2 libxi6 libxcursor1 libxext6 \
 && rm -rf /var/lib/apt/lists/*
# /proc/sys is read-only in a container; `docker run --sysctl` sets src_valid_mark instead.
RUN printf '#!/bin/sh\nexit 0\n' > /usr/local/sbin/sysctl && chmod +x /usr/local/sbin/sysctl
COPY --from=build /app /app
WORKDIR /app
ENTRYPOINT ["/app/VpnControl.Desktop"]
