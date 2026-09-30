using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VpnControl.Api.Data;
using VpnControl.Api.Security;

namespace VpnControl.Api.Endpoints;

/// <summary>
/// The endpoint a gateway's sync agent polls to learn which peers it should admit.
/// </summary>
/// <remarks>
/// The request is a long poll: the agent says which version it last applied, and the
/// request is held until a newer one exists or <c>wait</c> seconds pass. The same number is
/// the agent's acknowledgement of what it has applied; see <see cref="GatewaySyncCoordinator"/>.
/// <para>
/// The gateway pulls rather than the control plane pushing. The control plane then holds no
/// credential for any gateway, needs no inbound access to them, and a compromise of the API
/// cannot be turned into a shell on a gateway. The cost is that a new peer becomes active
/// on the next poll rather than immediately.
/// </para>
/// </remarks>
public static class GatewayEndpoints
{
    /// <summary>Maps <c>GET /api/gateway/peers</c> behind the gateway token filter.</summary>
    /// <param name="routes">Route builder to add to.</param>
    /// <returns>The route builder, so calls can be chained.</returns>
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup("/api/gateway")
            .WithTags("Gateway")
            .AddEndpointFilter<GatewayTokenEndpointFilter>();

        group.MapGet("/peers", GetPeersAsync)
            .WithName("GetGatewayPeers")
            .WithSummary("Returns the peers the calling gateway should admit.")
            .Produces<GatewayPeerList>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return routes;
    }

    /// <summary>Longest a poll may be held, kept below the reverse proxy's read timeout.</summary>
    private const int MaxWaitSeconds = 10;

    /// <summary>Lists the calling gateway's peers.</summary>
    /// <param name="httpContext">Current request, carrying the authenticated gateway.</param>
    /// <param name="dbContext">Database session for this request.</param>
    /// <param name="coordinator">Version tracking and the long-poll signal.</param>
    /// <param name="applied">Version the agent applied last, or 0 on its first request.</param>
    /// <param name="wait">Seconds to hold the request if nothing is newer than <paramref name="applied"/>.</param>
    /// <param name="cancellationToken">Cancelled when the agent disconnects.</param>
    /// <returns>The complete peer list, which the agent applies as a replacement.</returns>
    /// <remarks>
    /// The complete list rather than a diff since the last poll. A missed poll, a restart or
    /// a hand edit on the gateway is then corrected by the next successful one, with no
    /// state to reconcile. At a few hundred peers the payload is a few tens of kilobytes.
    /// </remarks>
    private static async Task<IResult> GetPeersAsync(
        HttpContext httpContext,
        ControlPlaneDbContext dbContext,
        GatewaySyncCoordinator coordinator,
        long? applied,
        int? wait,
        CancellationToken cancellationToken)
    {
        ServerRecord gateway = GatewayTokenEndpointFilter.GetGateway(httpContext);
        long appliedVersion = Math.Max(applied ?? 0, 0);

        coordinator.RecordApplied(gateway.Id, appliedVersion);

        int waitSeconds = Math.Clamp(wait ?? 0, 0, MaxWaitSeconds);
        if (waitSeconds > 0)
        {
            await coordinator.WaitForChangeAsync(
                gateway.Id,
                appliedVersion,
                TimeSpan.FromSeconds(waitSeconds),
                cancellationToken).ConfigureAwait(false);
        }

        // Read the version before the rows. A change is saved before its version is
        // published, so rows read after this point are at least as new as the number sent.
        long version = coordinator.CurrentVersion(gateway.Id);

        List<PeerRecord> peers = await dbContext.Peers
            .AsNoTracking()
            .Where(p => p.ServerId == gateway.Id)
            .OrderBy(p => p.AddressIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new GatewayPeerList
        {
            GatewayId = gateway.Id,
            Version = version,
            Peers = peers
                .Select(p => new GatewayPeer
                {
                    PublicKey = p.PublicKey,
                    AllowedIps = p.AssignedAddressV6 is null
                        ? [p.AssignedAddress]
                        : [p.AssignedAddress, p.AssignedAddressV6],
                })
                .ToList(),
        });
    }
}

/// <summary>Response of <c>GET /api/gateway/peers</c>.</summary>
public sealed record GatewayPeerList
{
    /// <summary>Gateway the list is for, so the agent can check it asked the right question.</summary>
    [JsonPropertyName("gatewayId")]
    public required string GatewayId { get; init; }

    /// <summary>
    /// Version of this list. The agent sends it back as <c>applied</c> once the list is in
    /// place, which is how the control plane learns the gateway has admitted the peers.
    /// </summary>
    [JsonPropertyName("version")]
    public required long Version { get; init; }

    /// <summary>Every peer the gateway should admit. Anything else on the interface is removed.</summary>
    [JsonPropertyName("peers")]
    public required IReadOnlyList<GatewayPeer> Peers { get; init; }
}

/// <summary>One peer as the gateway sees it.</summary>
public sealed record GatewayPeer
{
    /// <summary>Base64 X25519 public key of the client.</summary>
    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; init; }

    /// <summary>
    /// The client's tunnel addresses, one host prefix per family. On the gateway these are
    /// the source addresses the peer may use and the destinations routed back to it.
    /// </summary>
    [JsonPropertyName("allowedIps")]
    public required IReadOnlyList<string> AllowedIps { get; init; }
}
