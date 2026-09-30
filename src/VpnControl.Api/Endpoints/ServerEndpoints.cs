using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VpnControl.Api.Configuration;
using VpnControl.Api.Data;
using VpnControl.Api.Security;
using VpnControl.Core.Servers;

namespace VpnControl.Api.Endpoints;

/// <summary>
/// The read-only half of the control plane: which gateways exist.
/// </summary>
/// <remarks>
/// Endpoints are grouped into an extension method per area rather than left in
/// <c>Program.cs</c>. A minimal API is pleasant until the startup file is four hundred lines
/// long, and splitting by area keeps each group readable and testable on its own.
/// <para>
/// The catalog needs a device token like the peer endpoints do. Every client is enrolled
/// before it can connect anyway, so requiring the token costs a legitimate client nothing,
/// and it keeps the list of gateways and their keys away from anyone who merely found the
/// API's address.
/// </para>
/// </remarks>
public static class ServerEndpoints
{
    /// <summary>Maps the gateway catalog endpoints.</summary>
    /// <param name="routes">Route builder to add to.</param>
    /// <returns>The route builder, so calls can be chained.</returns>
    public static IEndpointRouteBuilder MapServerEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup("/api/servers")
            .WithTags("Servers")
            .AddEndpointFilter<DeviceTokenEndpointFilter>();

        group.MapGet("/", GetServersAsync)
            .WithName("GetServers")
            .WithSummary("Lists the gateways, optionally filtered by country or city.")
            .Produces<IReadOnlyList<VpnServer>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/{id}", GetServerAsync)
            .WithName("GetServer")
            .WithSummary("Returns one gateway by identifier.")
            .Produces<VpnServer>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>Lists gateways, filtered by country and city when asked.</summary>
    /// <param name="dbContext">Database session for this request.</param>
    /// <param name="options">Pool size the load figure is computed against.</param>
    /// <param name="country">Optional ISO 3166-1 alpha-2 country code.</param>
    /// <param name="city">Optional city name.</param>
    /// <param name="cancellationToken">
    /// Supplied by ASP.NET Core and cancelled when the client disconnects, so a query for a
    /// response nobody will read does not keep running.
    /// </param>
    /// <returns>The matching gateways, or a validation problem.</returns>
    private static async Task<IResult> GetServersAsync(
        ControlPlaneDbContext dbContext,
        IOptions<ControlPlaneOptions> options,
        string? country,
        string? city,
        CancellationToken cancellationToken)
    {
        if (country is not null && country.Length != 2)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["country"] = ["A country filter must be a two letter ISO 3166-1 alpha-2 code."],
            });
        }

        IQueryable<ServerRecord> query = dbContext.Servers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(country))
        {
            // EF.Functions.Like is used rather than string.Equals with a comparer, because a
            // comparison SQLite cannot translate would be evaluated in memory after loading
            // every row, which works in a test and falls over on a real table.
            query = query.Where(s => EF.Functions.Like(s.Country, country));
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            query = query.Where(s => EF.Functions.Like(s.City, city));
        }

        List<ServerRecord> servers = await query
            .OrderBy(s => s.Country)
            .ThenBy(s => s.City)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, int> peerCounts = await CountPeersAsync(dbContext, cancellationToken).ConfigureAwait(false);
        int capacity = options.Value.MaxPeersPerServer;

        return Results.Ok(servers
            .Select(s => s.ToContract(LoadPercent(peerCounts.GetValueOrDefault(s.Id), capacity)))
            .ToList());
    }

    /// <summary>Returns one gateway.</summary>
    /// <param name="dbContext">Database session for this request.</param>
    /// <param name="options">Pool size the load figure is computed against.</param>
    /// <param name="id">Gateway identifier.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>The gateway, or a 404 problem response.</returns>
    private static async Task<IResult> GetServerAsync(
        ControlPlaneDbContext dbContext,
        IOptions<ControlPlaneOptions> options,
        string id,
        CancellationToken cancellationToken)
    {
        ServerRecord? server = await dbContext.Servers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (server is null)
        {
            return Results.Problem(
                title: "Gateway not found.",
                detail: $"No gateway with id '{id}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        int peers = await dbContext.Peers
            .CountAsync(p => p.ServerId == server.Id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(server.ToContract(LoadPercent(peers, options.Value.MaxPeersPerServer)));
    }

    /// <summary>Counts registrations per gateway in one grouped query.</summary>
    private static Task<Dictionary<string, int>> CountPeersAsync(
        ControlPlaneDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.Peers
            .GroupBy(p => p.ServerId)
            .Select(g => new { ServerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ServerId, x => x.Count, cancellationToken);

    /// <summary>
    /// Expresses registrations as a share of the gateway's address pool.
    /// </summary>
    /// <remarks>
    /// This is occupancy, not CPU or bandwidth. It is the one load figure the control plane
    /// knows for certain, and it is labelled as what it is rather than presented as a
    /// measurement of how busy the machine is.
    /// </remarks>
    private static int LoadPercent(int peers, int capacity) =>
        capacity <= 0 ? 0 : Math.Clamp(peers * 100 / capacity, 0, 100);
}
