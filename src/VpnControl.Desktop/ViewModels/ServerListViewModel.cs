using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VpnControl.Core.Connection;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Desktop.Threading;

namespace VpnControl.Desktop.ViewModels;

/// <summary>
/// The gateway list: fetches the catalog, shows what the probes measured, and holds the
/// user's selection.
/// </summary>
/// <remarks>
/// Owns no connection logic. It asks the connection manager to refresh and turns the
/// result into rows; deciding what to do with a selected gateway belongs to
/// <see cref="MainWindowViewModel"/>, which can see both this list and the session.
/// </remarks>
public sealed partial class ServerListViewModel : ObservableObject
{
    private readonly VpnConnectionManager _manager;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ServerListViewModel> _logger;

    /// <summary>Creates the view model.</summary>
    /// <param name="manager">Session orchestrator the refresh is asked of.</param>
    /// <param name="dispatcher">Used to apply results on the UI thread.</param>
    /// <param name="logger">Destination for refresh failures.</param>
    public ServerListViewModel(
        VpnConnectionManager manager,
        IUiDispatcher dispatcher,
        ILogger<ServerListViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>The rows, in catalog order.</summary>
    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];

    /// <summary>The row the user has selected, or <c>null</c> when none is.</summary>
    [ObservableProperty]
    private ServerRowViewModel? _selectedServer;

    /// <summary>Whether a refresh is in flight, so the view can disable the button.</summary>
    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>One line describing the last refresh, shown under the list.</summary>
    [ObservableProperty]
    private string _status = "No gateways loaded yet.";

    /// <summary>
    /// How the latency column was measured, so a simulated figure is never presented as
    /// a real one.
    /// </summary>
    [ObservableProperty]
    private string _probeDescription = string.Empty;

    /// <summary>The gateway the last ranking put first, or <c>null</c> when none qualified.</summary>
    [ObservableProperty]
    private VpnServer? _fastest;

    /// <summary>
    /// Fetches the catalog, probes it and rebuilds the rows.
    /// </summary>
    /// <param name="cancellationToken">Abandons the refresh.</param>
    /// <returns>A task that completes once the rows have been updated.</returns>
    /// <remarks>
    /// A failure is reported in the status line and logged rather than thrown. A command
    /// that throws on a background thread takes the process with it, and the user's next
    /// action is to press refresh again, which they can only do if the window is still up.
    /// </remarks>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsRefreshing = true;
        Status = "Fetching the gateway catalog.";

        try
        {
            ServerSelectionResult selection = await _manager.RefreshAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() => Apply(_manager.Catalog, selection));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _dispatcher.Post(() => Status = "Refresh cancelled.");
        }
        catch (ServerCatalogException ex)
        {
            _logger.LogError(ex, "Could not refresh the gateway catalog.");
            _dispatcher.Post(() => Status = $"Could not reach the control plane: {ex.Message}");
        }
        finally
        {
            _dispatcher.Post(() => IsRefreshing = false);
        }
    }

    /// <summary>
    /// Rebuilds the rows from a catalog and a ranking.
    /// </summary>
    /// <param name="catalog">The gateways to show.</param>
    /// <param name="selection">The ranking to annotate them with.</param>
    /// <remarks>
    /// Rows are matched by identifier and reused rather than recreated, so the user's
    /// selection and the list's scroll position survive a refresh. Rebuilding the
    /// collection from scratch is simpler and loses both.
    /// </remarks>
    public void Apply(ServerCatalog catalog, ServerSelectionResult selection)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);

        var existing = Servers.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < catalog.Servers.Count; index++)
        {
            VpnServer server = catalog.Servers[index];
            kept.Add(server.Id);

            if (!existing.TryGetValue(server.Id, out ServerRowViewModel? row))
            {
                row = new ServerRowViewModel(server);
                Servers.Insert(Math.Min(index, Servers.Count), row);
            }
            else if (Servers.IndexOf(row) != index)
            {
                Servers.Move(Servers.IndexOf(row), index);
            }
        }

        // A gateway the operator has withdrawn disappears from the list. Holding a stale
        // row would offer the user a connection that cannot be made.
        for (int index = Servers.Count - 1; index >= 0; index--)
        {
            if (!kept.Contains(Servers[index].Id))
            {
                if (ReferenceEquals(SelectedServer, Servers[index]))
                {
                    SelectedServer = null;
                }

                Servers.RemoveAt(index);
            }
        }

        Annotate(selection);
        MarkCurrent(_manager.CurrentServer?.Id);

        Fastest = selection.Best;
        ProbeDescription = _manager.IsSimulated
            ? "Latency figures are simulated, not measured."
            : $"Latency is measured with {_manager.LatencyProbeName} to the gateway host.";

        Status = string.Create(
            CultureInfo.InvariantCulture,
            $"{catalog.Count} locations, {selection.Ranked.Count} available");

        SelectedServer ??= Servers.FirstOrDefault(row => row.IsFastest) ?? Servers.FirstOrDefault();
    }

    /// <summary>Flags the row belonging to the gateway in use, and clears the others.</summary>
    /// <param name="serverId">Identifier of the gateway in use, or <c>null</c> when idle.</param>
    public void MarkCurrent(string? serverId)
    {
        foreach (ServerRowViewModel row in Servers)
        {
            row.IsCurrent = serverId is not null && string.Equals(row.Id, serverId, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Copies a ranking onto the rows it describes.</summary>
    private void Annotate(ServerSelectionResult selection)
    {
        var rankings = selection.Ranked.ToDictionary(r => r.Server.Id, StringComparer.OrdinalIgnoreCase);
        var exclusions = selection.Excluded.ToDictionary(e => e.Server.Id, e => e.Reason, StringComparer.OrdinalIgnoreCase);
        string? bestId = selection.Best?.Id;

        foreach (ServerRowViewModel row in Servers)
        {
            if (rankings.TryGetValue(row.Id, out ServerRanking? ranking))
            {
                row.ApplyRanking(ranking);
            }
            else if (exclusions.TryGetValue(row.Id, out string? reason))
            {
                row.ApplyExclusion(reason);
            }

            row.IsFastest = bestId is not null && string.Equals(row.Id, bestId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
