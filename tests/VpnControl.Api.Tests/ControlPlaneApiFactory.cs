using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Api.Cli;
using VpnControl.Api.Data;
using VpnControl.Api.Security;
using VpnControl.Core.Crypto;

namespace VpnControl.Api.Tests;

/// <summary>
/// Starts the real API in process, against a database file of its own.
/// </summary>
/// <remarks>
/// <c>WebApplicationFactory</c> runs the application's actual startup path, so these tests
/// cover the endpoint routing, model binding, the options binding, the endpoint filter and
/// the EF Core queries together. Testing the handler methods directly would skip every one of
/// those, which is where the mistakes in a minimal API tend to be.
/// <para>
/// Each instance gets its own SQLite file in the temporary directory, so tests cannot see
/// each other's rows and can run in parallel. The file is deleted on disposal. An in-memory
/// provider would have been simpler and would have stopped exercising the real SQL.
/// </para>
/// </remarks>
public sealed class ControlPlaneApiFactory : WebApplicationFactory<Program>
{
    /// <summary>IPv6 /64 the factory configures, so the tests can check the addresses handed out.</summary>
    public const string TestIpv6Prefix = "fd4c:7a2e:91b3:0";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"vpn-control-plane-tests-{Guid.NewGuid():n}.db");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Configuration is overridden rather than services replaced, because the connection
        // string and the API key are already settings. Reaching for service replacement when
        // configuration will do produces tests that pass against a wiring the application
        // never actually uses.
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ControlPlane"] = $"Data Source={_databasePath}",
                ["ControlPlane:SeedDemoCatalog"] = "true",
                ["ControlPlane:Ipv6Prefix"] = TestIpv6Prefix,
            }));
    }

    /// <summary>Enrolls a new device through the admin commands.</summary>
    /// <param name="name">Device label.</param>
    /// <returns>The device identifier and its token.</returns>
    /// <remarks>
    /// Goes through <see cref="AdminCommands"/>, the same code path the operator uses, rather
    /// than inserting a row, so the tests also cover how tokens are generated and hashed.
    /// </remarks>
    public async Task<(string DeviceId, string Token)> EnrollDeviceAsync(string name = "test-device")
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        AdminCommands commands = scope.ServiceProvider.GetRequiredService<AdminCommands>();
        (DeviceRecord device, string token) = await commands.AddDeviceAsync(name, CancellationToken.None);
        return (device.Id, token);
    }

    /// <summary>Enrolls a new device and returns a client that presents its token.</summary>
    /// <param name="name">Device label.</param>
    /// <returns>An HTTP client for the device endpoints.</returns>
    public async Task<HttpClient> CreateDeviceClientAsync(string name = "test-device")
    {
        (_, string token) = await EnrollDeviceAsync(name);
        return CreateBearerClient(token);
    }

    /// <summary>Creates a client that presents the given bearer token.</summary>
    /// <param name="token">Token to send.</param>
    /// <returns>An HTTP client.</returns>
    public HttpClient CreateBearerClient(string token)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Registers a gateway with an agent token and returns that token.</summary>
    /// <param name="gatewayId">Identifier for the new gateway.</param>
    /// <returns>The agent token.</returns>
    public async Task<string> AddGatewayAsync(string gatewayId)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        AdminCommands commands = scope.ServiceProvider.GetRequiredService<AdminCommands>();

        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        string token = AccessTokens.Create(AccessTokens.GatewayPrefix);

        await commands.UpsertGatewayAsync(
            new ServerRecord
            {
                Id = gatewayId,
                Name = gatewayId,
                City = "Testville",
                Country = "LT",
                EndpointHost = $"{gatewayId}.invalid",
                EndpointPort = 51820,
                PublicKey = keys.PublicKeyBase64,
                Ipv6Egress = true,
            },
            token,
            CancellationToken.None);

        return token;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (string path in new[] { _databasePath, $"{_databasePath}-shm", $"{_databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
