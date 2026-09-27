using System.ComponentModel.DataAnnotations;

namespace VpnControl.Api.Configuration;

/// <summary>
/// Settings that decide what the control plane hands out to a client.
/// </summary>
/// <remarks>
/// Bound from the <c>ControlPlane</c> section and validated at startup, so a missing or
/// nonsensical value fails the process immediately rather than producing a tunnel
/// configuration that cannot work. Failing at startup is the cheap version of that bug.
/// </remarks>
public sealed class ControlPlaneOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// Key a caller must present in the <c>X-Api-Key</c> header on the peer endpoints.
    /// </summary>
    /// <remarks>
    /// Illustrative only. A shared static key identifies nobody, cannot be revoked for one
    /// device and is visible to anyone who can read the client's configuration. It is here
    /// to show where an endpoint filter attaches and how the endpoints are grouped by who
    /// may call them. A real deployment would issue a per-user token from a sign-in flow.
    /// </remarks>
    [Required]
    public string ApiKey { get; set; } = "local-development-key";

    /// <summary>
    /// First two octets of the address range peers are assigned from.
    /// </summary>
    /// <remarks>
    /// The lab uses 10.99.0.0/16, which is inside RFC 1918 private space and unlikely to
    /// collide with a home network the way 192.168.1.0/24 would.
    /// </remarks>
    [Required]
    [RegularExpression(@"^\d{1,3}\.\d{1,3}$")]
    public string AddressPrefix { get; set; } = "10.99";

    /// <summary>Resolvers handed to clients for use inside the tunnel.</summary>
    /// <remarks>
    /// Empty by default, with the real values in <c>appsettings.json</c>. The configuration
    /// binder adds to a collection rather than replacing it, so a property initialised with
    /// defaults ends up holding every default twice once configuration supplies the same
    /// values. Leaving it empty here makes configuration the only source.
    /// </remarks>
    public IList<string> DnsServers { get; set; } = [];

    /// <summary>Prefixes clients are told to route into the tunnel.</summary>
    /// <remarks>
    /// Empty by default for the same reason as <see cref="DnsServers"/>. A tunnel with no
    /// allowed prefixes would carry nothing, so at least one value is required and the
    /// process refuses to start without it.
    /// </remarks>
    [MinLength(1)]
    public IList<string> AllowedIps { get; set; } = [];

    /// <summary>Keepalive interval handed to clients, in seconds.</summary>
    [Range(0, 600)]
    public int PersistentKeepaliveSeconds { get; set; } = 25;

    /// <summary>
    /// Most peers one gateway will admit.
    /// </summary>
    /// <remarks>
    /// A limit rather than an unbounded pool, because the address range is finite and
    /// because the failure should be a clear 409 rather than an address that collides.
    /// </remarks>
    [Range(1, 60000)]
    public int MaxPeersPerServer { get; set; } = 250;
}
