namespace VpnControl.Core.Connection;

/// <summary>
/// Raised when code asks the state machine for a transition the table does not allow.
/// </summary>
/// <remarks>
/// This is a programming error rather than a runtime condition: it means the caller
/// believed the session was in a state it was not. Failing loudly is the point, because
/// the alternative is a session that looks connected and carries nothing.
/// </remarks>
public sealed class InvalidStateTransitionException : InvalidOperationException
{
    /// <summary>Creates the exception for a rejected transition.</summary>
    /// <param name="from">State the machine was in.</param>
    /// <param name="to">State that was requested.</param>
    public InvalidStateTransitionException(ConnectionState from, ConnectionState to)
        : base($"A connection cannot move from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    /// <summary>State the machine was in.</summary>
    public ConnectionState From { get; }

    /// <summary>State that was requested.</summary>
    public ConnectionState To { get; }
}
