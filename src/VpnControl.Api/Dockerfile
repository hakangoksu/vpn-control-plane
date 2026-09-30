# Build context is the repository root, because the API references VpnControl.Core:
#   docker build -f src/VpnControl.Api/Dockerfile -t vpn-control-plane-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so the restore layer is reused until a dependency changes.
COPY global.json Directory.Build.props .editorconfig ./
COPY src/VpnControl.Core/VpnControl.Core.csproj src/VpnControl.Core/
COPY src/VpnControl.Api/VpnControl.Api.csproj src/VpnControl.Api/
RUN dotnet restore src/VpnControl.Api/VpnControl.Api.csproj

COPY src/VpnControl.Core/ src/VpnControl.Core/
COPY src/VpnControl.Api/ src/VpnControl.Api/
RUN dotnet publish src/VpnControl.Api/VpnControl.Api.csproj \
      --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The database lives on a volume so it survives an image upgrade. The directory is owned
# by the unprivileged user the image already defines, which the process runs as.
RUN mkdir /data && chown app:app /data
VOLUME /data

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__ControlPlane="Data Source=/data/controlplane.db" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "VpnControl.Api.dll"]
