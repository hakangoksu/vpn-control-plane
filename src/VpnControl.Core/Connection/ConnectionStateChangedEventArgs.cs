namespace VpnControl.Core.Connection;

/// <summary>Describes one transition of the connection state machine.</summary>
/// <param name="previous">State the session was in.</param>
/// <param name="current">State it is in now.</param>
/// <param name="reason">Why the transition happened, when there is something to say.</param>
public sealed class ConnectionStateChangedEventArgs(
    ConnectionState previous,
    ConnectionState current,
    string? reason = null) : EventArgs
{
    /// <summary>State the session was in.</summary>
    public ConnectionState Previous { get; } = previous;

    /// <summary>State the session is in now.</summary>
    public ConnectionState Current { get; } = current;

    /// <summary>
    /// Why the transition happened, for example the error that caused a fault.
    /// </summary>
    public string? Reason { get; } = reason;
}
