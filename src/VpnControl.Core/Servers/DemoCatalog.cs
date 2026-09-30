using VpnControl.Core.Crypto;

namespace VpnControl.Core.Servers;

/// <summary>
/// The fictional gateway list this project ships with.
/// </summary>
/// <remarks>
/// Used to seed the API database and to populate the desktop app when it runs without a
/// backend. None of these hosts exist. They use the <c>.invalid</c> top level domain,
/// which is reserved by RFC 2606 precisely so that example names can never resolve to
/// somebody's real machine.
/// </remarks>
public static class DemoCatalog
{
    /// <summary>
    /// Builds the demo gateways, each with a freshly generated public key.
    /// </summary>
    /// <returns>Gateways in a fixed order, with real X25519 public keys.</returns>
    /// <remarks>
    /// The keys are generated rather than hard coded so that nothing in this repository
    /// looks like a credential, and so the key handling code is exercised on every run.
    /// The private halves are discarded immediately: nothing here can complete a
    /// handshake, which is correct, because none of these gateways exist.
    /// </remarks>
    public static IReadOnlyList<VpnServer> CreateServers() =>
    [
        Create("lt-vln-01", "Vilnius 1", "Vilnius", "LT", "lab-vilnius-1.invalid", 51820, 18),
        Create("lt-vln-02", "Vilnius 2", "Vilnius", "LT", "lab-vilnius-2.invalid", 51820, 64),
        Create("lt-kun-01", "Kaunas 1", "Kaunas", "LT", "lab-kaunas-1.invalid", 51820, 31),
        Create("pl-waw-01", "Warsaw 1", "Warsaw", "PL", "lab-warsaw-1.invalid", 51820, 47, ipv6Egress: false),
        Create("de-fra-01", "Frankfurt 1", "Frankfurt", "DE", "lab-frankfurt-1.invalid", 51820, 72),
        Create("de-fra-02", "Frankfurt 2", "Frankfurt", "DE", "lab-frankfurt-2.invalid", 51821, 96),
        Create("se-sto-01", "Stockholm 1", "Stockholm", "SE", "lab-stockholm-1.invalid", 51820, 12),
        Create("nl-ams-01", "Amsterdam 1", "Amsterdam", "NL", "lab-amsterdam-1.invalid", 51820, 55),
        Create("us-nyc-01", "New York 1", "New York", "US", "lab-newyork-1.invalid", 51820, 40, isEnabled: false),
    ];

    private static VpnServer Create(
        string id,
        string name,
        string city,
        string country,
        string host,
        int port,
        int loadPercent,
        bool isEnabled = true,
        bool ipv6Egress = true)
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        return new VpnServer
        {
            Id = id,
            Name = name,
            City = city,
            Country = country,
            EndpointHost = host,
            EndpointPort = port,
            PublicKey = keys.PublicKeyBase64,
            LoadPercent = loadPercent,
            IsEnabled = isEnabled,
            Ipv6Egress = ipv6Egress,
        };
    }
}
