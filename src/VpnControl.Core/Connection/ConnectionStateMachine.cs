namespace VpnControl.Core.Connection;

/// <summary>
/// Holds the session state and refuses any change the transition table does not allow.
/// </summary>
/// <remarks>
/// Written as its own class, with no knowledge of tunnels or HTTP, for one reason: the
/// rules become a table that can be read and tested on their own. The alternative, a
/// field updated from a dozen places in the orchestrator, is where a client ends up
/// showing "connected" over a tunnel that never came up.
/// <para>
/// Transitions are serialised with a lock, because a user clicking disconnect while a
/// handshake is in flight genuinely does mean two threads asking for a change at once.
/// The event is raised outside the lock, so a handler that calls back in cannot deadlock.
/// </para>
/// </remarks>
public sealed class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> AllowedTransitions = new()
    {
        // Idle. The only thing to do is start.
        [ConnectionState.Disconnected] = [ConnectionState.Connecting],

        // A connection attempt can succeed, be cancelled by the user, or fail.
        [ConnectionState.Connecting] = [ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Faulted],

        // An established session can move gateway, be closed, or drop.
        [ConnectionState.Connected] = [ConnectionState.Switching, ConnectionState.Disconnecting, ConnectionState.Faulted],

        // A switch lands on the new gateway, or is abandoned, or fails.
        [ConnectionState.Switching] = [ConnectionState.Connected, ConnectionState.Disconnecting, ConnectionState.Faulted],

        // Teardown normally completes. It can still fail, and a failure there matters:
        // an interface that would not come down is exactly the leak a kill switch exists
        // to prevent, so it faults rather than reporting a clean disconnect.
        [ConnectionState.Disconnecting] = [ConnectionState.Disconnected, ConnectionState.Faulted],

        // A fault can be retried directly, or cleaned up first.
        [ConnectionState.Faulted] = [ConnectionState.Connecting, ConnectionState.Disconnecting, ConnectionState.Disconnected],
    };

    private readonly object _gate = new();
    private ConnectionState _current;

    /// <summary>Creates a machine in the given starting state.</summary>
    /// <param name="initialState">
    /// Where to start. Defaults to <see cref="ConnectionState.Disconnected"/>; a test
    /// can start elsewhere to exercise one transition without driving the whole path.
    /// </param>
    public ConnectionStateMachine(ConnectionState initialState = ConnectionState.Disconnected)
    {
        _current = initialState;
    }

    /// <summary>Raised after a transition, outside the internal lock.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Current state.</summary>
    public ConnectionState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Whether the session is up and usable right now.</summary>
    public bool IsConnected => Current == ConnectionState.Connected;

    /// <summary>Whether an operation is in flight.</summary>
    public bool IsBusy => Current is ConnectionState.Connecting or ConnectionState.Switching or ConnectionState.Disconnecting;

    /// <summary>
    /// The transition table, exposed so documentation and tests read the same rules
    /// the implementation enforces.
    /// </summary>
    public static IReadOnlyDictionary<ConnectionState, IReadOnlyList<ConnectionState>> TransitionTable { get; } =
        AllowedTransitions.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ConnectionState>)pair.Value);

    /// <summary>Whether a transition out of the current state is allowed.</summary>
    /// <param name="next">Proposed state.</param>
    /// <returns><c>true</c> when the table permits it.</returns>
    public bool CanTransitionTo(ConnectionState next) => IsAllowed(Current, next);

    /// <summary>Whether a transition between two given states is allowed.</summary>
    /// <param name="from">Starting state.</param>
    /// <param name="to">Proposed state.</param>
    /// <returns><c>true</c> when the table permits it.</returns>
    public static bool IsAllowed(ConnectionState from, ConnectionState to) =>
        AllowedTransitions.TryGetValue(from, out ConnectionState[]? allowed) && Array.IndexOf(allowed, to) >= 0;

    /// <summary>Performs a transition.</summary>
    /// <param name="next">State to move to.</param>
    /// <param name="reason">Optional explanation carried on the event.</param>
    /// <exception cref="InvalidStateTransitionException">The table does not allow it.</exception>
    public void TransitionTo(ConnectionState next, string? reason = null)
    {
        if (!TryTransitionTo(next, reason))
        {
            throw new InvalidStateTransitionException(Current, next);
        }
    }

    /// <summary>Performs a transition if it is allowed, and reports whether it was.</summary>
    /// <param name="next">State to move to.</param>
    /// <param name="reason">Optional explanation carried on the event.</param>
    /// <returns><c>false</c> when the transition was rejected.</returns>
    /// <remarks>
    /// Used on teardown paths, where the session may already have reached the state
    /// being asked for and an exception would be noise rather than information.
    /// </remarks>
    public bool TryTransitionTo(ConnectionState next, string? reason = null)
    {
        ConnectionState previous;

        lock (_gate)
        {
            if (!IsAllowed(_current, next))
            {
                return false;
            }

            previous = _current;
            _current = next;
        }

        // Raised outside the lock: a handler that reads Current, or calls back in to
        // ask for another transition, would otherwise deadlock against this thread.
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, next, reason));
        return true;
    }
}
