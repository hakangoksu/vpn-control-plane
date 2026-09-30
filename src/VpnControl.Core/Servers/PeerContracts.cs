using System.Text.Json.Serialization;

namespace VpnControl.Core.Servers;

/// <summary>
/// What a client sends to claim a slot on a gateway.
/// </summary>
/// <param name="ServerId">Gateway the client wants to be admitted to.</param>
/// <param name="PublicKey">
/// Base64 encoded X25519 public key the client has just generated. Only the
/// public half ever leaves the client, which is the whole point of the exchange.
/// </param>
/// <param name="DeviceName">
/// Optional label so a user can recognise their own devices in a session list.
/// </param>
public sealed record PeerRegistrationRequest(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("deviceName")] string? DeviceName = null);

/// <summary>
/// Everything the server side decides on the client's behalf once a peer is
/// admitted: which address it may use and how to reach the gateway.
/// </summary>
/// <remarks>
/// This maps almost one to one onto a WireGuard configuration file. The client
/// supplies the private key, which never appears here.
/// </remarks>
public sealed record PeerConfiguration
{
    /// <summary>Identifier of the registration, used to release it again.</summary>
    [JsonPropertyName("peerId")]
    public required string PeerId { get; init; }

    /// <summary>Gateway this registration belongs to.</summary>
    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    /// <summary>IPv4 address assigned to the client inside the tunnel, in CIDR form.</summary>
    [JsonPropertyName("assignedAddress")]
    public required string AssignedAddress { get; init; }

    /// <summary>
    /// IPv6 address assigned to the client inside the tunnel, in CIDR form, or <c>null</c>
    /// when the control plane hands out IPv4 only.
    /// </summary>
    /// <remarks>
    /// A separate property rather than a list next to the IPv4 one, so an older client that
    /// knows nothing about IPv6 still finds the field it expects.
    /// </remarks>
    [JsonPropertyName("assignedAddressV6")]
    public string? AssignedAddressV6 { get; init; }

    /// <summary>Base64 encoded public key of the gateway.</summary>
    [JsonPropertyName("serverPublicKey")]
    public required string ServerPublicKey { get; init; }

    /// <summary><c>host:port</c> the client sends its handshake to.</summary>
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }

    /// <summary>Prefixes that should be routed into the tunnel.</summary>
    /// <remarks>
    /// <c>0.0.0.0/0</c> here means a full tunnel. A split tunnel lists only the
    /// prefixes the operator serves.
    /// </remarks>
    [JsonPropertyName("allowedIps")]
    public required IReadOnlyList<string> AllowedIps { get; init; }

    /// <summary>DNS resolvers to use while the tunnel is up.</summary>
    [JsonPropertyName("dnsServers")]
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();

    /// <summary>Seconds between keepalive packets, or <c>null</c> to send none.</summary>
    /// <remarks>
    /// A keepalive keeps a NAT mapping alive so the gateway can reach a client
    /// that is behind a home router. Without it an idle tunnel goes quiet.
    /// </remarks>
    [JsonPropertyName("persistentKeepaliveSeconds")]
    public int? PersistentKeepaliveSeconds { get; init; }
}
