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
    /// Whether to fill an empty database with the fictional gateways from <c>DemoCatalog</c>.
    /// </summary>
    /// <remarks>
    /// On in development and in the tests, off everywhere else. A deployment with real
    /// gateways registers them through the admin command line, and a demo row that slipped
    /// into that catalog would be advertised to clients as a gateway they could connect to.
    /// </remarks>
    public bool SeedDemoCatalog { get; set; }

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

    /// <summary>
    /// The IPv6 /64 peers are assigned from, written as its first four groups, for example
    /// <c>fd4c:7a2e:91b3:0</c>. Empty to hand out IPv4 only.
    /// </summary>
    /// <remarks>
    /// A unique local prefix (RFC 4193) rather than a slice of a gateway's public range, for
    /// two reasons. Not every gateway has public IPv6, and a client needs an address inside
    /// the tunnel either way so that IPv6 traffic is routed into it rather than around it.
    /// And RFC 6724 ranks a unique local source below IPv4, so a dual stack destination is
    /// reached over IPv4 and IPv6 is only used where nothing else would work.
    /// <para>
    /// The 40 bit global identifier should be random, as the RFC asks, so that two networks
    /// using this project do not collide if they are ever joined.
    /// </para>
    /// </remarks>
    [RegularExpression(@"^(f[cd][0-9a-f]{2}(:[0-9a-f]{1,4}){3})?$")]
    public string Ipv6Prefix { get; set; } = string.Empty;

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

    /// <summary>
    /// Longest time a registration waits for the gateway to confirm it has admitted the new
    /// peer, in seconds. Zero answers at once.
    /// </summary>
    /// <remarks>
    /// Waiting means the client's first handshake finds the key already in place. Without the
    /// wait, the handshake can arrive first, be dropped, and cost the client the five seconds
    /// WireGuard waits before retrying. The wait is bounded so a gateway that is down delays a
    /// registration by this much and no more.
    /// </remarks>
    [Range(0, 30)]
    public double ActivationWaitSeconds { get; set; } = 5;
}
