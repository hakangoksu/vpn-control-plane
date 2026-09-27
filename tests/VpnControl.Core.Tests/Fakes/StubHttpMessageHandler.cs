using System.Net;

namespace VpnControl.Core.Tests.Fakes;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a queue of prepared responses.
/// </summary>
/// <remarks>
/// Substituting the handler rather than the whole client is what lets these tests cover the
/// parts worth covering: status handling, retry decisions and deserialisation. A mocked
/// <c>IServerCatalogClient</c> would test nothing but the mock.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public StubHttpMessageHandler Respond(HttpStatusCode status, string? json = null)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = json is null
                ? new StringContent(string.Empty)
                : new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        return this;
    }

    public StubHttpMessageHandler Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    public StubHttpMessageHandler RespondWith(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"No response was queued for {request.Method} {request.RequestUri}.");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}
