using System.Diagnostics;
using System.Net.Sockets;
using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>
/// Measures the time to complete a TCP handshake with the gateway host.
/// </summary>
/// <remarks>
/// This measures the network path to the host, not the WireGuard service. WireGuard
/// deliberately never answers a packet that is not authenticated, so a UDP probe of
/// its port cannot tell "unreachable" from "working correctly and staying silent".
/// A TCP connect to a port the operator does answer, typically 443, gives a round trip
/// that tracks the path well enough to rank gateways, and it needs no privileges.
/// <para>
/// The figure it produces is the TCP handshake time to a different port, which is why
/// nothing in this project presents it as the tunnel's latency.
/// </para>
/// </remarks>
public sealed class TcpConnectLatencyProbe : ILatencyProbe
{
    private readonly int _probePort;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the probe.</summary>
    /// <param name="probePort">
    /// TCP port to connect to. Defaults to 443 because a gateway that terminates
    /// anything web facing will have it open, unlike the WireGuard UDP port.
    /// </param>
    /// <param name="timeout">
    /// How long to wait before giving up on a host. A gateway slower than this is not
    /// one worth connecting through anyway.
    /// </param>
    public TcpConnectLatencyProbe(int probePort = 443, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(probePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(probePort, 65535);

        _probePort = probePort;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    /// <inheritdoc />
    public string Name => $"TCP connect to port {_probePort}";

    /// <inheritdoc />
    public async Task<LatencyMeasurement> ProbeAsync(VpnServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        // A stopwatch is started rather than reading the wall clock twice, because a
        // clock adjustment between two reads can produce a negative interval.
        long start = Stopwatch.GetTimestamp();

        try
        {
            await socket.ConnectAsync(server.EndpointHost, _probePort, timeoutSource.Token).ConfigureAwait(false);
            return LatencyMeasurement.Success(server, Stopwatch.GetElapsedTime(start));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled the whole run, which is not a statement about this
            // gateway, so it propagates instead of being recorded as a failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            return LatencyMeasurement.Failure(server, $"No answer within {_timeout.TotalMilliseconds:F0} ms.");
        }
        catch (SocketException ex)
        {
            return LatencyMeasurement.Failure(server, ex.SocketErrorCode.ToString());
        }
    }
}
