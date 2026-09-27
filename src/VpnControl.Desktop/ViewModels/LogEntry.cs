using System.Globalization;
using Microsoft.Extensions.Logging;

namespace VpnControl.Desktop.ViewModels;

/// <summary>
/// One line in the log pane.
/// </summary>
/// <param name="Timestamp">When the line was written.</param>
/// <param name="Level">Severity, which the view turns into a colour.</param>
/// <param name="Category">Logger category, already shortened to its last segment.</param>
/// <param name="Message">The formatted message, with any exception message appended.</param>
/// <remarks>
/// Immutable, because a row that is already in the bound collection must not change
/// underneath the list. A new line is a new instance.
/// </remarks>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    /// <summary>Time of day, to the second, for the leading column.</summary>
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Four letter severity tag, so the column does not jump about in width.</summary>
    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "NONE",
    };
}
