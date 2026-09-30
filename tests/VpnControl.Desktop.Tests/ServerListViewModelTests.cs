using FluentAssertions;
using VpnControl.Desktop.Tests.Fakes;
using VpnControl.Desktop.ViewModels;

namespace VpnControl.Desktop.Tests;

/// <summary>Covers the list's construction, its annotations and the selection it keeps.</summary>
public sealed class ServerListViewModelTests
{
    [Fact]
    public async Task A_refresh_builds_one_row_per_gateway_in_catalog_order()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.LoadAsync();

        harness.ServerList.Servers.Select(row => row.Id)
            .Should().Equal("de-fra-01", "lt-kun-01", "lt-vln-01", "us-nyc-01");
    }

    [Fact]
    public async Task A_reachable_gateway_gets_a_latency_and_a_score()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.LoadAsync();

        ServerRowViewModel row = harness.ServerList.Servers.Single(r => r.Id == "lt-vln-01");
        row.LatencyMilliseconds.Should().NotBeNull();
        row.Score.Should().NotBeNull();
        row.IsEligible.Should().BeTrue();
        row.LatencyText.Should().EndWith(" ms");
    }

    [Fact]
    public async Task A_gateway_the_operator_disabled_is_shown_with_the_reason_it_was_excluded()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.LoadAsync();

        ServerRowViewModel row = harness.ServerList.Servers.Single(r => r.Id == "us-nyc-01");
        row.IsEligible.Should().BeFalse();
        row.ExclusionReason.Should().Be("Disabled by the operator.");
        row.LatencyText.Should().Be("-");
        row.Details.Should().Contain("Disabled by the operator.");
    }

    [Fact]
    public async Task An_unreachable_gateway_is_excluded_with_the_probe_failure()
    {
        await using TestHarness harness = TestHarness.Create(
            latencies: new Dictionary<string, TimeSpan?> { ["lt-kun-01"] = null });

        await harness.LoadAsync();

        ServerRowViewModel row = harness.ServerList.Servers.Single(r => r.Id == "lt-kun-01");
        row.IsEligible.Should().BeFalse();
        row.ExclusionReason.Should().Be("Simulated as unreachable.");
    }

    [Fact]
    public async Task Exactly_one_row_is_flagged_as_the_fastest()
    {
        await using TestHarness harness = TestHarness.Create(latencies: new Dictionary<string, TimeSpan?>
        {
            ["lt-vln-01"] = TimeSpan.FromMilliseconds(8),
            ["lt-kun-01"] = TimeSpan.FromMilliseconds(30),
            ["de-fra-01"] = TimeSpan.FromMilliseconds(45),
        });

        await harness.LoadAsync();

        harness.ServerList.Servers.Where(row => row.IsFastest).Select(row => row.Id)
            .Should().Equal("lt-vln-01");
        harness.ServerList.Fastest!.Id.Should().Be("lt-vln-01");
    }

    [Fact]
    public async Task The_status_line_counts_the_eligible_and_the_excluded()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.LoadAsync();

        // Three gateways are usable and the one the operator disabled is not.
        harness.ServerList.Status.Should().Be("4 locations, 3 available");
    }

    [Fact]
    public async Task A_simulated_probe_says_so_rather_than_presenting_its_numbers_as_measured()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.LoadAsync();

        harness.ServerList.ProbeDescription.Should().Be("Latency figures are simulated, not measured.");
    }

    [Fact]
    public async Task A_second_refresh_reuses_the_rows_and_keeps_the_selection()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-kun-01");
        ServerRowViewModel selected = harness.ServerList.SelectedServer!;

        await harness.LoadAsync();

        // The same instance, not an equal one: a rebuilt collection would reset the
        // selection and the scroll position on every refresh.
        harness.ServerList.SelectedServer.Should().BeSameAs(selected);
    }

    [Fact]
    public async Task The_first_refresh_selects_the_fastest_gateway_so_the_connect_button_is_usable()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.LoadAsync();

        harness.ServerList.SelectedServer!.IsFastest.Should().BeTrue();
    }

    [Fact]
    public async Task Marking_the_current_gateway_flags_exactly_that_row()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();

        harness.ServerList.MarkCurrent("lt-kun-01");

        harness.ServerList.Servers.Where(row => row.IsCurrent).Select(row => row.Id)
            .Should().Equal("lt-kun-01");

        harness.ServerList.MarkCurrent(null);
        harness.ServerList.Servers.Should().OnlyContain(row => !row.IsCurrent);
    }

    [Fact]
    public async Task IsRefreshing_is_false_again_once_a_refresh_has_finished()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.LoadAsync();

        harness.ServerList.IsRefreshing.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(12.0, 3)]
    [InlineData(79.0, 3)]
    [InlineData(80.0, 2)]
    [InlineData(179.0, 2)]
    [InlineData(180.0, 1)]
    [InlineData(900.0, 1)]
    public void The_signal_bars_follow_the_measured_latency(double? milliseconds, int expected)
    {
        var row = new ServerRowViewModel(VpnControl.Core.Servers.DemoCatalog.CreateServers()[0])
        {
            LatencyMilliseconds = milliseconds,
        };

        row.SignalLevel.Should().Be(expected);
    }

    [Fact]
    public void A_new_latency_announces_the_signal_level()
    {
        var row = new ServerRowViewModel(VpnControl.Core.Servers.DemoCatalog.CreateServers()[0]);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.LatencyMilliseconds = 42;

        changed.Should().Contain(nameof(ServerRowViewModel.SignalLevel));
    }
}
