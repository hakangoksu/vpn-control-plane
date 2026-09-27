using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VpnControl.Desktop.Logging;
using VpnControl.Desktop.Threading;

namespace VpnControl.Desktop.ViewModels;

/// <summary>
/// The scrolling log pane, and the destination the application's own logging is
/// forwarded to.
/// </summary>
/// <remarks>
/// Implementing <see cref="ILogSink"/> here rather than keeping a separate buffer means
/// there is one collection, the one the view is bound to, and no copying between them.
/// The cost is that this view model is written from background threads, which is why
/// every write goes through the dispatcher.
/// </remarks>
public sealed partial class LogViewModel : ObservableObject, ILogSink
{
    /// <summary>
    /// How many lines are kept.
    /// </summary>
    /// <remarks>
    /// A cap rather than an unbounded list. A client left running overnight with a
    /// reconnect loop would otherwise grow the collection until the process ran out of
    /// memory, and nobody scrolls back two thousand lines anyway.
    /// </remarks>
    public const int MaxEntries = 500;

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the view model.</summary>
    /// <param name="dispatcher">Used to marshal log writes onto the UI thread.</param>
    /// <param name="timeProvider">Clock used to stamp lines, injected for tests.</param>
    public LogViewModel(IUiDispatcher dispatcher, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The lines currently held, oldest first.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>
    /// The newest line, so a collapsed pane can still show the latest status.
    /// </summary>
    [ObservableProperty]
    private LogEntry? _latest;

    /// <inheritdoc />
    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(message);

        // The exception message is appended rather than shown on its own row, so a
        // failure reads as one line. The stack trace is left to the console provider,
        // which is where anyone reading a stack trace is already looking.
        string text = exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})";
        var entry = new LogEntry(_timeProvider.GetUtcNow(), level, ShortenCategory(category), text);

        _dispatcher.Post(() => Append(entry));
    }

    /// <summary>Empties the pane.</summary>
    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        Latest = null;
    }

    /// <summary>Adds a line and trims the oldest ones. Runs on the UI thread only.</summary>
    private void Append(LogEntry entry)
    {
        Entries.Add(entry);
        Latest = entry;

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }

    /// <summary>
    /// Reduces a logger category to its last segment.
    /// </summary>
    /// <remarks>
    /// The full category, for example
    /// <c>VpnControl.Core.Connection.VpnConnectionManager</c>, would take most of the
    /// width of the pane and repeat the same prefix on every line.
    /// </remarks>
    private static string ShortenCategory(string category)
    {
        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }
}
