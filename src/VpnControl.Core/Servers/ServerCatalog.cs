namespace VpnControl.Core.Servers;

/// <summary>
/// An immutable snapshot of the gateways known at one moment, with the queries the
/// user interface needs.
/// </summary>
/// <remarks>
/// Fetching returns a snapshot rather than a live collection on purpose. A list
/// that changes underneath a running selection pass is a source of bugs that only
/// show up under load, and an immutable snapshot removes the possibility. A refresh
/// produces a new catalog instead of mutating this one.
/// </remarks>
public sealed class ServerCatalog
{
    private readonly Dictionary<string, VpnServer> _byId;

    /// <summary>Builds a catalog from the gateways given.</summary>
    /// <param name="servers">The gateways, in any order.</param>
    /// <param name="retrievedAt">
    /// When the data was obtained, so a caller can decide it is too old to trust.
    /// </param>
    /// <exception cref="ArgumentException">Two gateways share an identifier.</exception>
    public ServerCatalog(IEnumerable<VpnServer> servers, DateTimeOffset retrievedAt)
    {
        ArgumentNullException.ThrowIfNull(servers);

        _byId = new Dictionary<string, VpnServer>(StringComparer.OrdinalIgnoreCase);
        foreach (VpnServer server in servers)
        {
            if (!_byId.TryAdd(server.Id, server))
            {
                throw new ArgumentException($"Duplicate server id '{server.Id}'.", nameof(servers));
            }
        }

        RetrievedAt = retrievedAt;
        Servers = _byId.Values
            .OrderBy(s => s.Country, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.City, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>An empty catalog, used as the starting state before the first fetch.</summary>
    public static ServerCatalog Empty { get; } = new(Array.Empty<VpnServer>(), DateTimeOffset.MinValue);

    /// <summary>All gateways, ordered by country, then city, then identifier.</summary>
    public IReadOnlyList<VpnServer> Servers { get; }

    /// <summary>When this snapshot was obtained.</summary>
    public DateTimeOffset RetrievedAt { get; }

    /// <summary>Number of gateways in the snapshot.</summary>
    public int Count => Servers.Count;

    /// <summary>Finds a gateway by identifier.</summary>
    /// <param name="id">The identifier to look for, compared case insensitively.</param>
    /// <returns>The gateway, or <c>null</c> when the catalog does not contain it.</returns>
    public VpnServer? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>Gateways the operator currently advertises as usable.</summary>
    public IEnumerable<VpnServer> Enabled() => Servers.Where(s => s.IsEnabled);

    /// <summary>Gateways in one country.</summary>
    /// <param name="country">ISO 3166-1 alpha-2 code, compared case insensitively.</param>
    public IEnumerable<VpnServer> InCountry(string country)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        return Servers.Where(s => string.Equals(s.Country, country, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The distinct countries represented, in alphabetical order.</summary>
    public IEnumerable<string> Countries() =>
        Servers.Select(s => s.Country).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
}
