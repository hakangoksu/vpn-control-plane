using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnControl.Core.Http;

namespace VpnControl.Core.Servers;

/// <summary>
/// Talks to the control plane API over HTTP.
/// </summary>
/// <remarks>
/// Registered as a typed client, so the host owns the <see cref="HttpClient"/> and
/// its handler lifetime. Creating an <c>HttpClient</c> per call is the classic way
/// to exhaust sockets, and holding one static instance forever means stale DNS;
/// <c>IHttpClientFactory</c> avoids both, and a typed client is how a class opts in.
/// <para>
/// Retries live here rather than in a delegating handler so that the decision of
/// what counts as transient can mention the catalog's own semantics: a 404 for a
/// gateway is an answer, not a failure, and must not be retried.
/// </para>
/// </remarks>
public sealed class HttpServerCatalogClient : IServerCatalogClient
{
    private const string ApiKeyHeader = "X-Api-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ServerCatalogOptions _options;
    private readonly ILogger<HttpServerCatalogClient> _logger;
    private readonly RetryPolicy _retryPolicy;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the client.</summary>
    /// <param name="httpClient">Typed client supplied by <c>IHttpClientFactory</c>.</param>
    /// <param name="options">Base address, credential and retry settings.</param>
    /// <param name="logger">Destination for request and retry diagnostics.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp the catalog snapshot. Injected so a test can assert on the
    /// timestamp instead of having to tolerate whatever <c>DateTimeOffset.UtcNow</c> said.
    /// </param>
    public HttpServerCatalogClient(
        HttpClient httpClient,
        IOptions<ServerCatalogOptions> options,
        ILogger<HttpServerCatalogClient> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryPolicy = new RetryPolicy(_options.MaxAttempts, _options.RetryBaseDelay);

        if (_httpClient.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseAddress))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseAddress, UriKind.Absolute);
        }
    }

    /// <inheritdoc />
    public async Task<ServerCatalog> GetServersAsync(
        string? country = null,
        string? city = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildServerListPath(country, city);

        List<VpnServer>? servers = await SendAsync(
            path,
            () => new HttpRequestMessage(HttpMethod.Get, path),
            static (response, token) => response.Content.ReadFromJsonAsync<List<VpnServer>>(JsonOptions, token),
            cancellationToken).ConfigureAwait(false);

        if (servers is null)
        {
            throw new ServerCatalogException("The catalog endpoint returned an empty body.");
        }

        return new ServerCatalog(servers, _timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public async Task<VpnServer?> GetServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        string path = $"api/servers/{Uri.EscapeDataString(serverId)}";

        // A missing gateway is an ordinary outcome for a caller resolving an id it
        // remembered from an earlier session, so 404 is mapped to null rather than
        // being thrown. Every other non-success status is still a failure.
        return await SendAsync(
            path,
            () => new HttpRequestMessage(HttpMethod.Get, path),
            static (response, token) => response.Content.ReadFromJsonAsync<VpnServer>(JsonOptions, token),
            cancellationToken,
            treatNotFoundAsNull: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PeerConfiguration> RegisterPeerAsync(
        PeerRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        const string Path = "api/peers";

        PeerConfiguration? peer = await SendAsync(
            Path,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, Path)
                {
                    Content = JsonContent.Create(request, options: JsonOptions),
                };
                AddApiKey(message);
                return message;
            },
            static (response, token) => response.Content.ReadFromJsonAsync<PeerConfiguration>(JsonOptions, token),
            cancellationToken).ConfigureAwait(false);

        return peer ?? throw new ServerCatalogException("The peer endpoint returned an empty body.");
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterPeerAsync(string peerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);

        string path = $"api/peers/{Uri.EscapeDataString(peerId)}";

        Ack? ack = await SendAsync(
            path,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Delete, path);
                AddApiKey(message);
                return message;
            },
            static (_, _) => Task.FromResult<Ack?>(Ack.Instance),
            cancellationToken,
            treatNotFoundAsNull: true).ConfigureAwait(false);

        // Null here means the server answered 404, so there was nothing to remove.
        return ack is not null;
    }

    private static string BuildServerListPath(string? country, string? city)
    {
        var query = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(country))
        {
            query.Add($"country={Uri.EscapeDataString(country)}");
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            query.Add($"city={Uri.EscapeDataString(city)}");
        }

        return query.Count == 0 ? "api/servers" : $"api/servers?{string.Join('&', query)}";
    }

    /// <summary>
    /// Decides whether a failed attempt is worth repeating.
    /// </summary>
    /// <remarks>
    /// Connection failures and timeouts are worth another try because the next
    /// attempt may reach a healthy instance. A 400 or a 401 will fail identically
    /// every time, so retrying only delays the error the user needs to see.
    /// </remarks>
    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException => true,

        // HttpClient reports its own timeout as TaskCanceledException. The caller's
        // cancellation is filtered out before this runs, so anything reaching here
        // is the timeout.
        TaskCanceledException => true,
        ServerCatalogException { StatusCode: >= 500 } => true,
        ServerCatalogException { StatusCode: (int)HttpStatusCode.RequestTimeout } => true,
        ServerCatalogException { StatusCode: (int)HttpStatusCode.TooManyRequests } => true,
        _ => false,
    };

    private void AddApiKey(HttpRequestMessage message)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.Add(ApiKeyHeader, _options.ApiKey);
        }
    }

    /// <summary>
    /// Sends a request under the retry policy and turns any failure into a
    /// <see cref="ServerCatalogException"/>.
    /// </summary>
    /// <remarks>
    /// The request is built by a factory rather than passed in, because an
    /// <see cref="HttpRequestMessage"/> cannot be sent twice and a retry needs a
    /// fresh one.
    /// </remarks>
    private async Task<T?> SendAsync<T>(
        string path,
        Func<HttpRequestMessage> requestFactory,
        Func<HttpResponseMessage, CancellationToken, Task<T?>> readAsync,
        CancellationToken cancellationToken,
        bool treatNotFoundAsNull = false)
        where T : class
    {
        try
        {
            return await _retryPolicy.ExecuteAsync(
                async (attempt, token) =>
                {
                    if (attempt > 1)
                    {
                        _logger.LogDebug("Retrying {Path}, attempt {Attempt} of {MaxAttempts}.", path, attempt, _options.MaxAttempts);
                    }

                    using HttpRequestMessage request = requestFactory();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(_options.RequestTimeout);

                    using HttpResponseMessage response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false);

                    if (treatNotFoundAsNull && response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        throw new ServerCatalogException(
                            $"{request.Method} {path} failed with {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}")
                        {
                            StatusCode = (int)response.StatusCode,
                        };
                    }

                    return await readAsync(response, timeout.Token).ConfigureAwait(false);
                },
                IsTransient,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop. Propagating the cancellation unchanged lets
            // them distinguish "I cancelled this" from "the control plane is down".
            throw;
        }
        catch (ServerCatalogException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ServerCatalogException($"Request to {path} failed: {ex.Message}", ex);
        }
    }

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : string.Concat(body.AsSpan(0, 300), "...");

    /// <summary>
    /// Stands in for "the call succeeded and there is no body to read".
    /// </summary>
    /// <remarks>
    /// The shared send helper is generic over a reference type so that null can mean
    /// "the server said 404". A DELETE has nothing to deserialise, so it needs some
    /// non-null value to return instead, and this is it.
    /// </remarks>
    private sealed class Ack
    {
        public static Ack Instance { get; } = new();
    }
}
