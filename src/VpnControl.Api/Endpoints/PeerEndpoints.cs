using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VpnControl.Api.Configuration;
using VpnControl.Api.Data;
using VpnControl.Api.Security;
using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;

namespace VpnControl.Api.Endpoints;

/// <summary>
/// The writing half of the control plane: admitting a client key to a gateway and
/// releasing it again.
/// </summary>
public static class PeerEndpoints
{
    /// <summary>Maps the peer registration endpoints behind the device token filter.</summary>
    /// <param name="routes">Route builder to add to.</param>
    /// <returns>The route builder, so calls can be chained.</returns>
    public static IEndpointRouteBuilder MapPeerEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // The filter is attached to the group, not to each endpoint. Adding a third peer
        // endpoint later then cannot forget it, which is the usual way an endpoint ends up
        // unprotected.
        RouteGroupBuilder group = routes.MapGroup("/api/peers")
            .WithTags("Peers")
            .AddEndpointFilter<DeviceTokenEndpointFilter>();

        group.MapPost("/", RegisterPeerAsync)
            .WithName("RegisterPeer")
            .WithSummary("Registers a client public key with a gateway and returns its tunnel parameters.")
            .Produces<PeerConfiguration>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id}", UnregisterPeerAsync)
            .WithName("UnregisterPeer")
            .WithSummary("Releases a peer registration.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>Admits a public key to a gateway and reserves an address for it.</summary>
    /// <remarks>
    /// A device holds one registration at a time. Registering again, on the same gateway or
    /// another, releases whatever the device held before. The client runs one tunnel, so an
    /// older registration can only belong to a session that ended without releasing it, a
    /// crash or a lost network, and leaving it would keep a key admitted that nothing will
    /// ever use. It also caps what a stolen device token can consume at one address.
    /// </remarks>
    /// <param name="request">Gateway identifier and client public key.</param>
    /// <param name="httpContext">Current request, carrying the authenticated device.</param>
    /// <param name="dbContext">Database session for this request.</param>
    /// <param name="options">Address range, DNS and keepalive settings.</param>
    /// <param name="logger">Destination for registration diagnostics.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>201 with the peer configuration, or a problem response.</returns>
    private static async Task<IResult> RegisterPeerAsync(
        PeerRegistrationRequest request,
        HttpContext httpContext,
        ControlPlaneDbContext dbContext,
        IOptions<ControlPlaneOptions> options,
        ILogger<ControlPlaneDbContext> logger,
        CancellationToken cancellationToken)
    {
        DeviceRecord device = DeviceTokenEndpointFilter.GetDevice(httpContext);
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.ServerId))
        {
            errors["serverId"] = ["A gateway identifier is required."];
        }

        // The key is checked for shape only. Nothing here can prove the caller holds the
        // matching private key; only a completed handshake at the gateway can do that.
        if (!WireGuardKeyPair.IsValidKey(request.PublicKey))
        {
            errors["publicKey"] = ["A WireGuard public key is 32 bytes, base64 encoded."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        ServerRecord? server = await dbContext.Servers
            .FirstOrDefaultAsync(s => s.Id == request.ServerId, cancellationToken)
            .ConfigureAwait(false);

        if (server is null)
        {
            return Results.Problem(
                title: "Gateway not found.",
                detail: $"No gateway with id '{request.ServerId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (!server.IsEnabled)
        {
            return Results.Problem(
                title: "Gateway is not accepting peers.",
                detail: $"Gateway '{server.Id}' is currently disabled.",
                statusCode: StatusCodes.Status409Conflict);
        }

        List<PeerRecord> previous = await dbContext.Peers
            .Where(p => p.DeviceId == device.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        dbContext.Peers.RemoveRange(previous);

        // The device's previous address on this gateway counts as free, because the same
        // SaveChanges call that inserts the new row deletes the old one.
        List<int> usedIndexes = await dbContext.Peers
            .Where(p => p.ServerId == server.Id && p.DeviceId != device.Id)
            .Select(p => p.AddressIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        ControlPlaneOptions settings = options.Value;

        if (usedIndexes.Count >= settings.MaxPeersPerServer)
        {
            return Results.Problem(
                title: "Gateway is full.",
                detail: $"Gateway '{server.Id}' already holds {usedIndexes.Count} peers.",
                statusCode: StatusCodes.Status409Conflict);
        }

        int addressIndex = FirstFreeIndex(usedIndexes);

        var peer = new PeerRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            ServerId = server.Id,
            DeviceId = device.Id,
            PublicKey = request.PublicKey,
            AddressIndex = addressIndex,
            AssignedAddress = PeerAddressing.FormatIpv4(settings.AddressPrefix, addressIndex),
            AssignedAddressV6 = PeerAddressing.FormatIpv6(settings.Ipv6Prefix, addressIndex),
            DeviceName = device.Name,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Peers.Add(peer);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // The unique indexes are what actually prevent a duplicate key or a duplicate
            // address, and they fire here rather than in the checks above, because two
            // concurrent requests can both read the same free index before either writes.
            logger.LogWarning(ex, "Rejected a duplicate registration on {ServerId}.", server.Id);

            return Results.Problem(
                title: "Registration conflicts with an existing peer.",
                detail: "That public key is already registered on this gateway, or the address was taken concurrently. Retry.",
                statusCode: StatusCodes.Status409Conflict);
        }

        logger.LogInformation(
            "Registered peer {PeerId} for device {DeviceId} on {ServerId} with address {Address}.",
            peer.Id,
            device.Id,
            server.Id,
            peer.AssignedAddress);

        var configuration = new PeerConfiguration
        {
            PeerId = peer.Id,
            ServerId = server.Id,
            AssignedAddress = peer.AssignedAddress,
            AssignedAddressV6 = peer.AssignedAddressV6,
            ServerPublicKey = server.PublicKey,
            Endpoint = $"{server.EndpointHost}:{server.EndpointPort}",
            AllowedIps = [.. settings.AllowedIps],
            DnsServers = [.. settings.DnsServers],
            PersistentKeepaliveSeconds = settings.PersistentKeepaliveSeconds > 0
                ? settings.PersistentKeepaliveSeconds
                : null,
        };

        return Results.Created($"/api/peers/{peer.Id}", configuration);
    }

    /// <summary>Releases a registration the calling device owns.</summary>
    /// <remarks>
    /// A registration that exists but belongs to another device gets the same 404 as one
    /// that does not exist, so a caller cannot use this endpoint to discover other devices'
    /// peer identifiers.
    /// </remarks>
    /// <param name="id">Peer identifier returned at registration.</param>
    /// <param name="httpContext">Current request, carrying the authenticated device.</param>
    /// <param name="dbContext">Database session for this request.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>204 when a registration was removed, 404 when there was none.</returns>
    private static async Task<IResult> UnregisterPeerAsync(
        string id,
        HttpContext httpContext,
        ControlPlaneDbContext dbContext,
        CancellationToken cancellationToken)
    {
        DeviceRecord device = DeviceTokenEndpointFilter.GetDevice(httpContext);

        PeerRecord? peer = await dbContext.Peers
            .FirstOrDefaultAsync(p => p.Id == id && p.DeviceId == device.Id, cancellationToken)
            .ConfigureAwait(false);

        if (peer is null)
        {
            return Results.Problem(
                title: "Peer not found.",
                detail: $"No peer registration with id '{id}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        dbContext.Peers.Remove(peer);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// Finds the lowest address index not already in use.
    /// </summary>
    /// <param name="usedIndexes">Indexes currently held on the gateway.</param>
    /// <returns>The lowest free index, counting from 1.</returns>
    /// <remarks>
    /// Reusing the lowest free index rather than always incrementing keeps the pool compact,
    /// so a gateway that has been up for a long time does not run out of a /16 while holding
    /// twenty peers. Index 0 is skipped because that address belongs to the gateway itself.
    /// </remarks>
    private static int FirstFreeIndex(List<int> usedIndexes)
    {
        var used = new HashSet<int>(usedIndexes);

        int candidate = 1;
        while (used.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }
}
