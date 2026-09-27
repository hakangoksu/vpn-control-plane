namespace VpnControl.Tunnel;

/// <summary>Settings for <see cref="WgQuickTunnel"/>.</summary>
public sealed class WgQuickOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "WgQuick";

    /// <summary>
    /// Interface name to create. Also the base name of the generated config file,
    /// because <c>wg-quick</c> derives one from the other.
    /// </summary>
    /// <remarks>
    /// Linux caps an interface name at 15 characters, so there is not much room.
    /// </remarks>
    public string InterfaceName { get; set; } = "vpnlab0";

    /// <summary>Directory the generated configuration is written to.</summary>
    /// <remarks>
    /// Defaults to a subdirectory of the user's home rather than <c>/etc/wireguard</c>, so
    /// nothing has to be written to a system location before the tool is even known to
    /// work. <c>wg-quick</c> accepts a full path to a config file, which is what makes that
    /// possible.
    /// </remarks>
    public string ConfigDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "vpn-control-plane");

    /// <summary>
    /// Command used to gain privileges, or <c>null</c> to run the tools directly.
    /// </summary>
    /// <remarks>
    /// Creating a network interface needs <c>CAP_NET_ADMIN</c>. Setting this to <c>sudo</c>
    /// works interactively but will hang on a password prompt when nothing is attached to
    /// the terminal, which is exactly why a desktop client uses a privileged helper service
    /// instead. It is left empty by default so that failure mode is opt in.
    /// </remarks>
    public string? PrivilegeEscalationCommand { get; set; }

    /// <summary>How long a single <c>wg-quick</c> invocation may take.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
