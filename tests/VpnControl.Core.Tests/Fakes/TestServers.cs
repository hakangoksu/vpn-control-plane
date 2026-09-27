using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;

namespace VpnControl.Core.Tests.Fakes;

/// <summary>Builds gateways for tests without repeating the required properties.</summary>
internal static class TestServers
{
    /// <summary>A valid base64 X25519 key, generated once for the whole test run.</summary>
    /// <remarks>
    /// Generated rather than hard coded so nothing in the repository resembles a credential,
    /// and shared across tests because generating one per gateway is pure overhead here.
    /// </remarks>
    public static string SampleKey { get; } = GenerateKey();

    public static VpnServer Create(
        string id,
        int loadPercent = 0,
        bool isEnabled = true,
        string city = "Vilnius",
        string country = "LT",
        string? publicKey = null) => new()
        {
            Id = id,
            Name = id.ToUpperInvariant(),
            City = city,
            Country = country,
            EndpointHost = $"{id}.invalid",
            EndpointPort = 51820,
            PublicKey = publicKey ?? SampleKey,
            LoadPercent = loadPercent,
            IsEnabled = isEnabled,
        };

    public static PeerConfiguration CreatePeerConfiguration(
        string serverId = "lt-vln-01",
        string address = "10.99.0.2/32",
        string? serverPublicKey = null) => new()
        {
            PeerId = "peer-1",
            ServerId = serverId,
            AssignedAddress = address,
            ServerPublicKey = serverPublicKey ?? SampleKey,
            Endpoint = $"{serverId}.invalid:51820",
            AllowedIps = ["10.99.0.0/16"],
            DnsServers = ["10.99.0.1"],
            PersistentKeepaliveSeconds = 25,
        };

    public static string GenerateKey()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        return keys.PublicKeyBase64;
    }
}
