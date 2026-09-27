using System.ComponentModel.DataAnnotations;

namespace VpnControl.Api.Data;

/// <summary>
/// One client admitted to one gateway, holding the address reserved for it.
/// </summary>
/// <remarks>
/// The row exists so the address is not handed to a second client. Nothing secret is
/// stored: the public key is public by design, and the gateway's private key is not the
/// control plane's business.
/// </remarks>
public sealed class PeerRecord
{
    /// <summary>Primary key handed back to the client so it can release the registration.</summary>
    [Key]
    [MaxLength(64)]
    public required string Id { get; set; }

    /// <summary>Gateway this registration is on.</summary>
    [MaxLength(64)]
    public required string ServerId { get; set; }

    /// <summary>Navigation to the gateway.</summary>
    public ServerRecord? Server { get; set; }

    /// <summary>Base64 X25519 public key the client presented.</summary>
    [MaxLength(64)]
    public required string PublicKey { get; set; }

    /// <summary>Address reserved for this client, in CIDR form.</summary>
    [MaxLength(64)]
    public required string AssignedAddress { get; set; }

    /// <summary>
    /// Index of the address inside the gateway's pool.
    /// </summary>
    /// <remarks>
    /// Stored alongside the rendered address so the next free index can be found with a
    /// query instead of by parsing every address string back into octets.
    /// </remarks>
    public required int AddressIndex { get; set; }

    /// <summary>Label the client supplied, if any.</summary>
    [MaxLength(128)]
    public string? DeviceName { get; set; }

    /// <summary>When the registration was made.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
