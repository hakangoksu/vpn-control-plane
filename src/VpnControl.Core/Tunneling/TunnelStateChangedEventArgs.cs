namespace VpnControl.Core.Tunneling;

/// <summary>Carries a backend's state change to whoever is listening.</summary>
/// <param name="previous">State the backend was in.</param>
/// <param name="current">State it is in now.</param>
/// <param name="detail">Optional explanation, for example the error that faulted it.</param>
public sealed class TunnelStateChangedEventArgs(TunnelState previous, TunnelState current, string? detail = null)
    : EventArgs
{
    /// <summary>State the backend was in.</summary>
    public TunnelState Previous { get; } = previous;

    /// <summary>State the backend is in now.</summary>
    public TunnelState Current { get; } = current;

    /// <summary>Optional explanation of the change.</summary>
    public string? Detail { get; } = detail;
}
