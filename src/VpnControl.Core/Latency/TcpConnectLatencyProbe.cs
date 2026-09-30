using System.Diagnostics;
using System.Net;
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
/// A refused connection counts as an answer. A reset comes back from the host's kernel after
/// exactly one round trip, the same as a SYN-ACK would, so a gateway that rejects the probe
/// port with a TCP reset can be measured without running any service on it. The gateways in
/// this project's deployment do exactly that, which keeps their open port count at SSH and
/// WireGuard.
/// </para>
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

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            // Name resolution is done first and kept out of the measurement. Timing
            // Socket.ConnectAsync(host, port) would include the DNS lookup and every failed
            // attempt on an address family the local network cannot reach, which made a
            // gateway with an AAAA record look a second slower than one without.
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(server.EndpointHost, timeoutSource.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return LatencyMeasurement.Failure(server, "The host name has no addresses.");
            }

            string lastError = "No address answered.";

            // IPv4 first. Every gateway has it, and a client on a network without IPv6 would
            // otherwise spend part of its budget on an attempt that cannot succeed. The round
            // trip over either family ranks gateways the same way.
            foreach (IPAddress address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                // A stopwatch is started rather than reading the wall clock twice, because a
                // clock adjustment between two reads can produce a negative interval.
                long start = Stopwatch.GetTimestamp();

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, _probePort), timeoutSource.Token).ConfigureAwait(false);
                    return LatencyMeasurement.Success(server, Stopwatch.GetElapsedTime(start));
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    return LatencyMeasurement.Success(server, Stopwatch.GetElapsedTime(start));
                }
                catch (SocketException ex)
                {
                    lastError = ex.SocketErrorCode.ToString();
                }
            }

            return LatencyMeasurement.Failure(server, lastError);
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
