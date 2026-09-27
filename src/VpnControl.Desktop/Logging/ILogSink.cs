using Microsoft.Extensions.Logging;

namespace VpnControl.Desktop.Logging;

/// <summary>
/// Somewhere for a log line to be shown to the user.
/// </summary>
/// <remarks>
/// A VPN client that fails to connect and says only "could not connect" gives the user
/// nothing to act on, so this application shows the log it already writes. The sink is
/// an interface so the logging provider does not have to know that the destination is a
/// view model, and so the view model does not have to know anything about
/// <c>Microsoft.Extensions.Logging</c> plumbing beyond the level enum.
/// </remarks>
public interface ILogSink
{
    /// <summary>Records one line.</summary>
    /// <param name="level">Severity, used to colour the row and to filter.</param>
    /// <param name="category">Logger category, shortened for display by the sink.</param>
    /// <param name="message">The formatted message.</param>
    /// <param name="exception">The exception, when the line carried one.</param>
    /// <remarks>
    /// Called from any thread, so an implementation marshals before touching UI state.
    /// </remarks>
    void Write(LogLevel level, string category, string message, Exception? exception);
}
