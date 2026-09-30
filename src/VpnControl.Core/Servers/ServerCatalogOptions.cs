using System.ComponentModel.DataAnnotations;

namespace VpnControl.Core.Servers;

/// <summary>
/// Settings for <see cref="HttpServerCatalogClient"/>, bound from configuration.
/// </summary>
/// <remarks>
/// This is the options pattern: the class carries the settings, the host binds it
/// from <c>appsettings.json</c> or environment variables, and the client receives it
/// through <c>IOptions&lt;T&gt;</c>. The client never reads configuration itself, so
/// a test can construct the options directly and skip the host entirely.
/// </remarks>
public sealed class ServerCatalogOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "ServerCatalog";

    /// <summary>Base address of the control plane API, including any path prefix.</summary>
    [Required]
    public string BaseAddress { get; set; } = "http://localhost:5080/";

    /// <summary>
    /// Token issued to this device by the operator, sent as a bearer credential on every
    /// request.
    /// </summary>
    /// <remarks>
    /// One token per device, so the operator can revoke a lost laptop without touching any
    /// other client. It belongs in the git-ignored <c>appsettings.Local.json</c>, never in the
    /// committed settings file.
    /// </remarks>
    public string? DeviceToken { get; set; }

    /// <summary>How long a single HTTP attempt may take before it is abandoned.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Attempts, including the first, for a request that fails transiently.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait before the second attempt, doubled for each attempt after it.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}
