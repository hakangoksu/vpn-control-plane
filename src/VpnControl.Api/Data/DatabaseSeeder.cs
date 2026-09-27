using Microsoft.EntityFrameworkCore;
using VpnControl.Core.Servers;

namespace VpnControl.Api.Data;

/// <summary>
/// Creates the database if it is missing and puts the fictional gateways in it.
/// </summary>
/// <remarks>
/// Uses <c>EnsureCreatedAsync</c> rather than EF Core migrations. That is the right choice
/// here and the wrong one for a real service, so it is worth being explicit about why: this
/// schema has no history to preserve, the database is disposable, and a migrations folder
/// would add ceremony to a project whose subject is not schema evolution. A service with
/// data anyone cares about needs migrations, because <c>EnsureCreated</c> cannot alter an
/// existing schema and will silently leave an old one in place.
/// </remarks>
public static class DatabaseSeeder
{
    /// <summary>Ensures the schema exists and seeds it once.</summary>
    /// <param name="dbContext">Context to create and seed.</param>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns>Number of gateways inserted, which is zero on every run after the first.</returns>
    public static async Task<int> EnsureSeededAsync(
        ControlPlaneDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        if (await dbContext.Servers.AnyAsync(cancellationToken).ConfigureAwait(false))
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
                LoadPercent = server.LoadPercent,
                IsEnabled = server.IsEnabled,
            });
        }

        return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
