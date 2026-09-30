namespace VpnControl.Desktop.Composition;

/// <summary>
/// Which implementations the client is wired up with.
/// </summary>
/// <remarks>
/// Every setting here defaults to the safe, self-contained choice, so the application
/// launches on a machine with no backend running, no WireGuard installed and no root
/// access. The switches exist because the alternative implementations are real code that
/// would otherwise never be reachable from the application, and a seam nothing ever uses
/// is a seam nobody can trust.
/// </remarks>
public sealed class DesktopOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "Desktop";

    /// <summary>
    /// Whether to talk to the control plane API instead of answering from memory.
    /// </summary>
    /// <remarks>
    /// Off by default. With it on, the API project has to be running and reachable at the
    /// address in the <c>ServerCatalog</c> section.
    /// </remarks>
    public bool UseControlPlaneApi { get; set; }

    /// <summary>
    /// Whether to create a real interface with <c>wg-quick</c> instead of simulating one.
    /// </summary>
    /// <remarks>
    /// Off by default, and turning it on needs Linux, the WireGuard tools, and privileges
    /// this process does not have on its own. The gateways in the demo catalog do not
    /// exist, so a real tunnel to one of them will time out on the handshake: this is for
    /// pointing the client at a gateway of your own.
    /// </remarks>
    public bool UseRealTunnel { get; set; }

    /// <summary>
    /// Whether to measure latency over the network instead of deriving it.
    /// </summary>
    /// <remarks>
    /// Off by default, because every host in the demo catalog is in the reserved
    /// <c>.invalid</c> domain and cannot resolve. Probing them for real would produce a
    /// list of failures and a slow refresh, which is accurate and useless.
    /// </remarks>
    public bool UseRealLatencyProbe { get; set; }

    /// <summary>TCP port the real latency probe connects to.</summary>
    /// <remarks>
    /// 443 suits a gateway that serves anything over HTTPS. The gateways in this project's
    /// deployment expose nothing but SSH and WireGuard, so the deployment points this at 22:
    /// the probe needs a TCP handshake from the host, and SSH is the one TCP port that is
    /// open anyway. Nothing is opened for the probe's sake.
    /// </remarks>
    public int LatencyProbePort { get; set; } = 443;
}
