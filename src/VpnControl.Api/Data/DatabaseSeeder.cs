using Microsoft.EntityFrameworkCore;
using VpnControl.Core.Servers;

namespace VpnControl.Api.Data;

/// <summary>
/// Brings the database schema up to date and, when asked, fills it with the fictional gateways.
/// </summary>
/// <remarks>
/// The schema is managed with EF Core migrations. A deployment keeps its database across
/// upgrades, and <c>EnsureCreated</c> cannot alter an existing schema: it would silently leave
/// the old one in place and the first query against a new column would fail. Migrations apply
/// exactly the changes the database has not seen yet.
/// <para>
/// Applying them at startup suits a single instance, which is what this deployment runs. With
/// several instances starting together, two could race to apply the same migration, and the
/// step would move into the deployment pipeline instead.
/// </para>
/// </remarks>
public static class DatabaseSeeder
{
    /// <summary>Applies pending migrations and seeds the demo catalog if requested.</summary>
    /// <remarks>
    /// Migrations are applied only when some are pending. Applying them takes a lock that EF
    /// Core stores as a row in the database, and on SQLite that row outlives a process
    /// killed while holding it: every later start then waits on it forever. A deployment
    /// once restarted the container at exactly that moment and the API stopped answering.
    /// Checking first means the lock is taken only on the start that actually upgrades the
    /// schema, not on every start.
    /// </remarks>
    /// <param name="dbContext">Context to migrate and seed.</param>
    /// <param name="seedDemoCatalog">
    /// Whether to insert the fictional gateways into an empty catalog. Off outside development,
    /// so a real deployment never advertises a gateway that does not exist.
    /// </param>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns>Number of gateways inserted, which is zero whenever nothing was seeded.</returns>
    public static async Task<int> MigrateAndSeedAsync(
        ControlPlaneDbContext dbContext,
        bool seedDemoCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        IEnumerable<string> pending = await dbContext.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (pending.Any())
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!seedDemoCatalog || await dbContext.Servers.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        foreach (VpnServer server in DemoCatalog.CreateServers())
        {
            dbContext.Servers.Add(new ServerRecord
            {
                Id = server.Id,
                Name = server.Name,
                City = server.City,
                Country = server.Country,
                EndpointHost = server.EndpointHost,
                EndpointPort = server.EndpointPort,
                PublicKey = server.PublicKey,
                IsEnabled = server.IsEnabled,
                Ipv6Egress = server.Ipv6Egress,
            });
        }

        return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
