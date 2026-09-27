namespace VpnControl.Core.Tunneling;

/// <summary>
/// Raised when a tunnel backend cannot do what it was asked.
/// </summary>
/// <remarks>
/// A backend drives external tooling, so it can fail in ways that mean very different
/// things: the tool is not installed, the user is not privileged, the configuration was
/// rejected. Those all arrive as a non-zero exit code and some text, so this type keeps
/// the text and adds a flag for the one distinction the user interface has to act on,
/// which is whether asking for privileges would help.
/// </remarks>
public sealed class TunnelException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What the backend could not do.</param>
    public TunnelException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying failure.</summary>
    /// <param name="message">What the backend could not do.</param>
    /// <param name="innerException">The process or IO failure behind it.</param>
    public TunnelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Whether the operation would plausibly succeed with elevated privileges.
    /// </summary>
    /// <remarks>
    /// Creating a network interface is privileged everywhere. When this is <c>true</c>,
    /// the right response is to ask for elevation rather than to show a stack trace.
    /// </remarks>
    public bool IsPrivilegeProblem { get; init; }

    /// <summary>Exit code of the external tool, when one was run.</summary>
    public int? ExitCode { get; init; }
}
