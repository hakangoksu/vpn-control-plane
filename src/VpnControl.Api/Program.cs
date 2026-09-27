using Microsoft.EntityFrameworkCore;
using VpnControl.Api.Configuration;
using VpnControl.Api.Data;
using VpnControl.Api.Endpoints;
using VpnControl.Api.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Options pattern with validation at startup. ValidateOnStart turns a bad configuration
// value into a failure to launch rather than into a confusing 500 on the first request
// that happens to touch it.
builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection(ControlPlaneOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The connection string comes from configuration so a test, a container and a developer
// machine can each point at their own file without a code change.
string connectionString = builder.Configuration.GetConnectionString("ControlPlane")
    ?? "Data Source=controlplane.db";

builder.Services.AddDbContext<ControlPlaneDbContext>(options => options.UseSqlite(connectionString));

// Registered so the filter's own dependencies are resolved by the container rather than
// constructed by hand at each endpoint.
builder.Services.AddScoped<ApiKeyEndpointFilter>();

// Makes unhandled exceptions and bare status codes come back as RFC 9457 problem
// documents, so a client gets the same shape of error whatever went wrong.
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

WebApplication app = builder.Build();

// Creating the schema on startup suits a disposable lab database. A service with data
// worth keeping would run migrations from a deployment step instead, so that two
// instances starting at once cannot both try to build the schema.
await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    ControlPlaneDbContext dbContext = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
    int seeded = await DatabaseSeeder.EnsureSeededAsync(dbContext, app.Lifetime.ApplicationStopping);

    if (seeded > 0)
    {
        app.Logger.LogInformation("Seeded {Count} fictional gateways.", seeded);
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

await app.RunAsync();

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
