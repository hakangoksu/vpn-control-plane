using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
    /// <summary>API key the factory configures, for tests that need to send a valid one.</summary>
    public const string TestApiKey = "test-api-key";

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
                ["ControlPlane:ApiKey"] = TestApiKey,
            }));
    }

    /// <summary>Creates a client that already carries the API key header.</summary>
    /// <returns>An HTTP client for the peer endpoints.</returns>
    public HttpClient CreateAuthenticatedClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        return client;
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
