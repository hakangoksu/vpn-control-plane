using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VpnControl.Core.Connection;
using VpnControl.Core.Servers;
using VpnControl.Core.Tunneling;
using VpnControl.Desktop.Threading;

namespace VpnControl.Desktop.ViewModels;

/// <summary>
/// The window's view model, and the only place that decides what a button press means.
/// </summary>
/// <remarks>
/// It composes the three panes rather than holding their state, so each of them stays
/// small and separately testable, and it owns the commands because every interesting one
/// needs to see more than one pane: connecting needs the list's selection and the
/// session's state, and the primary button has to know whether it is starting a session
/// or moving one.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly VpnConnectionManager _manager;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<MainWindowViewModel> _logger;
    private bool _disposed;

    /// <summary>Creates the view model.</summary>
    /// <param name="manager">Session orchestrator every command acts on.</param>
    /// <param name="connection">The session pane.</param>
    /// <param name="serverList">The gateway pane.</param>
    /// <param name="log">The log pane.</param>
    /// <param name="dispatcher">Used to apply background updates on the UI thread.</param>
    /// <param name="logger">Destination for command failures.</param>
    public MainWindowViewModel(
        VpnConnectionManager manager,
        ConnectionViewModel connection,
        ServerListViewModel serverList,
        LogViewModel log,
        IUiDispatcher dispatcher,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(serverList);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _dispatcher = dispatcher;
        _logger = logger;

        Connection = connection;
        ServerList = serverList;
        Log = log;

        // The commands are enabled or disabled by the session state and by the selection,
        // neither of which this object owns, so it listens to both children.
        Connection.PropertyChanged += OnConnectionPropertyChanged;
        ServerList.PropertyChanged += OnServerListPropertyChanged;
    }

    /// <summary>The session pane.</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>The gateway pane.</summary>
    public ServerListViewModel ServerList { get; }

    /// <summary>The log pane.</summary>
    public LogViewModel Log { get; }

    /// <summary>Window title.</summary>
    public string Title { get; } = "VPN Control Plane";

    /// <summary>The last command failure, or <c>null</c> when the last one succeeded.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>
    /// Label for the primary button, which changes with what pressing it would do.
    /// </summary>
    /// <remarks>
    /// One button rather than separate connect and switch buttons. With a session up and a
    /// different gateway selected, "connect" is not what the user means and a second
    /// button they can only press in one state is clutter.
    /// </remarks>
    public string PrimaryActionText => Connection.State switch
    {
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Switching => "Switching…",
        ConnectionState.Disconnecting => "Disconnecting…",
        ConnectionState.Connected when IsSelectionTheCurrentServer || ServerList.SelectedServer is null => "Disconnect",
        ConnectionState.Connected => $"Switch to {ServerList.SelectedServer!.City}",
        _ when ServerList.SelectedServer is ServerRowViewModel row => $"Connect to {row.City}",
        _ => "Connect",
    };

    /// <summary>
    /// Whether the primary button currently ends the session, so the view can style it as the
    /// quieter, secondary kind of action rather than the call to action.
    /// </summary>
    public bool PrimaryDisconnects =>
        Connection.State == ConnectionState.Connected &&
        (IsSelectionTheCurrentServer || ServerList.SelectedServer is null);

    /// <summary>Whether the selected row is the gateway the session already uses.</summary>
    public bool IsSelectionTheCurrentServer =>
        ServerList.SelectedServer is ServerRowViewModel row &&
        _manager.CurrentServer is VpnServer current &&
        string.Equals(row.Id, current.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads the catalog for the first time.</summary>
    /// <param name="cancellationToken">Abandons the load.</param>
    /// <returns>A task that completes once the first refresh has finished.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Connection.StartMonitoring();
        await ServerList.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to the selected gateway, or moves an established session to it.
    /// </summary>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>A task that completes once the tunnel is up or the attempt has failed.</returns>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (ServerList.SelectedServer is not ServerRowViewModel row)
        {
            return;
        }

        await RunAsync(
            () => _manager.IsConnected
                ? _manager.SwitchToAsync(row.Server, cancellationToken)
                : _manager.ConnectAsync(row.Server, cancellationToken),
            $"connect to {row.Name}").ConfigureAwait(false);
    }

    /// <summary>
    /// The one main button: connect to the selection, switch to it, or disconnect.
    /// </summary>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>A task that completes once the operation has finished.</returns>
    /// <remarks>
    /// One button whose meaning follows the state, with the label saying exactly what a press
    /// does ("Connect to Riga", "Switch to Paris", "Disconnect"). Two buttons side by side,
    /// one of them always disabled, made the user work out which one applied.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunPrimary))]
    private Task PrimaryAsync(CancellationToken cancellationToken) =>
        PrimaryDisconnects ? DisconnectAsync(cancellationToken) : ConnectAsync(cancellationToken);

    /// <summary>Refreshes, then connects to whichever gateway ranks first.</summary>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>A task that completes once the tunnel is up or the attempt has failed.</returns>
    /// <remarks>
    /// Re-measures rather than trusting the last ranking, because the point of the action
    /// is to use the fastest gateway now, and the list may have been probed minutes ago.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanConnectToFastest))]
    private async Task ConnectToFastestAsync(CancellationToken cancellationToken)
    {
        await RunAsync(
            async () =>
            {
                VpnServer chosen = await _manager.ConnectToFastestAsync(cancellationToken).ConfigureAwait(false);
                _dispatcher.Post(() => ServerList.SelectedServer =
                    ServerList.Servers.FirstOrDefault(row => string.Equals(row.Id, chosen.Id, StringComparison.OrdinalIgnoreCase)));
            },
            "connect to the fastest gateway").ConfigureAwait(false);
    }

    /// <summary>Tears the session down.</summary>
    /// <param name="cancellationToken">Abandons the wait for the operation gate.</param>
    /// <returns>A task that completes once the tunnel is down or the attempt has failed.</returns>
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync(CancellationToken cancellationToken) =>
        await RunAsync(() => _manager.DisconnectAsync(cancellationToken), "disconnect").ConfigureAwait(false);

    /// <summary>
    /// Turns the kill switch on or off.
    /// </summary>
    /// <param name="cancellationToken">Abandons the re-apply, when one is needed.</param>
    /// <returns>A task that completes once the setting is in effect.</returns>
    /// <remarks>
    /// A command rather than a two-way bound property. Changing the setting while a tunnel
    /// is up means rebuilding it, which is asynchronous work that a property setter has no
    /// honest way to perform.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanToggleKillSwitch))]
    private async Task ToggleKillSwitchAsync(CancellationToken cancellationToken)
    {
        bool desired = !_manager.KillSwitchEnabled;

        await RunAsync(
            () => _manager.SetKillSwitchAsync(desired, cancellationToken),
            desired ? "enable the kill switch" : "disable the kill switch").ConfigureAwait(false);

        _dispatcher.Post(() => Connection.ApplyKillSwitch(_manager.KillSwitchEnabled));
    }

    /// <summary>Re-measures the catalog.</summary>
    /// <param name="cancellationToken">Abandons the refresh.</param>
    /// <returns>A task that completes once the list has been updated.</returns>
    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => ServerList.RefreshAsync(cancellationToken);

    /// <summary>Unsubscribes and stops the session pane's polling loop.</summary>
    /// <returns>A task that completes once the panes have been released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        ServerList.PropertyChanged -= OnServerListPropertyChanged;

        await Connection.DisposeAsync().ConfigureAwait(false);
    }

    private bool CanConnect() =>
        !Connection.IsBusy && ServerList.SelectedServer is not null && !IsSelectionTheCurrentServer;

    private bool CanConnectToFastest() => !Connection.IsBusy;

    private bool CanRunPrimary() => PrimaryDisconnects ? CanDisconnect() : CanConnect();

    private bool CanDisconnect() => Connection.State is not (ConnectionState.Disconnected or ConnectionState.Disconnecting);

    private bool CanToggleKillSwitch() => !Connection.IsBusy;

    /// <summary>
    /// Runs one session operation, turning a failure into a message rather than a crash.
    /// </summary>
    /// <remarks>
    /// The manager has already logged the failure and moved the state machine to
    /// <see cref="ConnectionState.Faulted"/>, so there is nothing left to do here except
    /// give the window something to show. The exception types caught are the ones the
    /// manager documents; anything else is a defect and should not be swallowed.
    /// </remarks>
    private async Task RunAsync(Func<Task> operation, string description)
    {
        _dispatcher.Post(() => Error = null);

        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => Error = $"Cancelled while trying to {description}.");
        }
        catch (Exception ex) when (ex is ServerCatalogException or TunnelException or NoServerAvailableException or InvalidStateTransitionException)
        {
            _logger.LogWarning("Could not {Description}: {Reason}", description, ex.Message);
            _dispatcher.Post(() => Error = $"Could not {description}: {ex.Message}");
        }
        finally
        {
            _dispatcher.Post(() =>
            {
                ServerList.MarkCurrent(_manager.CurrentServer?.Id);
                NotifyCommandStates();
            });
        }
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.State) or nameof(ConnectionViewModel.IsBusy))
        {
            NotifyCommandStates();
        }
    }

    private void OnServerListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerListViewModel.SelectedServer))
        {
            NotifyCommandStates();
        }
    }

    /// <summary>Re-evaluates every command's availability and the primary button's label.</summary>
    private void NotifyCommandStates()
    {
        PrimaryCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        ConnectToFastestCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ToggleKillSwitchCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(IsSelectionTheCurrentServer));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryDisconnects));
    }
}
