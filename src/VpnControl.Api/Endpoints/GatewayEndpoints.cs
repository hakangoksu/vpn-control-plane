using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VpnControl.Api.Data;
using VpnControl.Api.Security;

namespace VpnControl.Api.Endpoints;

/// <summary>
/// The endpoint a gateway's sync agent polls to learn which peers it should admit.
/// </summary>
/// <remarks>
/// The gateway pulls rather than the control plane pushing. The control plane then holds no
/// credential for any gateway, needs no inbound access to them, and a compromise of the API
/// cannot be turned into a shell on a gateway. The cost is that a new peer becomes active
/// on the next poll rather than immediately.
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

    /// <summary>Lists the calling gateway's peers.</summary>
    /// <param name="httpContext">Current request, carrying the authenticated gateway.</param>
    /// <param name="dbContext">Database session for this request.</param>
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
        CancellationToken cancellationToken)
    {
        ServerRecord gateway = GatewayTokenEndpointFilter.GetGateway(httpContext);

        List<PeerRecord> peers = await dbContext.Peers
            .AsNoTracking()
            .Where(p => p.ServerId == gateway.Id)
            .OrderBy(p => p.AddressIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new GatewayPeerList
        {
            GatewayId = gateway.Id,
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
