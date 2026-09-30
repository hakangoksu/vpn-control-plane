using System.ComponentModel.DataAnnotations;
using VpnControl.Core.Servers;

namespace VpnControl.Api.Data;

/// <summary>
/// A gateway as it is stored.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="VpnServer"/>, the type the API publishes, even though the
/// two currently carry the same fields. The stored shape belongs to the database and will
/// grow columns that no client should see, an operator contact or a provisioning state, and
/// mapping in one place is cheaper than discovering later that a new column leaked into the
/// public contract.
/// </remarks>
public sealed class ServerRecord
{
    /// <summary>Primary key, for example <c>lt-vln-01</c>.</summary>
    [Key]
    [MaxLength(64)]
    public required string Id { get; set; }

    /// <summary>Display name.</summary>
    [MaxLength(128)]
    public required string Name { get; set; }

    /// <summary>City the gateway is in.</summary>
    [MaxLength(128)]
    public required string City { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    [MaxLength(2)]
    public required string Country { get; set; }

    /// <summary>Host name of the WireGuard endpoint.</summary>
    [MaxLength(255)]
    public required string EndpointHost { get; set; }

    /// <summary>UDP port of the WireGuard endpoint.</summary>
    public required int EndpointPort { get; set; }

    /// <summary>Base64 X25519 public key of the gateway.</summary>
    [MaxLength(64)]
    public required string PublicKey { get; set; }

    /// <summary>
    /// Whether the gateway forwards IPv6 to the internet.
    /// </summary>
    /// <remarks>
    /// Published so the client can show it. A gateway without IPv6 egress still carries the
    /// client's IPv6 traffic inside the tunnel and rejects it there, so this flag changes
    /// what works, never what leaks.
    /// </remarks>
    public bool Ipv6Egress { get; set; }

    /// <summary>
    /// SHA-256 of the token the gateway's sync agent presents, as lower case hex, or
    /// <c>null</c> for a gateway that has no agent.
    /// </summary>
    /// <remarks>
    /// This is one of the columns the note on the class anticipated: it must never reach a
    /// client, and <see cref="ToContract"/> does not copy it.
    /// </remarks>
    [MaxLength(64)]
    public string? AgentTokenHash { get; set; }

    /// <summary>Whether the gateway is advertised to clients.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Peers currently registered on this gateway.</summary>
    public ICollection<PeerRecord> Peers { get; } = new List<PeerRecord>();

    /// <summary>Projects the stored row onto the published contract.</summary>
    /// <param name="loadPercent">
    /// Share of the gateway's address pool in use, computed by the caller from the peer
    /// count. It is derived at read time rather than stored, because a stored figure is only
    /// as current as whoever last wrote it, and nothing on a gateway reports one.
    /// </param>
    /// <returns>The gateway as a client sees it.</returns>
    public VpnServer ToContract(int loadPercent) => new()
    {
        Id = Id,
        Name = Name,
        City = City,
        Country = Country,
        EndpointHost = EndpointHost,
        EndpointPort = EndpointPort,
        PublicKey = PublicKey,
        LoadPercent = loadPercent,
        IsEnabled = IsEnabled,
        Ipv6Egress = Ipv6Egress,
    };
}
