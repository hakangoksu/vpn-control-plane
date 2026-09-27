using System.Globalization;
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
    /// <summary>Maps the peer registration endpoints behind the API key filter.</summary>
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
            .AddEndpointFilter<ApiKeyEndpointFilter>();

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
    /// <param name="request">Gateway identifier and client public key.</param>
    /// <param name="dbContext">Database session for this request.</param>
    /// <param name="options">Address range, DNS and keepalive settings.</param>
    /// <param name="logger">Destination for registration diagnostics.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>201 with the peer configuration, or a problem response.</returns>
    private static async Task<IResult> RegisterPeerAsync(
        PeerRegistrationRequest request,
        ControlPlaneDbContext dbContext,
        IOptions<ControlPlaneOptions> options,
        ILogger<ControlPlaneDbContext> logger,
        CancellationToken cancellationToken)
    {
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

        List<int> usedIndexes = await dbContext.Peers
            .Where(p => p.ServerId == server.Id)
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
            PublicKey = request.PublicKey,
            AddressIndex = addressIndex,
            AssignedAddress = FormatAddress(settings.AddressPrefix, addressIndex),
            DeviceName = request.DeviceName,
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
            "Registered peer {PeerId} on {ServerId} with address {Address}.",
            peer.Id,
            server.Id,
            peer.AssignedAddress);

        var configuration = new PeerConfiguration
        {
            PeerId = peer.Id,
            ServerId = server.Id,
            AssignedAddress = peer.AssignedAddress,
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

    /// <summary>Releases a registration.</summary>
    /// <param name="id">Peer identifier returned at registration.</param>
    /// <param name="dbContext">Database session for this request.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>204 when a registration was removed, 404 when there was none.</returns>
    private static async Task<IResult> UnregisterPeerAsync(
        string id,
        ControlPlaneDbContext dbContext,
        CancellationToken cancellationToken)
    {
        PeerRecord? peer = await dbContext.Peers
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
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

    /// <summary>
    /// Renders an address index as a /32 inside the configured prefix.
    /// </summary>
    /// <param name="prefix">First two octets, for example <c>10.99</c>.</param>
    /// <param name="index">Address index, counting from 1.</param>
    /// <returns>The address in CIDR form.</returns>
    /// <remarks>
    /// A /32 is handed out rather than the whole subnet, because a client should route only
    /// its own address onto the interface. Giving each client a /16 would have every one of
    /// them believing it owns the entire range.
    /// <para>
    /// The last octet skips 0 and 255, which are the network and broadcast addresses of the
    /// /24 they sit in, so 254 usable addresses fit in each.
    /// </para>
    /// </remarks>
    private static string FormatAddress(string prefix, int index)
    {
        int thirdOctet = index / 254;
        int fourthOctet = (index % 254) + 1;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}.{thirdOctet}.{fourthOctet}/32");
    }
}
