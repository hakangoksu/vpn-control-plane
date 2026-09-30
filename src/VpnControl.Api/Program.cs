using Microsoft.EntityFrameworkCore;
using VpnControl.Api.Cli;
using VpnControl.Api.Configuration;
using VpnControl.Api.Data;
using VpnControl.Api.Endpoints;
using VpnControl.Api.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Real deployment values, the IPv6 prefix, allowed host names and the database path, live in
// a file that is listed in .gitignore. It is optional so the project still runs from a fresh
// clone, and it is added last so it overrides the committed defaults.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

if (AdminCli.IsAdminInvocation(args))
{
    // The admin commands print tokens on standard output for a script to capture. The console
    // logger writes there too, and a log line mixed into a captured token would corrupt it, so
    // logging is off in this mode. Failures still surface as exceptions and exit codes.
    builder.Logging.ClearProviders();
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // The largest legitimate body is a peer registration of a few hundred bytes. A small cap
    // turns an oversized upload into a 413 before any of it is buffered or parsed.
    kestrel.Limits.MaxRequestBodySize = 16 * 1024;

    // No reason to tell every caller which server software answered.
    kestrel.AddServerHeader = false;
});

// Options pattern with validation at startup. ValidateOnStart turns a bad configuration
// value into a failure to launch rather than into a confusing 500 on the first request
// that happens to touch it.
builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection(ControlPlaneOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The connection string comes from configuration so a test, a container and a developer
// machine can each point at their own file without a code change.
//
// It is read inside the registration callback, from the resolved IConfiguration, rather than
// from builder.Configuration here. That is not a style preference. Reading it at this point
// would capture whatever value is present while the builder is still being assembled, and a
// test host that adds its own configuration source afterwards, which is exactly what
// WebApplicationFactory does, would be ignored. Two test classes then quietly shared one
// database file. Resolving it at DI time sees the final configuration.
builder.Services.AddDbContext<ControlPlaneDbContext>((serviceProvider, options) =>
{
    IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();
    options.UseSqlite(configuration.GetConnectionString("ControlPlane") ?? "Data Source=controlplane.db");
});

// Registered so the filters' own dependencies are resolved by the container rather than
// constructed by hand at each endpoint.
builder.Services.AddScoped<DeviceTokenEndpointFilter>();
builder.Services.AddScoped<GatewayTokenEndpointFilter>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GatewaySyncCoordinator>();
builder.Services.AddScoped<AdminCommands>();

// Makes unhandled exceptions and bare status codes come back as RFC 9457 problem
// documents, so a client gets the same shape of error whatever went wrong.
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

WebApplication app = builder.Build();

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    ControlPlaneDbContext dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
    bool seedDemo = app.Configuration.GetValue<bool>($"{ControlPlaneOptions.SectionName}:{nameof(ControlPlaneOptions.SeedDemoCatalog)}");

    // The admin command line never seeds: it is how real gateways are entered, and demo rows
    // appearing next to them would be advertised to clients.
    bool isAdmin = AdminCli.IsAdminInvocation(args);
    int seeded = await DatabaseSeeder.MigrateAndSeedAsync(dbContext, seedDemo && !isAdmin, app.Lifetime.ApplicationStopping);

    if (seeded > 0)
    {
        app.Logger.LogInformation("Seeded {Count} fictional gateways.", seeded);
    }

    if (isAdmin)
    {
        AdminCommands commands = scope.ServiceProvider.GetRequiredService<AdminCommands>();
        return await AdminCli.RunAsync(args[1..], commands, Console.In, Console.Out, Console.Error, CancellationToken.None);
    }
}

if (app.Environment.IsDevelopment())
{
    // Swagger is development only. An unauthenticated schema of every endpoint is a
    // convenience while building and an invitation in production.
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStatusCodePages();

app.MapHealthEndpoint();
app.MapServerEndpoints();
app.MapPeerEndpoints();
app.MapGatewayEndpoints();

await app.RunAsync();
return 0;

/// <summary>
/// Entry point marker.
/// </summary>
/// <remarks>
/// Top-level statements compile into an internal <c>Program</c> class. Declaring it as a
/// public partial here gives <c>WebApplicationFactory&lt;Program&gt;</c> in the test project
/// something to name, which is how the API is tested in process without a running server.
/// </remarks>
public partial class Program
{
    /// <summary>Not intended to be constructed.</summary>
    protected Program()
    {
    }
}
