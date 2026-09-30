using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>
/// Measures the ICMP echo round trip to the gateway host.
/// </summary>
/// <remarks>
/// This is the measurement VPN clients commonly show, and it touches nothing on the
/// gateway but the kernel's echo reply. The TCP probe was the first choice here, but a
/// gateway that exposes only SSH and WireGuard leaves SSH as the one port to time, and
/// SSH sits behind a per-source connection limit. A few refreshes in a row used up the
/// allowance, the probe's SYN was dropped, and the figure grew by TCP's one second
/// retransmission delay. Measuring the path with ICMP keeps the SSH limit strict.
/// <para>
/// On Linux, <see cref="Ping"/> opens a raw socket when the process may, and otherwise runs
/// the system's <c>ping</c> utility, which on current distributions sends through an
/// unprivileged ICMP socket. No elevated rights are needed, but the utility has to be
/// installed; where it is missing, the probe reports that as a failed measurement rather
/// than throwing, and the TCP probe is the alternative.
/// </para>
/// <para>
/// Like the TCP probe, it resolves the name first and keeps that out of the figure, and
/// it tries IPv4 before IPv6 so a client without IPv6 does not spend its budget on an
/// address it cannot reach.
/// </para>
/// </remarks>
public sealed class IcmpLatencyProbe : ILatencyProbe
{
    private readonly TimeSpan _timeout;

    /// <summary>Creates the probe.</summary>
    /// <param name="timeout">How long to wait for each echo reply.</param>
    public IcmpLatencyProbe(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    /// <inheritdoc />
    public string Name => "ICMP echo";

    /// <inheritdoc />
    public async Task<LatencyMeasurement> ProbeAsync(VpnServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        IPAddress[] addresses;
        try
        {
            using var resolveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resolveTimeout.CancelAfter(_timeout);
            addresses = await Dns.GetHostAddressesAsync(server.EndpointHost, resolveTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return LatencyMeasurement.Failure(server, "The host name did not resolve in time.");
        }
        catch (SocketException ex)
        {
            return LatencyMeasurement.Failure(server, ex.SocketErrorCode.ToString());
        }

        string lastError = "The host name has no addresses.";
        int timeoutMilliseconds = (int)_timeout.TotalMilliseconds;

        foreach (IPAddress address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var ping = new Ping();
            try
            {
                PingReply reply = await ping.SendPingAsync(address, timeoutMilliseconds).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    // RoundtripTime is whole milliseconds, which is finer than the
                    // variation between two probes of the same gateway.
                    return LatencyMeasurement.Success(server, TimeSpan.FromMilliseconds(reply.RoundtripTime));
                }

                lastError = reply.Status.ToString();
            }
            catch (PingException ex)
            {
                lastError = ex.InnerException?.Message ?? ex.Message;
            }
            catch (PlatformNotSupportedException ex)
            {
                // No raw socket and no ping utility. Every address would fail the same way.
                return LatencyMeasurement.Failure(server, ex.Message);
            }
        }

        return LatencyMeasurement.Failure(server, lastError);
    }
}
