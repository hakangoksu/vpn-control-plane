namespace VpnControl.Core.Connection;

/// <summary>
/// Every state a VPN session can be in, from the application's point of view.
/// </summary>
/// <remarks>
/// A VPN client with a boolean for "connected" runs out of room almost immediately:
/// the user clicks disconnect while the handshake is still in flight, or switches
/// gateway while connected, and the interface has no honest way to describe what is
/// happening. Naming the in-between states is what makes those cases expressible.
/// </remarks>
public enum ConnectionState
{
    /// <summary>No tunnel, and nothing in progress.</summary>
    Disconnected = 0,

    /// <summary>Registering with the control plane and bringing the interface up.</summary>
    Connecting = 1,

    /// <summary>The tunnel is up.</summary>
    Connected = 2,

    /// <summary>
    /// Moving an established session to a different gateway.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Connecting"/> because the user's expectation differs: a
    /// switch that fails should say so against a session the user thought was working,
    /// and the interface should not flash back to a disconnected look on the way.
    /// </remarks>
    Switching = 3,

    /// <summary>Tearing the tunnel down and releasing the registration.</summary>
    Disconnecting = 4,

    /// <summary>
    /// Something failed and the session is not usable.
    /// </summary>
    /// <remarks>
    /// A separate state rather than a return to <see cref="Disconnected"/>, so the
    /// interface can keep showing the reason instead of quietly looking idle.
    /// </remarks>
    Faulted = 5,
}
