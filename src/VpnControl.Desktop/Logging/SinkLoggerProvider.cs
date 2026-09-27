using Microsoft.Extensions.Logging;
using VpnControl.Desktop.Threading;

namespace VpnControl.Desktop.Logging;

/// <summary>
/// A logging provider that forwards every line to an <see cref="ILogSink"/>.
/// </summary>
/// <remarks>
/// Registered alongside the console provider rather than instead of it, so the same
/// diagnostics reach a terminal and the window. Writing a provider by hand here is a
/// few lines and avoids a dependency whose only job would be to copy strings into a
/// list.
/// </remarks>
/// <param name="sink">Destination for the lines.</param>
public sealed class SinkLoggerProvider(ILogSink sink) : ILoggerProvider
{
    private readonly ILogSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new SinkLogger(_sink, categoryName);

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to release. The sink outlives the provider because it is the view model
    /// the window is bound to, so disposing it here would empty the log on shutdown.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <summary>The per-category logger the provider hands out.</summary>
    private sealed class SinkLogger(ILogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        // Debug and trace from the Avalonia internals would drown the window, so the
        // floor is set here rather than in configuration: this provider has a different
        // audience from the console one and wants a different level.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            sink.Write(logLevel, category, formatter(state, exception), exception);
        }
    }

    /// <summary>A scope that carries nothing, for a provider that ignores scopes.</summary>
    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
