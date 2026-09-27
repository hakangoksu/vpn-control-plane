namespace VpnControl.Core.Connection;

/// <summary>Settings for <see cref="VpnConnectionManager"/>.</summary>
public sealed class VpnConnectionOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "VpnConnection";

    /// <summary>Label sent with a peer registration so a user can recognise the device.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Whether new tunnels are built as full tunnels with no route around them.</summary>
    public bool KillSwitchEnabled { get; set; }

    /// <summary>MTU applied to the tunnel interface.</summary>
    public int Mtu { get; set; } = 1420;

    /// <summary>How many gateways may be probed at once during a refresh.</summary>
    public int ProbeConcurrency { get; set; } = 8;

    /// <summary>
    /// How long teardown may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Teardown gets its own budget because it runs on the path where something already
    /// went wrong. Letting it inherit a cancelled token would mean a cancelled connect
    /// leaves the interface behind.
    /// </remarks>
    public TimeSpan TeardownTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
