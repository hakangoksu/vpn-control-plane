using FluentAssertions;
using VpnControl.Core.Connection;
using VpnControl.Core.Tunneling;
using VpnControl.Desktop.Tests.Fakes;

namespace VpnControl.Desktop.Tests;

/// <summary>Covers what the session panel shows as a session starts, runs and ends.</summary>
public sealed class ConnectionViewModelTests
{
    [Fact]
    public void Before_anything_happens_the_panel_reads_as_idle()
    {
        using var harness = new SyncHarness();

        harness.Connection.State.Should().Be(ConnectionState.Disconnected);
        harness.Connection.IsConnected.Should().BeFalse();
        harness.Connection.IsBusy.Should().BeFalse();
        harness.Connection.StateText.Should().Be("Disconnected");
        harness.Connection.Headline.Should().Be("Not protected");
        harness.Connection.HasSession.Should().BeFalse();
        harness.Connection.ElapsedText.Should().Be("-");
        harness.Connection.HandshakeText.Should().Be("-");
    }

    [Fact]
    public async Task Connecting_fills_in_the_gateway_and_the_endpoint()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();

        await harness.Manager.ConnectAsync(TestHarness.Servers[0]);

        harness.Connection.State.Should().Be(ConnectionState.Connected);
        harness.Connection.IsConnected.Should().BeTrue();
        harness.Connection.StateText.Should().Be("Connected");
        harness.Connection.HasSession.Should().BeTrue();
        harness.Connection.Subtitle.Should().StartWith("Traffic leaves from ");
        harness.Connection.ServerName.Should().Be(TestHarness.Servers[0].Name);
        harness.Connection.Endpoint.Should().Be(TestHarness.Servers[0].Endpoint);
    }

    [Fact]
    public async Task Every_transition_is_visible_in_the_order_it_happened()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();

        var observed = new List<ConnectionState>();
        harness.Connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(harness.Connection.State))
            {
                observed.Add(harness.Connection.State);
            }
        };

        await harness.Manager.ConnectAsync(TestHarness.Servers[0]);
        await harness.Manager.DisconnectAsync();

        // The in-between states matter: a panel that jumped straight to connected would
        // tell the user their traffic was protected while the handshake was in flight.
        observed.Should().Equal(
            ConnectionState.Connecting,
            ConnectionState.Connected,
            ConnectionState.Disconnecting,
            ConnectionState.Disconnected);
    }

    [Fact]
    public async Task A_failed_connection_leaves_the_panel_faulted_with_the_reason()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Tunnel.FailOnUp = new TunnelException("The interface could not be created.");

        Func<Task> act = () => harness.Manager.ConnectAsync(TestHarness.Servers[0]);

        await act.Should().ThrowAsync<TunnelException>();
        harness.Connection.State.Should().Be(ConnectionState.Faulted);
        harness.Connection.IsFaulted.Should().BeTrue();
        harness.Connection.Detail.Should().Be("The interface could not be created.");
    }

    [Fact]
    public async Task Disconnecting_clears_the_counters_so_nothing_reads_as_still_flowing()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        await harness.Manager.ConnectAsync(TestHarness.Servers[0]);
        harness.Connection.ApplyStatistics(new TunnelStatistics(8192, 2048, DateTimeOffset.UtcNow, "x:51820"));
        harness.Connection.BytesReceived.Should().Be(8192);

        await harness.Manager.DisconnectAsync();

        harness.Connection.BytesReceived.Should().Be(0);
        harness.Connection.BytesSent.Should().Be(0);
        harness.Connection.Elapsed.Should().Be(TimeSpan.Zero);
        harness.Connection.LastHandshake.Should().BeNull();
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(5 * 1024 * 1024, "5.0 MiB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GiB")]
    public void Counters_are_formatted_in_binary_units(long bytes, string expected)
    {
        using var harness = new SyncHarness();

        harness.Connection.ApplyStatistics(new TunnelStatistics(bytes, bytes, null, null));

        harness.Connection.BytesReceivedText.Should().Be(expected);
        harness.Connection.BytesSentText.Should().Be(expected);
    }

    [Fact]
    public async Task The_elapsed_time_counts_from_when_the_tunnel_came_up()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        await harness.Manager.ConnectAsync(TestHarness.Servers[0]);

        harness.Connection.ApplyStatistics(TunnelStatistics.Empty);

        // The real clock is used here, so the assertion is on the shape rather than on a
        // figure: the session started moments ago and has not been running for an hour.
        harness.Connection.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        harness.Connection.ElapsedText.Should().MatchRegex(@"^00:00:\d\d$");
    }

    [Fact]
    public async Task The_backend_is_described_as_simulated_when_it_is()
    {
        await using TestHarness harness = TestHarness.Create();

        harness.Connection.IsSimulated.Should().BeTrue();
        harness.Connection.TunnelDescription.Should().Contain("no packets are being moved");
    }

    [Fact]
    public async Task Disposing_the_panel_stops_it_reacting_to_the_session()
    {
        TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();

        await harness.Main.DisposeAsync();
        await harness.Manager.ConnectAsync(TestHarness.Servers[0]);

        // Unsubscribed, so the panel no longer follows the session it is detached from.
        harness.Connection.State.Should().Be(ConnectionState.Disconnected);

        await harness.Manager.DisposeAsync();
    }

    /// <summary>A harness for the cases that need no session, only the panel itself.</summary>
    private sealed class SyncHarness : IDisposable
    {
        private readonly TestHarness _harness = TestHarness.Create();

        public VpnControl.Desktop.ViewModels.ConnectionViewModel Connection => _harness.Connection;

        public void Dispose() => _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
