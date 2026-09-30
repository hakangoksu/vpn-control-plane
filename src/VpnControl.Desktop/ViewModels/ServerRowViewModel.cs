using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;

namespace VpnControl.Desktop.ViewModels;

/// <summary>
/// One row of the gateway list: a gateway, what the last probe said about it, and its
/// place in the ranking.
/// </summary>
/// <remarks>
/// A wrapper rather than binding the list straight to <see cref="VpnServer"/>. The
/// gateway is an immutable record that the catalog owns, while a row has to change as
/// probes come in and as the session moves, and it carries things the domain model has
/// no business knowing: whether this is the row the user is connected to, and how the
/// latency should read when the gateway did not answer.
/// </remarks>
/// <param name="server">The gateway this row stands for.</param>
public sealed partial class ServerRowViewModel(VpnServer server) : ObservableObject
{
    /// <summary>The gateway, for the code that acts on a selection.</summary>
    public VpnServer Server { get; } = server ?? throw new ArgumentNullException(nameof(server));

    /// <summary>Gateway identifier, used as the list item key.</summary>
    public string Id => Server.Id;

    /// <summary>Display name, for example <c>Vilnius 1</c>.</summary>
    public string Name => Server.Name;

    /// <summary>City and country, for example <c>Vilnius, LT</c>.</summary>
    public string Location => Server.Location;

    /// <summary>Reported utilisation, 0 to 100.</summary>
    public int LoadPercent => Server.LoadPercent;

    /// <summary>Utilisation as text with its unit, for the load column.</summary>
    public string LoadText => string.Create(CultureInfo.InvariantCulture, $"{Server.LoadPercent}%");

    /// <summary>
    /// Measured round trip in milliseconds, or <c>null</c> when the gateway did not answer.
    /// </summary>
    /// <remarks>
    /// Kept as a number as well as text so the view can colour the column by value.
    /// </remarks>
    [ObservableProperty]
    private double? _latencyMilliseconds;

    /// <summary>Why this gateway was excluded from the ranking, or <c>null</c> when it was not.</summary>
    [ObservableProperty]
    private string? _exclusionReason;

    /// <summary>Whether this gateway came first in the last ranking.</summary>
    [ObservableProperty]
    private bool _isFastest;

    /// <summary>Whether the current session runs through this gateway.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Score the selector gave it, or <c>null</c> when it was excluded.</summary>
    [ObservableProperty]
    private double? _score;

    /// <summary>Whether this gateway is a candidate the user can connect to.</summary>
    public bool IsEligible => ExclusionReason is null;

    /// <summary>City the gateway is in, the row's main label.</summary>
    public string City => Server.City;

    /// <summary>ISO country code, shown as a compact badge in place of a flag.</summary>
    /// <remarks>
    /// A code rather than a flag glyph: flag emoji do not render on every platform and font,
    /// and a two-letter code is unambiguous where a small flag often is not.
    /// </remarks>
    public string CountryCode => Server.Country;

    /// <summary>Whether IPv6 leaves through this gateway.</summary>
    public bool HasIpv6 => Server.Ipv6Egress;

    /// <summary>Whether the gateway is busy enough to be worth mentioning.</summary>
    /// <remarks>
    /// The load figure is only shown when it says something. A column of zeros is noise, and
    /// a tag that appears at 70 percent occupancy tells the user why a closer gateway may not
    /// be the first choice.
    /// </remarks>
    public bool IsBusy => Server.LoadPercent >= 70;

    /// <summary>
    /// Connection quality from zero to three bars, derived from the measured latency.
    /// </summary>
    /// <remarks>
    /// Bars rather than a colour for the number. Colouring 150 ms red told the user something
    /// was wrong when nothing was; red is kept for failures. The thresholds are presentation
    /// choices for a VPN's use: under 80 ms feels local, under 180 ms is comfortable for
    /// browsing and calls, above that it is noticeable. Zero bars means no answer.
    /// </remarks>
    public int SignalLevel => LatencyMilliseconds switch
    {
        null => 0,
        < 80 => 3,
        < 180 => 2,
        _ => 1,
    };

    /// <summary>Latency for the column, or a dash when there is no measurement.</summary>
    public string LatencyText => LatencyMilliseconds is double ms
        ? string.Create(CultureInfo.InvariantCulture, $"{ms:F0} ms")
        : "-";

    /// <summary>
    /// One line summarising the row, shown as the tooltip.
    /// </summary>
    /// <remarks>
    /// The exclusion reason is the useful half of a ranking and would otherwise have
    /// nowhere to go: a greyed out row that does not say why is a support question.
    /// </remarks>
    public string Details => ExclusionReason is string reason
        ? $"{Server.Endpoint} is not a candidate: {reason}"
        : string.Create(CultureInfo.InvariantCulture, $"{Server.Endpoint}, score {Score ?? 0:F0}, load {Server.LoadPercent}%");

    /// <summary>Records a successful measurement and its place in the ranking.</summary>
    /// <param name="ranking">The ranking entry for this gateway.</param>
    public void ApplyRanking(ServerRanking ranking)
    {
        ArgumentNullException.ThrowIfNull(ranking);

        LatencyMilliseconds = ranking.RoundTrip.TotalMilliseconds;
        Score = ranking.Score;
        ExclusionReason = null;
    }

    /// <summary>Records that this gateway is not a candidate.</summary>
    /// <param name="reason">Why it was excluded, in the selector's words.</param>
    public void ApplyExclusion(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        LatencyMilliseconds = null;
        Score = null;
        ExclusionReason = reason;
    }

    /// <summary>
    /// Raises change notifications for the properties computed from the observable ones.
    /// </summary>
    /// <remarks>
    /// The generated setters notify their own property only, so a text or tooltip derived
    /// from several of them has to be announced by hand. Doing it in one override keeps
    /// the list in a single place instead of spread over four partial methods.
    /// </remarks>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPropertyChanged(e);

        switch (e.PropertyName)
        {
            case nameof(LatencyMilliseconds):
                OnPropertyChanged(nameof(LatencyText));
                OnPropertyChanged(nameof(SignalLevel));
                OnPropertyChanged(nameof(Details));
                break;
            case nameof(ExclusionReason):
                OnPropertyChanged(nameof(IsEligible));
                OnPropertyChanged(nameof(Details));
                break;
            case nameof(Score):
                OnPropertyChanged(nameof(Details));
                break;
            default:
                break;
        }
    }
}
