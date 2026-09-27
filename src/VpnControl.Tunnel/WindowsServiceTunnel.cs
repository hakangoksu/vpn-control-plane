using VpnControl.Core.Crypto;
using VpnControl.Core.Tunneling;

namespace VpnControl.Tunnel;

/// <summary>
/// The place a Windows backend would go. Every member throws
/// <see cref="PlatformNotSupportedException"/>.
/// </summary>
/// <remarks>
/// This type is documentation with a compiler-checked signature, not an implementation.
/// It exists because the alternative was worse: a backend that logged "connected" on
/// Windows without creating a tunnel would be a lie told by a VPN client, which is the one
/// kind of lie such a program must never tell. This repository was written on Arch Linux
/// and has never had a Windows tunnel tested against it, so it says so here instead.
///
/// <para><b>How the real thing works.</b> WireGuard on Windows is a kernel driver,
/// WireGuardNT, exposed through the <c>wireguard.dll</c> tunnel library. A client does not
/// drive it from the process the user clicks on. It installs a Windows service, one per
/// tunnel, whose executable calls <c>WireGuardTunnelService</c> with the path to a
/// configuration file. That service runs as LocalSystem, creates the adapter, applies the
/// addresses and routes, and owns the tunnel for its lifetime. Stopping the service removes
/// the adapter.</para>
///
/// <para><b>Why the split matters.</b> Creating a network adapter, writing routes and
/// installing firewall filters all require administrative rights. A desktop application
/// runs as the logged-in user and must not require the user to be an administrator, so the
/// work is divided: an unprivileged UI process and a privileged helper, talking over a
/// named pipe or through the service control manager. The official Windows client does
/// exactly this, and its pipe is secured so only the elevated side can be driven by a
/// caller with the right identity.</para>
///
/// <para><b>Why the UI must not hold the keys.</b> If the UI process generated and held the
/// tunnel's private key, that key would live in a process that loads UI frameworks, third
/// party controls and possibly a browser engine, any of which enlarges what an attacker can
/// reach. The privileged side should generate the key pair, keep the private half, hand the
/// public half up for registration, and write the configuration to a location the user
/// cannot read. The UI then never has a secret to leak, and a crash dump from it cannot
/// contain one. This is also why the kill switch belongs on the privileged side: it is
/// enforced with Windows Filtering Platform filters, which an unprivileged process cannot
/// install, and which must survive the UI process being killed.</para>
///
/// <para><b>What implementing this would take.</b> A service host executable, an installer
/// that registers it, a named pipe protocol with an access control list, marshalling to
/// <c>wireguard.dll</c> through P/Invoke or a wrapper, and a way to read adapter statistics
/// back. None of that can be written honestly, let alone tested, from this machine.</para>
/// </remarks>
public sealed class WindowsServiceTunnel : IVpnTunnel
{
    private const string NotImplementedMessage =
        "The Windows backend is not implemented in this project. It requires a privileged service " +
        "driving the WireGuardNT driver through wireguard.dll, which cannot be built or tested here. " +
        "Use SimulatedTunnel, or WgQuickTunnel on Linux.";

    /// <inheritdoc />
    public string Name => "Windows tunnel service (not implemented)";

    /// <inheritdoc />
    /// <remarks>
    /// Reported as <c>false</c> rather than <c>true</c>. This backend does not simulate a
    /// tunnel, it refuses to provide one, and a user interface must not be able to read
    /// this property and decide it is safe to show a connected state.
    /// </remarks>
    public bool IsSimulated => false;

    /// <inheritdoc />
    public TunnelState State => TunnelState.Down;

    /// <inheritdoc />
    /// <remarks>Never raised, because this backend never changes state.</remarks>
    public event EventHandler<TunnelStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">Always.</exception>
    public Task UpAsync(WireGuardConfig config, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">Always.</exception>
    public Task DownAsync(CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">Always.</exception>
    public Task<TunnelStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException(NotImplementedMessage);

    /// <inheritdoc />
    /// <remarks>
    /// Does nothing and does not throw. Dispose is called on cleanup paths, and a type that
    /// throws from it turns one failure into two.
    /// </remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
