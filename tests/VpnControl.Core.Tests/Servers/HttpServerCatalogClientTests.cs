using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VpnControl.Core.Servers;
using VpnControl.Core.Tests.Fakes;
using Xunit;

namespace VpnControl.Core.Tests.Servers;

public sealed class HttpServerCatalogClientTests
{
    private static readonly string ServerJson = $$"""
        [
          {
            "id": "lt-vln-01",
            "name": "Vilnius 1",
            "city": "Vilnius",
            "country": "LT",
            "endpointHost": "lab-vilnius-1.invalid",
            "endpointPort": 51820,
            "publicKey": "{{TestServers.SampleKey}}",
            "loadPercent": 18,
            "isEnabled": true
          }
        ]
        """;

    private static HttpServerCatalogClient CreateClient(
        StubHttpMessageHandler handler,
        int maxAttempts = 3,
        string? deviceToken = null) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/") },
            Options.Create(new ServerCatalogOptions
            {
                BaseAddress = "http://control-plane.invalid/",
                MaxAttempts = maxAttempts,
                RetryBaseDelay = TimeSpan.Zero,
                DeviceToken = deviceToken,
            }),
            NullLogger<HttpServerCatalogClient>.Instance);

    [Fact]
    public async Task A_catalog_response_is_deserialised_into_the_snapshot()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, ServerJson);

        ServerCatalog catalog = await CreateClient(handler).GetServersAsync();

        catalog.Count.Should().Be(1);
        VpnServer server = catalog.Servers[0];
        server.Id.Should().Be("lt-vln-01");
        server.Endpoint.Should().Be("lab-vilnius-1.invalid:51820");
        server.LoadPercent.Should().Be(18);
    }

    [Fact]
    public async Task Country_and_city_filters_are_sent_as_query_parameters()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "[]");

        await CreateClient(handler).GetServersAsync("LT", "Kaunas");

        handler.Requests.Single().RequestUri!.Query.Should().Be("?country=LT&city=Kaunas");
    }

    [Fact]
    public async Task A_missing_gateway_comes_back_as_null_rather_than_an_exception()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.NotFound);

        VpnServer? server = await CreateClient(handler).GetServerAsync("nope");

        server.Should().BeNull();
        handler.Requests.Should().ContainSingle("a 404 is an answer, so it must not be retried");
    }

    [Fact]
    public async Task A_server_error_is_retried_and_the_later_success_is_returned()
    {
        var handler = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, """{"title":"down"}""")
            .Respond(HttpStatusCode.OK, ServerJson);

        ServerCatalog catalog = await CreateClient(handler).GetServersAsync();

        catalog.Count.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_transport_failure_is_retried()
    {
        var handler = new StubHttpMessageHandler()
            .Throw(new HttpRequestException("connection refused"))
            .Respond(HttpStatusCode.OK, ServerJson);

        ServerCatalog catalog = await CreateClient(handler).GetServersAsync();

        catalog.Count.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_client_error_is_not_retried_and_surfaces_with_its_status_code()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.BadRequest, """{"title":"bad country"}""");

        Func<Task> act = () => CreateClient(handler).GetServersAsync();

        (await act.Should().ThrowAsync<ServerCatalogException>())
            .Which.StatusCode.Should().Be(400);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Retries_stop_once_the_attempt_budget_is_spent()
    {
        var handler = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.BadGateway)
            .Respond(HttpStatusCode.BadGateway);

        Func<Task> act = () => CreateClient(handler, maxAttempts: 2).GetServersAsync();

        await act.Should().ThrowAsync<ServerCatalogException>();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_empty_body_on_a_success_response_is_reported_rather_than_read_as_an_empty_catalog()
    {
        // The distinction matters: an empty list means the operator has no gateways, and an
        // empty body means something is wrong with the service.
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "null");

        Func<Task> act = () => CreateClient(handler).GetServersAsync();

        await act.Should().ThrowAsync<ServerCatalogException>().WithMessage("*empty body*");
    }

    [Fact]
    public async Task Unparseable_json_becomes_a_catalog_exception_rather_than_a_json_exception()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{not json");

        Func<Task> act = () => CreateClient(handler).GetServersAsync();

        await act.Should().ThrowAsync<ServerCatalogException>();
    }

    [Fact]
    public async Task Cancellation_is_propagated_and_nothing_is_retried()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, ServerJson);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => CreateClient(handler).GetServersAsync(cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_device_token_is_sent_as_a_bearer_credential_on_every_request()
    {
        var handler = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "[]")
            .Respond(HttpStatusCode.Created, $$"""
                {
                  "peerId": "p1",
                  "serverId": "lt-vln-01",
                  "assignedAddress": "10.99.0.2/32",
                  "serverPublicKey": "{{TestServers.SampleKey}}",
                  "endpoint": "lab-vilnius-1.invalid:51820",
                  "allowedIps": ["0.0.0.0/0"]
                }
                """);

        HttpServerCatalogClient client = CreateClient(handler, deviceToken: "vpd_test-token");
        await client.GetServersAsync();
        PeerConfiguration peer = await client.RegisterPeerAsync(new PeerRegistrationRequest("lt-vln-01", TestServers.SampleKey));

        peer.PeerId.Should().Be("p1");
        handler.Requests.Should().OnlyContain(r =>
            r.Headers.Authorization != null &&
            r.Headers.Authorization.Scheme == "Bearer" &&
            r.Headers.Authorization.Parameter == "vpd_test-token");
    }

    [Fact]
    public async Task Releasing_a_registration_reports_whether_there_was_one_to_release()
    {
        var handler = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.NoContent)
            .Respond(HttpStatusCode.NotFound);

        HttpServerCatalogClient client = CreateClient(handler);

        (await client.UnregisterPeerAsync("p1")).Should().BeTrue();
        (await client.UnregisterPeerAsync("p1")).Should().BeFalse();
    }

    [Fact]
    public async Task A_peer_identifier_is_escaped_into_the_path()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.NoContent);

        await CreateClient(handler).UnregisterPeerAsync("a b/c");

        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/peers/a%20b%2Fc");
    }
}
