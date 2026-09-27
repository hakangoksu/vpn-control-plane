using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;

namespace VpnControl.Desktop.Tests.Fakes;

/// <summary>Builds gateways for these tests without repeating the required properties.</summary>
internal static class TestGateways
{
    /// <summary>A valid base64 X25519 key, generated once for the whole test run.</summary>
    /// <remarks>
    /// Generated rather than hard coded, so nothing in the repository resembles a credential.
    /// </remarks>
    public static string SampleKey { get; } = Generate();

    public static VpnServer Create(
        string id,
        string city,
        string country = "LT",
        int loadPercent = 0,
        bool isEnabled = true) => new()
        {
            Id = id,
            Name = city + " gateway",
            City = city,
            Country = country,
            EndpointHost = $"{id}.invalid",
            EndpointPort = 51820,
            PublicKey = SampleKey,
            LoadPercent = loadPercent,
            IsEnabled = isEnabled,
        };

    private static string Generate()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        return keys.PublicKeyBase64;
    }
}
