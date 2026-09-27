using System.Text.Json.Serialization;

namespace VpnControl.Core.Servers;

/// <summary>
/// A single VPN gateway a client can tunnel through.
/// </summary>
/// <remarks>
/// This type does double duty: it is the domain model the selection logic reasons
/// about and it is the wire model the catalog API returns. Splitting the two is
/// the usual advice, but in this solution the API project references this library,
/// so one definition means the contract cannot drift between the two sides. If the
/// stored shape and the published shape ever diverge, that is the moment to split
/// it and map between them.
/// <para>
/// It is a record so that value equality comes for free, which the tests rely on,
/// and so that <c>with</c> expressions can produce a modified copy without
/// mutating an instance another thread may be reading.
/// </para>
/// </remarks>
public sealed record VpnServer
{
    /// <summary>Stable identifier, for example <c>lt-vln-01</c>.</summary>
    /// <remarks>
    /// Used as the tie-breaker of last resort during selection, so it has to be
    /// unique and stable across catalog refreshes.
    /// </remarks>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Human readable name shown in the user interface.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>City the gateway sits in, for grouping and filtering.</summary>
    [JsonPropertyName("city")]
    public required string City { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code, for example <c>LT</c>.</summary>
    [JsonPropertyName("country")]
    public required string Country { get; init; }

    /// <summary>Host name or address the WireGuard endpoint listens on.</summary>
    [JsonPropertyName("endpointHost")]
    public required string EndpointHost { get; init; }

    /// <summary>UDP port the WireGuard endpoint listens on.</summary>
    [JsonPropertyName("endpointPort")]
    public required int EndpointPort { get; init; }

    /// <summary>Base64 encoded X25519 public key of the gateway.</summary>
    /// <remarks>
    /// The client needs this to write the <c>[Peer]</c> section of its tunnel
    /// configuration. It is public key material, so it is safe to serve openly.
    /// </remarks>
    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; init; }

    /// <summary>
    /// Reported utilisation of the gateway, from 0 (idle) to 100 (saturated).
    /// </summary>
    /// <remarks>
    /// Selection weights latency by this value, because the fastest gateway is a
    /// poor choice if it is already the busiest one.
    /// </remarks>
    [JsonPropertyName("loadPercent")]
    public required int LoadPercent { get; init; }

    /// <summary>
    /// Whether the operator currently advertises this gateway as usable.
    /// </summary>
    /// <remarks>
    /// This is the operator's opinion. Whether the client can actually reach the
    /// gateway is a separate question, answered by a latency probe.
    /// </remarks>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;

    /// <summary><c>host:port</c> form of the endpoint, as WireGuard writes it.</summary>
    [JsonIgnore]
    public string Endpoint => $"{EndpointHost}:{EndpointPort}";

    /// <summary>Short label for logs and list rows, for example <c>Vilnius, LT</c>.</summary>
    [JsonIgnore]
    public string Location => $"{City}, {Country}";
}
