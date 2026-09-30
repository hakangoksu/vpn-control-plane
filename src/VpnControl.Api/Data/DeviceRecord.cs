using System.ComponentModel.DataAnnotations;

namespace VpnControl.Api.Data;

/// <summary>
/// A client device the operator has enrolled, identified by the hash of its access token.
/// </summary>
/// <remarks>
/// A device is the unit of access control. Every peer registration belongs to one, so a
/// device can release only its own registrations, and revoking a device removes every
/// tunnel it holds on the next gateway sync. A shared key would give neither property.
/// <para>
/// The token itself is never stored. It is shown once when the device is enrolled, and
/// only its SHA-256 hash is kept. Someone who copies the database learns nothing that lets
/// them call the API.
/// </para>
/// </remarks>
public sealed class DeviceRecord
{
    /// <summary>Primary key, a random identifier that is not itself a secret.</summary>
    [Key]
    [MaxLength(64)]
    public required string Id { get; set; }

    /// <summary>Label the operator chose, for example <c>laptop</c>.</summary>
    [MaxLength(128)]
    public required string Name { get; set; }

    /// <summary>SHA-256 of the access token, as lower case hex.</summary>
    [MaxLength(64)]
    public required string TokenHash { get; set; }

    /// <summary>When the device was enrolled.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the device was revoked, or <c>null</c> while it is active.
    /// </summary>
    /// <remarks>
    /// Revocation keeps the row rather than deleting it, so the list of devices shows that
    /// a token existed and when it stopped working. The token hash stays unique, which also
    /// means a revoked token can never be enrolled again by accident.
    /// </remarks>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Peer registrations this device currently holds.</summary>
    public ICollection<PeerRecord> Peers { get; } = new List<PeerRecord>();
}
