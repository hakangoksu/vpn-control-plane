using FluentAssertions;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Core.Tests.Fakes;
using Xunit;

namespace VpnControl.Core.Tests.Latency;

public sealed class LatencyProbeTests
{
    [Fact]
    public async Task The_deterministic_probe_returns_the_same_value_for_the_same_gateway()
    {
        var probe = new DeterministicLatencyProbe();
        VpnServer server = TestServers.Create("lt-vln-01");

        LatencyMeasurement first = await probe.ProbeAsync(server);
        LatencyMeasurement second = await probe.ProbeAsync(server);

        second.RoundTrip.Should().Be(first.RoundTrip!.Value);
        first.IsReachable.Should().BeTrue();
    }

    [Fact]
    public async Task Different_gateways_get_different_derived_latencies()
    {
        var probe = new DeterministicLatencyProbe();

        LatencyMeasurement a = await probe.ProbeAsync(TestServers.Create("lt-vln-01"));
        LatencyMeasurement b = await probe.ProbeAsync(TestServers.Create("de-fra-01"));

        b.RoundTrip.Should().NotBe(a.RoundTrip!.Value);
    }

    [Fact]
    public async Task A_derived_latency_stays_inside_the_configured_band()
    {
        var probe = new DeterministicLatencyProbe(
            minimum: TimeSpan.FromMilliseconds(10),
            spread: TimeSpan.FromMilliseconds(50));

        foreach (VpnServer server in DemoCatalog.CreateServers())
        {
            LatencyMeasurement measurement = await probe.ProbeAsync(server);

            measurement.RoundTrip!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(10));
            measurement.RoundTrip!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(60));
        }
    }

    [Fact]
    public async Task An_override_of_null_makes_a_gateway_unreachable()
    {
        var probe = new DeterministicLatencyProbe(new Dictionary<string, TimeSpan?>
        {
            ["down"] = null,
            ["up"] = TimeSpan.FromMilliseconds(25),
        });

        LatencyMeasurement down = await probe.ProbeAsync(TestServers.Create("down"));
        LatencyMeasurement up = await probe.ProbeAsync(TestServers.Create("up"));

        down.IsReachable.Should().BeFalse();
        down.Error.Should().NotBeNullOrWhiteSpace();
        up.RoundTrip.Should().Be(TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task ProbeAll_returns_the_reachable_gateways_first_and_fastest_first()
    {
        var probe = new DeterministicLatencyProbe(new Dictionary<string, TimeSpan?>
        {
            ["slow"] = TimeSpan.FromMilliseconds(200),
            ["fast"] = TimeSpan.FromMilliseconds(20),
            ["down"] = null,
            ["middling"] = TimeSpan.FromMilliseconds(90),
        });

        IReadOnlyList<LatencyMeasurement> results = await probe.ProbeAllAsync([
            TestServers.Create("slow"),
            TestServers.Create("down"),
            TestServers.Create("fast"),
            TestServers.Create("middling"),
        ]);

        results.Select(m => m.Server.Id).Should().Equal("fast", "middling", "slow", "down");
    }

    [Fact]
    public async Task ProbeAll_measures_every_gateway_exactly_once()
    {
        var probe = new CountingProbe();
        VpnServer[] servers = [.. DemoCatalog.CreateServers()];

        IReadOnlyList<LatencyMeasurement> results = await probe.ProbeAllAsync(servers, maxConcurrency: 4);

        results.Should().HaveCount(servers.Length);
        probe.Calls.Should().Be(servers.Length);
    }

    [Fact]
    public async Task ProbeAll_honours_cancellation()
    {
        var probe = new DeterministicLatencyProbe();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => probe.ProbeAllAsync(DemoCatalog.CreateServers(), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_tcp_probe_reports_an_unresolvable_host_as_unreachable_rather_than_throwing()
    {
        // The .invalid domain cannot resolve, by design, which makes this a reliable way to
        // exercise the failure path without depending on the network.
        var probe = new TcpConnectLatencyProbe(probePort: 443, timeout: TimeSpan.FromMilliseconds(500));

        LatencyMeasurement measurement = await probe.ProbeAsync(TestServers.Create("nothing-here"));

        measurement.IsReachable.Should().BeFalse();
        measurement.Error.Should().NotBeNullOrWhiteSpace();
        measurement.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task The_tcp_probe_times_a_completed_handshake()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new TcpConnectLatencyProbe(port, TimeSpan.FromSeconds(2));

        LatencyMeasurement measurement = await probe.ProbeAsync(TestServers.Create("loopback") with { EndpointHost = "127.0.0.1" });

        measurement.IsReachable.Should().BeTrue();
        measurement.RoundTrip.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task The_tcp_probe_counts_a_refused_connection_as_an_answer()
    {
        // A reset comes back after one round trip, like a SYN-ACK, so it measures the path.
        int port;
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        var probe = new TcpConnectLatencyProbe(port, TimeSpan.FromSeconds(2));

        LatencyMeasurement measurement = await probe.ProbeAsync(TestServers.Create("loopback") with { EndpointHost = "127.0.0.1" });

        measurement.IsReachable.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void The_tcp_probe_rejects_a_port_outside_the_valid_range(int port)
    {
        Action act = () => _ = new TcpConnectLatencyProbe(port);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_measurement_of_a_disabled_gateway_is_reachable_but_not_healthy()
    {
        LatencyMeasurement measurement = LatencyMeasurement.Success(
            TestServers.Create("withdrawn", isEnabled: false),
            TimeSpan.FromMilliseconds(5));

        measurement.IsReachable.Should().BeTrue();
        measurement.IsHealthy.Should().BeFalse();
    }

    private sealed class CountingProbe : ILatencyProbe
    {
        private int _calls;

        public int Calls => _calls;

        public string Name => "Counting";

        public Task<LatencyMeasurement> ProbeAsync(VpnServer server, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(LatencyMeasurement.Success(server, TimeSpan.FromMilliseconds(10)));
        }
    }
}
