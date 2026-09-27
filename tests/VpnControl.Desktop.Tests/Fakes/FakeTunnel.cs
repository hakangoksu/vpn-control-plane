using VpnControl.Core.Crypto;
using VpnControl.Core.Tunneling;

namespace VpnControl.Desktop.Tests.Fakes;

/// <summary>
/// A tunnel backend that comes up at once and reports counters the test chooses.
/// </summary>
/// <remarks>
/// The real <c>SimulatedTunnel</c> would do, but it waits for its simulated handshake and
/// grows its counters from a stopwatch, neither of which a test wants to sit through or
/// assert against.
/// </remarks>
internal sealed class FakeTunnel : IVpnTunnel
{
    public string Name => "Fake tunnel";

    public bool IsSimulated => true;

    public TunnelState State { get; private set; } = TunnelState.Down;

    public event EventHandler<TunnelStateChangedEventArgs>? StateChanged;

    /// <summary>Counters returned while the tunnel is up.</summary>
    public TunnelStatistics Statistics { get; set; } = new(4096, 1024, null, "lt-vln-01.invalid:51820");

    /// <summary>Exception to throw from the next call to <see cref="UpAsync"/>.</summary>
    public Exception? FailOnUp { get; set; }

    public Task UpAsync(WireGuardConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailOnUp is Exception failure)
        {
            FailOnUp = null;
            return Task.FromException(failure);
        }

        SetState(TunnelState.Up);
        return Task.CompletedTask;
    }

    public Task DownAsync(CancellationToken cancellationToken = default)
    {
        SetState(TunnelState.Down);
        return Task.CompletedTask;
    }

    public Task<TunnelStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State == TunnelState.Up ? Statistics : TunnelStatistics.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SetState(TunnelState next)
    {
        TunnelState previous = State;
        State = next;

        if (previous != next)
        {
            StateChanged?.Invoke(this, new TunnelStateChangedEventArgs(previous, next));
        }
    }
}
