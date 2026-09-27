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

    /// <summary>Reported load, 0 to 100.</summary>
    public required int LoadPercent { get; set; }

    /// <summary>Whether the gateway is advertised to clients.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Peers currently registered on this gateway.</summary>
    public ICollection<PeerRecord> Peers { get; } = new List<PeerRecord>();

    /// <summary>Projects the stored row onto the published contract.</summary>
    /// <returns>The gateway as a client sees it.</returns>
    public VpnServer ToContract() => new()
    {
        Id = Id,
        Name = Name,
        City = City,
        Country = Country,
        EndpointHost = EndpointHost,
        EndpointPort = EndpointPort,
        PublicKey = PublicKey,
        LoadPercent = LoadPercent,
        IsEnabled = IsEnabled,
    };
}
