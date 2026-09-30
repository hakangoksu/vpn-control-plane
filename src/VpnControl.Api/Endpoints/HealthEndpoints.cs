using VpnControl.Api.Data;

namespace VpnControl.Api.Endpoints;

/// <summary>A liveness endpoint that actually checks something.</summary>
public static class HealthEndpoints
{
    /// <summary>Maps <c>GET /health</c>.</summary>
    /// <param name="routes">Route builder to add to.</param>
    /// <returns>The route builder, so calls can be chained.</returns>
    public static IEndpointRouteBuilder MapHealthEndpoint(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/health", async (ControlPlaneDbContext dbContext, CancellationToken cancellationToken) =>
            {
                // A health endpoint that returns 200 unconditionally reports that the process
                // is running, which the load balancer could already see. Touching the
                // database makes the answer mean something: this instance can serve a request.
                bool databaseReachable = await dbContext.Database
                    .CanConnectAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Status only. This endpoint is anonymous, so it says nothing about how many
                // gateways or peers exist.
                return databaseReachable
                    ? Results.Ok(new { status = "healthy" })
                    : Results.Problem(
                        title: "Database is not reachable.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("Health")
            .WithTags("Health")
            .WithSummary("Reports whether this instance can serve requests.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }
}
