using VpnControl.Core.Crypto;
using VpnControl.Core.Tunneling;

namespace VpnControl.Core.Tests.Fakes;

/// <summary>
/// A tunnel backend the tests can steer: it records what it was asked to do and fails on
/// demand.
/// </summary>
/// <remarks>
/// Written by hand rather than produced by a mocking library. The orchestrator's interesting
/// behaviour is a sequence of calls under failure, and a hand-written double makes that
/// sequence readable in the assertions instead of hidden behind setup expressions.
/// </remarks>
internal sealed class FakeVpnTunnel : IVpnTunnel
{
    public string Name => "Fake tunnel";

    public bool IsSimulated => true;

    public TunnelState State { get; private set; } = TunnelState.Down;

    public event EventHandler<TunnelStateChangedEventArgs>? StateChanged;

    /// <summary>Configurations handed to <see cref="UpAsync"/>, in order.</summary>
    public List<WireGuardConfig> AppliedConfigs { get; } = [];

    public int UpCalls { get; private set; }

    public int DownCalls { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>Exception to throw from the next <see cref="UpAsync"/>, or <c>null</c>.</summary>
    public Exception? FailOnUp { get; set; }

    /// <summary>Exception to throw from the next <see cref="DownAsync"/>, or <c>null</c>.</summary>
    public Exception? FailOnDown { get; set; }

    /// <summary>Delay observed inside <see cref="UpAsync"/>, for cancellation tests.</summary>
    public TimeSpan UpDelay { get; set; } = TimeSpan.Zero;

    public TunnelStatistics Statistics { get; set; } = new(1024, 512, DateTimeOffset.UnixEpoch, "fake:51820");

    public async Task UpAsync(WireGuardConfig config, CancellationToken cancellationToken = default)
    {
        UpCalls++;

        if (UpDelay > TimeSpan.Zero)
        {
            await Task.Delay(UpDelay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (FailOnUp is Exception failure)
        {
            FailOnUp = null;
            SetState(TunnelState.Faulted);
            throw failure;
        }

        AppliedConfigs.Add(config);
        SetState(TunnelState.Up);
    }

    public Task DownAsync(CancellationToken cancellationToken = default)
    {
        DownCalls++;

        if (FailOnDown is Exception failure)
        {
            FailOnDown = null;
            SetState(TunnelState.Faulted);
            return Task.FromException(failure);
        }

        SetState(TunnelState.Down);
        return Task.CompletedTask;
    }

    public Task<TunnelStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State == TunnelState.Up ? Statistics : TunnelStatistics.Empty);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

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
