namespace VpnControl.Core.Tunneling;

/// <summary>
/// What a tunnel backend knows about its own interface.
/// </summary>
/// <remarks>
/// Smaller than the connection state the rest of the application tracks, and that is
/// intentional. A backend knows whether an interface exists; it does not know whether
/// the application is in the middle of switching gateways or waiting on the control
/// plane. Giving the backend the larger vocabulary would mean two places deciding what
/// "connecting" means.
/// </remarks>
public enum TunnelState
{
    /// <summary>No interface is configured.</summary>
    Down = 0,

    /// <summary>The interface is configured and carrying traffic.</summary>
    Up = 1,

    /// <summary>The backend tried to bring the interface up or down and failed.</summary>
    Faulted = 2,
}
