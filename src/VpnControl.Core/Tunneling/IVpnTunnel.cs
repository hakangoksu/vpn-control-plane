using VpnControl.Core.Crypto;

namespace VpnControl.Core.Tunneling;

/// <summary>
/// The one thing every platform has to provide: somewhere to put a WireGuard
/// configuration and a way to take it away again.
/// </summary>
/// <remarks>
/// This is the seam between logic and platform. Everything above it, catalog fetching,
/// selection, key handling, the state machine, is portable C# that runs anywhere and is
/// covered by unit tests. Everything below it is <c>wg-quick</c> on Linux, a privileged
/// service on Windows, or a simulation, and is where the untestable parts are confined.
/// <para>
/// Keeping the interface this narrow is the point. Three methods and an event are
/// enough to write the whole application against, which means porting to a new platform
/// is a bounded job rather than an audit of the entire codebase.
/// </para>
/// </remarks>
public interface IVpnTunnel : IAsyncDisposable
{
    /// <summary>Name of the backend, for logs and for the user interface.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this backend only pretends to move packets.
    /// </summary>
    /// <remarks>
    /// Exposed rather than inferred from the type, so the user interface can label a
    /// simulated session without knowing which backends exist. A VPN client that leaves
    /// a user unsure whether their traffic is protected is worse than one that does not
    /// connect at all.
    /// </remarks>
    bool IsSimulated { get; }

    /// <summary>Current state of the interface.</summary>
    TunnelState State { get; }

    /// <summary>Raised after the backend's state changes.</summary>
    /// <remarks>
    /// Handlers run on whichever thread caused the change, which for a desktop client
    /// is not the UI thread. Marshalling is the listener's job, and the view models in
    /// this solution do it.
    /// </remarks>
    event EventHandler<TunnelStateChangedEventArgs>? StateChanged;

    /// <summary>Brings the interface up with the given configuration.</summary>
    /// <param name="config">The configuration to apply.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>A task that completes once the interface is up.</returns>
    /// <exception cref="TunnelException">
    /// The backend could not apply the configuration. Implementations leave no
    /// half-configured interface behind when they throw.
    /// </exception>
    Task UpAsync(WireGuardConfig config, CancellationToken cancellationToken = default);

    /// <summary>Takes the interface down.</summary>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>A task that completes once the interface is gone.</returns>
    /// <remarks>
    /// Bringing down a tunnel that is already down succeeds quietly. Teardown runs on
    /// paths where something has already gone wrong, and a backend that throws there
    /// would turn one failure into two.
    /// </remarks>
    /// <exception cref="TunnelException">The interface exists and could not be removed.</exception>
    Task DownAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the current counters.</summary>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <returns>
    /// The counters, or <see cref="TunnelStatistics.Empty"/> when the interface is down.
    /// </returns>
    Task<TunnelStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}
