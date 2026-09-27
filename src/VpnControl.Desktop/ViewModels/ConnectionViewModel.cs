using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VpnControl.Core.Connection;
using VpnControl.Core.Tunneling;
using VpnControl.Desktop.Threading;

namespace VpnControl.Desktop.ViewModels;

/// <summary>
/// The session panel: what state the tunnel is in, how long it has been up, and how much
/// has gone through it.
/// </summary>
/// <remarks>
/// Read only by design. It reflects the session and polls its counters; the commands that
/// change the session live on <see cref="MainWindowViewModel"/>, which can see both this
/// and the gateway list. Splitting it that way keeps the thing that renders the state
/// separate from the thing that decides what a button press means.
/// <para>
/// Counters are polled rather than pushed because that is what the underlying interface
/// offers: WireGuard exposes byte counts to be read, not an event to subscribe to, so a
/// client that wanted to push would be polling underneath anyway.
/// </para>
/// </remarks>
public sealed partial class ConnectionViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>How often the counters and the elapsed time are refreshed.</summary>
    /// <remarks>
    /// One second matches what the numbers are worth: they are shown to the nearest
    /// second and the nearest kibibyte, so a faster poll would only spend wakeups.
    /// </remarks>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly VpnConnectionManager _manager;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ConnectionViewModel> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _monitorCts = new();

    private Task? _monitorTask;
    private bool _disposed;

    /// <summary>Creates the view model and subscribes to session state changes.</summary>
    /// <param name="manager">The session being reflected.</param>
    /// <param name="dispatcher">Used to apply background updates on the UI thread.</param>
    /// <param name="logger">Destination for polling failures.</param>
    /// <param name="timeProvider">Clock used for the elapsed time and for the poll timer.</param>
    public ConnectionViewModel(
        VpnConnectionManager manager,
        IUiDispatcher dispatcher,
        ILogger<ConnectionViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _dispatcher = dispatcher;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        TunnelDescription = manager.IsSimulated
            ? $"{manager.TunnelName}: no packets are being moved and no network state is changed."
            : $"{manager.TunnelName}: a real interface is created.";
        IsSimulated = manager.IsSimulated;
        KillSwitchEnabled = manager.KillSwitchEnabled;

        // The manager raises this on whichever thread performed the transition, which is
        // a thread pool thread for everything except the very first change.
        _manager.StateChanged += OnStateChanged;
    }

    /// <summary>Current session state.</summary>
    [ObservableProperty]
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>The reason carried by the last transition, shown under the state.</summary>
    [ObservableProperty]
    private string _detail = "Not connected.";

    /// <summary>Name of the gateway in use, or a dash when there is none.</summary>
    [ObservableProperty]
    private string _serverName = "-";

    /// <summary>Endpoint the tunnel points at, or a dash when there is none.</summary>
    [ObservableProperty]
    private string _endpoint = "-";

    /// <summary>How long the current session has been up.</summary>
    [ObservableProperty]
    private TimeSpan _elapsed;

    /// <summary>Bytes received since the tunnel came up.</summary>
    [ObservableProperty]
    private long _bytesReceived;

    /// <summary>Bytes sent since the tunnel came up.</summary>
    [ObservableProperty]
    private long _bytesSent;

    /// <summary>
    /// When the peer last completed a handshake, or <c>null</c> when it never has.
    /// </summary>
    [ObservableProperty]
    private DateTimeOffset? _lastHandshake;

    /// <summary>Whether new tunnels are built with the kill switch on.</summary>
    [ObservableProperty]
    private bool _killSwitchEnabled;

    /// <summary>Whether the active backend only simulates a tunnel.</summary>
    public bool IsSimulated { get; }

    /// <summary>What the active backend does, spelled out for the banner.</summary>
    public string TunnelDescription { get; }

    /// <summary>Whether the tunnel is up.</summary>
    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>Whether an operation is in flight, so the view can disable its buttons.</summary>
    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Switching or ConnectionState.Disconnecting;

    /// <summary>Whether the session ended in a failure the user should see.</summary>
    public bool IsFaulted => State == ConnectionState.Faulted;

    /// <summary>The state as a word for the status line.</summary>
    public string StateText => State switch
    {
        ConnectionState.Disconnected => "Disconnected",
        ConnectionState.Connecting => "Connecting",
        ConnectionState.Connected => "Connected",
        ConnectionState.Switching => "Switching gateway",
        ConnectionState.Disconnecting => "Disconnecting",
        ConnectionState.Faulted => "Failed",
        _ => State.ToString(),
    };

    /// <summary>Elapsed session time as <c>hh:mm:ss</c>.</summary>
    public string ElapsedText => Elapsed == TimeSpan.Zero
        ? "-"
        : Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>Bytes received, in units a person reads.</summary>
    public string BytesReceivedText => FormatBytes(BytesReceived);

    /// <summary>Bytes sent, in units a person reads.</summary>
    public string BytesSentText => FormatBytes(BytesSent);

    /// <summary>Age of the last handshake, or a dash when there has not been one.</summary>
    /// <remarks>
    /// Shown as an age rather than a timestamp because the age is the part that matters:
    /// WireGuard has no session to close, so a tunnel whose last handshake is minutes old
    /// is one whose peer has gone away, even though the interface is still there.
    /// </remarks>
    public string HandshakeText => LastHandshake is DateTimeOffset handshake
        ? string.Create(CultureInfo.InvariantCulture, $"{(_timeProvider.GetUtcNow() - handshake).TotalSeconds:F0} s ago")
        : "-";

    /// <summary>
    /// Starts the polling loop.
    /// </summary>
    /// <remarks>
    /// Separate from the constructor so nothing runs in the background before the window
    /// that displays it exists, and so a test can construct the view model without a loop
    /// it then has to stop.
    /// </remarks>
    public void StartMonitoring()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The task is stored rather than discarded, so that disposal can wait for the loop
        // to finish instead of leaving it half way through a poll. Not async void: the
        // exception from an async void method has nowhere to go but the finalizer thread.
        _monitorTask ??= MonitorAsync(_monitorCts.Token);
    }

    /// <summary>Applies one reading of the session, on the UI thread.</summary>
    /// <param name="statistics">Counters read from the tunnel.</param>
    /// <remarks>
    /// Public so a test can drive the display without running the timer loop.
    /// </remarks>
    public void ApplyStatistics(TunnelStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        BytesReceived = statistics.BytesReceived;
        BytesSent = statistics.BytesSent;
        LastHandshake = statistics.LastHandshake;
        Endpoint = statistics.Endpoint ?? _manager.CurrentServer?.Endpoint ?? "-";
        Elapsed = _manager.ConnectedSince is DateTimeOffset since
            ? _timeProvider.GetUtcNow() - since
            : TimeSpan.Zero;

        // Recomputed every tick because it ages on its own, with no property changing.
        OnPropertyChanged(nameof(HandshakeText));
    }

    /// <summary>Records the kill switch setting the manager now holds.</summary>
    /// <param name="enabled">The setting in effect.</param>
    public void ApplyKillSwitch(bool enabled) => KillSwitchEnabled = enabled;

    /// <summary>Stops the polling loop and unsubscribes.</summary>
    /// <returns>A task that completes once the loop has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StateChanged -= OnStateChanged;

        await _monitorCts.CancelAsync().ConfigureAwait(false);

        if (_monitorTask is Task running)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelling the timer is how the loop is asked to stop.
            }
        }

        _monitorCts.Dispose();
    }

    /// <summary>Polls the tunnel counters until cancelled.</summary>
    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                TunnelStatistics statistics = await _manager.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
                _dispatcher.Post(() => ApplyStatistics(statistics));
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            // A failed read of the counters is not a reason to bring the application down,
            // but it must not vanish either: the loop stops and the log says why.
            _logger.LogError(ex, "Stopped polling the tunnel counters.");
        }
    }

    /// <summary>Copies a session transition onto the bound properties.</summary>
    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e) =>
        _dispatcher.Post(() =>
        {
            State = e.Current;
            Detail = e.Reason ?? StateText;
            ServerName = _manager.CurrentServer?.Name ?? "-";
            Endpoint = _manager.CurrentServer?.Endpoint ?? "-";
            KillSwitchEnabled = _manager.KillSwitchEnabled;

            if (e.Current is ConnectionState.Disconnected or ConnectionState.Faulted)
            {
                // Leaving the last session's counters on screen next to a disconnected
                // status would read as though something were still flowing.
                BytesReceived = 0;
                BytesSent = 0;
                Elapsed = TimeSpan.Zero;
                LastHandshake = null;
            }
        });

    /// <summary>
    /// Formats a byte count in binary units.
    /// </summary>
    /// <remarks>
    /// KiB rather than kB, because these are counters of bytes moved and the tooling this
    /// project sits next to reports the same way. One decimal place is enough to see the
    /// number move without it jittering.
    /// </remarks>
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1} {units[unit]}");
    }

    /// <summary>Announces the properties computed from <see cref="State"/> and the counters.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPropertyChanged(e);

        switch (e.PropertyName)
        {
            case nameof(State):
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsFaulted));
                break;
            case nameof(Elapsed):
                OnPropertyChanged(nameof(ElapsedText));
                break;
            case nameof(BytesReceived):
                OnPropertyChanged(nameof(BytesReceivedText));
                break;
            case nameof(BytesSent):
                OnPropertyChanged(nameof(BytesSentText));
                break;
            case nameof(LastHandshake):
                OnPropertyChanged(nameof(HandshakeText));
                break;
            default:
                break;
        }
    }
}
