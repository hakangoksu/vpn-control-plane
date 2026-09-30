using System.Collections.Concurrent;

namespace VpnControl.Api.Endpoints;

/// <summary>
/// Tracks, per gateway, which version of its peer list exists and which version the
/// gateway has confirmed applying, and lets requests wait for either to move.
/// </summary>
/// <remarks>
/// This is what makes a new peer usable the moment registration returns. Without it the
/// client's first handshake can reach the gateway before the gateway has admitted the key;
/// WireGuard drops the unknown handshake silently and retries only after five seconds.
/// <para>
/// The protocol has two halves. A gateway agent long-polls with the version it last
/// applied, and the request is held until a newer version exists, so a change reaches the
/// gateway within one round trip instead of one polling interval. The agent's next request
/// carries the version it has just applied, which is the acknowledgement a registration
/// waits for before it answers the client.
/// </para>
/// <para>
/// State is in memory, which suits the single instance this project runs. Versions start
/// from the wall clock at startup, so after a restart every new version is larger than
/// anything an agent reports from before it, and a stale acknowledgement can never be
/// mistaken for a fresh one. Several instances would need the version and the wake-up
/// signal in a shared store instead.
/// </para>
/// </remarks>
public sealed class GatewaySyncCoordinator(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, GatewayState> _gateways = new(StringComparer.Ordinal);
    private readonly long _baseVersion = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The current version of a gateway's peer list.</summary>
    /// <param name="gatewayId">Gateway to ask about.</param>
    /// <returns>A number that grows every time the list changes.</returns>
    public long CurrentVersion(string gatewayId) => Get(gatewayId).Version;

    /// <summary>Records that a gateway's peer list changed and wakes its waiting poll.</summary>
    /// <param name="gatewayId">Gateway whose list changed.</param>
    /// <returns>The new version, which the change is part of.</returns>
    public long MarkChanged(string gatewayId)
    {
        GatewayState state = Get(gatewayId);
        lock (state)
        {
            state.Version++;
            state.Changed.TrySetResult();
            state.Changed = NewSignal();
            return state.Version;
        }
    }

    /// <summary>
    /// Records the version a gateway reports having applied and wakes registrations
    /// waiting for it.
    /// </summary>
    /// <param name="gatewayId">Gateway reporting.</param>
    /// <param name="appliedVersion">Version the agent applied, as it reports it.</param>
    public void RecordApplied(string gatewayId, long appliedVersion)
    {
        GatewayState state = Get(gatewayId);
        lock (state)
        {
            // An agent can only confirm versions this instance has handed out. A larger
            // number is a bug or a lie, and must not confirm registrations not yet served.
            long confirmed = Math.Min(appliedVersion, state.Version);
            if (confirmed <= state.Applied)
            {
                return;
            }

            state.Applied = confirmed;
            state.AppliedMoved.TrySetResult();
            state.AppliedMoved = NewSignal();
        }
    }

    /// <summary>
    /// Waits until the gateway's list is newer than the version the agent already has, or
    /// until the timeout passes.
    /// </summary>
    /// <param name="gatewayId">Gateway polling.</param>
    /// <param name="knownVersion">Version the agent already applied.</param>
    /// <param name="timeout">Longest time to hold the request.</param>
    /// <param name="cancellationToken">Cancelled when the agent disconnects.</param>
    /// <returns>A task that completes when there is something new or the time is up.</returns>
    public async Task WaitForChangeAsync(string gatewayId, long knownVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        GatewayState state = Get(gatewayId);
        Task signal;
        lock (state)
        {
            if (state.Version > knownVersion)
            {
                return;
            }

            signal = state.Changed.Task;
        }

        await WaitOrTimeoutAsync(signal, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the gateway confirms applying at least the given version.
    /// </summary>
    /// <param name="gatewayId">Gateway to wait for.</param>
    /// <param name="version">Version that must be applied.</param>
    /// <param name="timeout">Longest time to wait.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns><c>true</c> when the gateway confirmed in time.</returns>
    public async Task<bool> WaitUntilAppliedAsync(string gatewayId, long version, TimeSpan timeout, CancellationToken cancellationToken)
    {
        GatewayState state = Get(gatewayId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        while (true)
        {
            Task signal;
            lock (state)
            {
                if (state.Applied >= version)
                {
                    return true;
                }

                signal = state.AppliedMoved.Task;
            }

            try
            {
                await signal.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The deadline passed. Not an error: the peer is stored and the gateway will
                // admit it on its next poll. The caller reports it as not yet confirmed.
                return false;
            }
        }
    }

    private static async Task WaitOrTimeoutAsync(Task signal, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The poll answers with the unchanged list; the agent simply asks again.
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private GatewayState Get(string gatewayId) =>
        _gateways.GetOrAdd(gatewayId, _ => new GatewayState(_baseVersion));

    /// <summary>Mutable per-gateway state, guarded by locking on the instance itself.</summary>
    private sealed class GatewayState(long baseVersion)
    {
        public long Version { get; set; } = baseVersion;

        public long Applied { get; set; }

        public TaskCompletionSource Changed { get; set; } = NewSignal();

        public TaskCompletionSource AppliedMoved { get; set; } = NewSignal();
    }
}
