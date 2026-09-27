using FluentAssertions;
using Microsoft.Extensions.Options;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Core.Tests.Fakes;
using Xunit;

namespace VpnControl.Core.Tests.Latency;

public sealed class ServerSelectorTests
{
    private static ServerSelector Selector(double loadWeight = 1.0, int maxLoad = 95, TimeSpan? maxLatency = null) =>
        new(Options.Create(new ServerSelectionOptions
        {
            LoadWeight = loadWeight,
            MaxLoadPercent = maxLoad,
            MaxLatency = maxLatency,
        }));

    private static LatencyMeasurement Reachable(string id, double milliseconds, int load = 0, bool enabled = true) =>
        LatencyMeasurement.Success(
            TestServers.Create(id, loadPercent: load, isEnabled: enabled),
            TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public void The_lowest_latency_gateway_wins_when_load_is_equal()
    {
        ServerSelectionResult result = Selector().Select([
            Reachable("slow", 120),
            Reachable("fast", 20),
            Reachable("middling", 60),
        ]);

        result.Best!.Id.Should().Be("fast");
        result.Ranked.Select(r => r.Server.Id).Should().Equal("fast", "middling", "slow");
    }

    [Fact]
    public void Load_weighting_can_beat_a_lower_raw_latency()
    {
        // 30 ms at 90 percent load scores 57. 40 ms idle scores 40, so the idle gateway wins
        // even though the loaded one is closer.
        ServerSelectionResult result = Selector(loadWeight: 1.0).Select([
            Reachable("near-but-busy", 30, load: 90),
            Reachable("further-but-idle", 40),
        ]);

        result.Best!.Id.Should().Be("further-but-idle");
    }

    [Fact]
    public void A_load_weight_of_zero_ranks_purely_on_latency()
    {
        ServerSelectionResult result = Selector(loadWeight: 0).Select([
            Reachable("near-but-busy", 30, load: 90),
            Reachable("further-but-idle", 40),
        ]);

        result.Best!.Id.Should().Be("near-but-busy");
    }

    [Fact]
    public void A_tie_on_score_is_broken_by_load_then_by_identifier()
    {
        // Same latency, different load: the quieter gateway is preferred.
        Selector().Select([Reachable("b", 50, load: 40), Reachable("a", 50, load: 10)])
            .Best!.Id.Should().Be("a");

        // Identical in every respect the policy cares about, so the identifier decides. Not a
        // real preference, just a guarantee that the same input gives the same output.
        Selector().Select([Reachable("zeta", 50), Reachable("alpha", 50)])
            .Best!.Id.Should().Be("alpha");
    }

    [Fact]
    public void An_unreachable_gateway_is_excluded_with_its_probe_error()
    {
        VpnServer unreachable = TestServers.Create("down");

        ServerSelectionResult result = Selector().Select([
            LatencyMeasurement.Failure(unreachable, "ConnectionRefused"),
            Reachable("up", 80),
        ]);

        result.Best!.Id.Should().Be("up");
        result.Excluded.Should().ContainSingle()
            .Which.Should().Match<ExcludedServer>(e => e.Server.Id == "down" && e.Reason == "ConnectionRefused");
    }

    [Fact]
    public void A_gateway_disabled_by_the_operator_is_excluded_even_when_it_answers()
    {
        ServerSelectionResult result = Selector().Select([
            Reachable("disabled", 5, enabled: false),
            Reachable("enabled", 90),
        ]);

        result.Best!.Id.Should().Be("enabled");
        result.Excluded.Should().ContainSingle().Which.Reason.Should().Be("Disabled by the operator.");
    }

    [Fact]
    public void A_gateway_above_the_load_limit_is_excluded_rather_than_merely_penalised()
    {
        ServerSelectionResult result = Selector(maxLoad: 80).Select([Reachable("saturated", 5, load: 99)]);

        result.HasCandidate.Should().BeFalse();
        result.Excluded.Should().ContainSingle().Which.Reason.Should().Contain("99%").And.Contain("80%");
    }

    [Fact]
    public void A_gateway_above_the_latency_limit_is_excluded()
    {
        ServerSelectionResult result = Selector(maxLatency: TimeSpan.FromMilliseconds(100))
            .Select([Reachable("distant", 350)]);

        result.HasCandidate.Should().BeFalse();
        result.Excluded.Should().ContainSingle().Which.Reason.Should().Contain("350 ms");
    }

    [Fact]
    public void An_empty_input_produces_no_candidate_and_no_exclusions()
    {
        ServerSelectionResult result = Selector().Select([]);

        result.HasCandidate.Should().BeFalse();
        result.Best.Should().BeNull();
        result.Excluded.Should().BeEmpty();
    }

    [Fact]
    public void Every_gateway_being_unreachable_yields_a_reason_for_each()
    {
        ServerSelectionResult result = Selector().Select([
            LatencyMeasurement.Failure(TestServers.Create("a"), "TimedOut"),
            LatencyMeasurement.Failure(TestServers.Create("b"), "HostNotFound"),
        ]);

        result.Best.Should().BeNull();
        result.Excluded.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(50, 150)]
    [InlineData(100, 200)]
    public void Score_scales_latency_by_the_load_penalty(int loadPercent, double expectedScore)
    {
        Selector(loadWeight: 1.0).Score(TimeSpan.FromMilliseconds(100), loadPercent)
            .Should().BeApproximately(expectedScore, 0.001);
    }

    [Fact]
    public void Score_clamps_a_load_reading_outside_the_expected_range()
    {
        // A gateway reporting 140 percent is a bug somewhere upstream. Clamping keeps the
        // ranking sane instead of letting one bad reading dominate it.
        ServerSelector selector = Selector();

        selector.Score(TimeSpan.FromMilliseconds(100), 140).Should().Be(selector.Score(TimeSpan.FromMilliseconds(100), 100));
        selector.Score(TimeSpan.FromMilliseconds(100), -20).Should().Be(selector.Score(TimeSpan.FromMilliseconds(100), 0));
    }

    [Fact]
    public void SelectBest_agrees_with_the_head_of_the_ranking()
    {
        LatencyMeasurement[] measurements = [Reachable("a", 90), Reachable("b", 10)];

        Selector().SelectBest(measurements).Should().Be(Selector().Select(measurements).Best);
    }
}
