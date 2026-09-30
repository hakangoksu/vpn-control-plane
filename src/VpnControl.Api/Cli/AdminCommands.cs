using Microsoft.EntityFrameworkCore;
using VpnControl.Api.Data;
using VpnControl.Api.Security;
using VpnControl.Core.Crypto;

namespace VpnControl.Api.Cli;

/// <summary>
/// The operator's actions: enrolling and revoking devices, registering gateways.
/// </summary>
/// <remarks>
/// These run from a shell on the control plane host, not over HTTP. The API then has no
/// administrative endpoint at all, so there is no admin credential to leak and no admin
/// route to attack. Reaching these commands already requires a shell on the host, which is
/// a stronger check than anything the API could add.
/// <para>
/// Separated from the argument parsing in <see cref="AdminCli"/> so the tests can call them
/// directly against a database.
/// </para>
/// </remarks>
/// <param name="dbContext">Database the commands act on.</param>
/// <param name="timeProvider">Clock for creation and revocation stamps.</param>
public sealed class AdminCommands(ControlPlaneDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Enrolls a device and returns its token.</summary>
    /// <param name="name">Label for the device.</param>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns>The new device and its token. The token is not stored and cannot be shown again.</returns>
    public async Task<(DeviceRecord Device, string Token)> AddDeviceAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string token = AccessTokens.Create(AccessTokens.DevicePrefix);
        var device = new DeviceRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name.Trim(),
            TokenHash = AccessTokens.Hash(token),
            CreatedAt = timeProvider.GetUtcNow(),
        };

        dbContext.Devices.Add(device);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (device, token);
    }

    /// <summary>Lists devices with the number of registrations each holds.</summary>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns>Devices, oldest first.</returns>
    public async Task<IReadOnlyList<(DeviceRecord Device, int Peers)>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.Devices
            .AsNoTracking()
            .Select(d => new { Device = d, Peers = d.Peers.Count })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered after loading: SQLite cannot sort on a DateTimeOffset column in SQL.
        return rows
            .OrderBy(r => r.Device.CreatedAt)
            .Select(r => (r.Device, r.Peers))
            .ToList();
    }

    /// <summary>
    /// Revokes a device and deletes every registration it holds.
    /// </summary>
    /// <param name="deviceId">Device to revoke.</param>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns>Number of registrations removed, or <c>null</c> when no such device exists.</returns>
    /// <remarks>
    /// The registrations are deleted in the same transaction that marks the device revoked.
    /// The token stops working on the next request, and each gateway drops the device's
    /// peer on its next sync, so an open tunnel stops passing traffic within one poll
    /// interval rather than lasting until the client disconnects of its own accord.
    /// </remarks>
    public async Task<int?> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        DeviceRecord? device = await dbContext.Devices
            .Include(d => d.Peers)
            .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            .ConfigureAwait(false);

        if (device is null)
        {
            return null;
        }

        int removed = device.Peers.Count;
        dbContext.Peers.RemoveRange(device.Peers);
        device.RevokedAt ??= timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    /// <summary>Creates a gateway or updates the one with the same identifier.</summary>
    /// <param name="gateway">Gateway fields. <see cref="ServerRecord.AgentTokenHash"/> is ignored.</param>
    /// <param name="agentToken">
    /// Token the gateway's sync agent will present. Passed in rather than generated, so the
    /// deployment tooling that installs the agent owns the value and this call can be
    /// repeated without rotating it.
    /// </param>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns><c>true</c> when the gateway was created, <c>false</c> when it was updated.</returns>
    /// <exception cref="ArgumentException">A field is missing or malformed.</exception>
    public async Task<bool> UpsertGatewayAsync(ServerRecord gateway, string agentToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        Validate(gateway, agentToken);

        ServerRecord? existing = await dbContext.Servers
            .FirstOrDefaultAsync(s => s.Id == gateway.Id, cancellationToken)
            .ConfigureAwait(false);

        string tokenHash = AccessTokens.Hash(agentToken);

        if (existing is null)
        {
            gateway.AgentTokenHash = tokenHash;
            dbContext.Servers.Add(gateway);
        }
        else
        {
            existing.Name = gateway.Name;
            existing.City = gateway.City;
            existing.Country = gateway.Country;
            existing.EndpointHost = gateway.EndpointHost;
            existing.EndpointPort = gateway.EndpointPort;
            existing.PublicKey = gateway.PublicKey;
            existing.IsEnabled = gateway.IsEnabled;
            existing.Ipv6Egress = gateway.Ipv6Egress;
            existing.AgentTokenHash = tokenHash;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return existing is null;
    }

    /// <summary>Lists gateways.</summary>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns>Gateways ordered by identifier.</returns>
    public async Task<IReadOnlyList<ServerRecord>> ListGatewaysAsync(CancellationToken cancellationToken) =>
        await dbContext.Servers
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Deletes a gateway and its registrations.</summary>
    /// <param name="gatewayId">Gateway to delete.</param>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns><c>true</c> when a gateway was deleted.</returns>
    public async Task<bool> RemoveGatewayAsync(string gatewayId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);

        int deleted = await dbContext.Servers
            .Where(s => s.Id == gatewayId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return deleted > 0;
    }

    private static void Validate(ServerRecord gateway, string agentToken)
    {
        if (string.IsNullOrWhiteSpace(gateway.Id) || gateway.Id.Length > 64)
        {
            throw new ArgumentException("A gateway id of at most 64 characters is required.", nameof(gateway));
        }

        if (gateway.Country is not { Length: 2 } || !gateway.Country.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("The country must be an upper case ISO 3166-1 alpha-2 code.", nameof(gateway));
        }

        if (Uri.CheckHostName(gateway.EndpointHost) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("The endpoint host is not a valid host name or address.", nameof(gateway));
        }

        if (gateway.EndpointPort is < 1 or > 65535)
        {
            throw new ArgumentException("The endpoint port must be between 1 and 65535.", nameof(gateway));
        }

        if (!WireGuardKeyPair.IsValidKey(gateway.PublicKey))
        {
            throw new ArgumentException("The public key is not a 32 byte base64 value.", nameof(gateway));
        }

        if (!AccessTokens.HasShape(agentToken, AccessTokens.GatewayPrefix))
        {
            throw new ArgumentException($"The agent token must be a {AccessTokens.GatewayPrefix} token.", nameof(agentToken));
        }
    }
}
