using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;
using Xunit;

namespace VpnControl.Api.Tests;

public sealed class ServerEndpointTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>, IAsyncLifetime
{
    private readonly ControlPlaneApiFactory _factory = factory;
    private HttpClient _client = null!;

    /// <inheritdoc />
    public async Task InitializeAsync() => _client = await _factory.CreateDeviceClientAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_catalog_requires_a_device_token()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(new Uri("/api/servers", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_catalog_never_exposes_a_gateways_agent_token_hash()
    {
        await _factory.AddGatewayAsync("catalog-leak-check");

        string body = await _client.GetStringAsync(new Uri("/api/servers", UriKind.Relative));

        body.Should().NotContainAny("agentToken", "AgentTokenHash", "tokenHash");
    }

    [Fact]
    public async Task The_catalog_returns_the_seeded_gateways()
    {
        List<VpnServer>? servers = await _client.GetFromJsonAsync<List<VpnServer>>("/api/servers");

        servers.Should().NotBeNull();
        servers!.Should().NotBeEmpty();
        servers!.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        servers!.Should().OnlyContain(s => WireGuardKeyPair.IsValidKey(s.PublicKey));
    }

    [Fact]
    public async Task The_catalog_is_ordered_by_country_then_city()
    {
        List<VpnServer> servers = (await _client.GetFromJsonAsync<List<VpnServer>>("/api/servers"))!;

        servers.Should().BeInAscendingOrder(s => s.Country);
    }

    [Fact]
    public async Task A_country_filter_narrows_the_result()
    {
        List<VpnServer> servers = (await _client.GetFromJsonAsync<List<VpnServer>>("/api/servers?country=LT"))!;

        servers.Should().NotBeEmpty();
        servers.Should().OnlyContain(s => s.Country == "LT");
    }

    [Fact]
    public async Task A_city_filter_narrows_the_result()
    {
        List<VpnServer> servers = (await _client.GetFromJsonAsync<List<VpnServer>>("/api/servers?city=Kaunas"))!;

        servers.Should().NotBeEmpty();
        servers.Should().OnlyContain(s => s.City == "Kaunas");
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_returns_an_empty_list_rather_than_a_404()
    {
        // An empty result is a valid answer to "which gateways are in Antarctica".
        HttpResponseMessage response = await _client.GetAsync(new Uri("/api/servers?country=AQ", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<VpnServer>>())!.Should().BeEmpty();
    }

    [Fact]
    public async Task A_country_filter_of_the_wrong_length_is_rejected_as_a_validation_problem()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/api/servers?country=LTU", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("country");
    }

    [Fact]
    public async Task One_gateway_can_be_fetched_by_identifier()
    {
        VpnServer? server = await _client.GetFromJsonAsync<VpnServer>("/api/servers/lt-vln-01");

        server.Should().NotBeNull();
        server!.Id.Should().Be("lt-vln-01");
        server.City.Should().Be("Vilnius");
        server.EndpointHost.Should().EndWith(".invalid");
    }

    [Fact]
    public async Task An_unknown_gateway_gives_a_problem_document()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/api/servers/does-not-exist", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task The_health_endpoint_answers_anonymously_and_reveals_no_counts()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"healthy\"}");
    }
}
