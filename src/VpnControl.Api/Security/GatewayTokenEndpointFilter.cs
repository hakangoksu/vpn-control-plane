using Microsoft.EntityFrameworkCore;
using VpnControl.Api.Data;

namespace VpnControl.Api.Security;

/// <summary>
/// Admits a request only when it carries the agent token of a known gateway, and makes that
/// gateway available to the handler.
/// </summary>
/// <remarks>
/// The token decides which gateway is asking. Nothing in the URL or the query does, so an
/// agent token taken from a compromised gateway reads that gateway's peer list and nothing
/// else.
/// </remarks>
/// <param name="dbContext">Database session for this request.</param>
public sealed class GatewayTokenEndpointFilter(ControlPlaneDbContext dbContext) : IEndpointFilter
{
    private const string ItemKey = "vpn-control-plane.gateway";

    /// <summary>Returns the gateway the filter authenticated for this request.</summary>
    /// <param name="context">Current request.</param>
    /// <returns>The gateway.</returns>
    /// <exception cref="InvalidOperationException">The endpoint is not behind this filter.</exception>
    public static ServerRecord GetGateway(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items[ItemKey] as ServerRecord
            ?? throw new InvalidOperationException("The endpoint is not protected by the gateway token filter.");
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;
        string? token = BearerToken.Read(http.Request);

        if (!AccessTokens.HasShape(token, AccessTokens.GatewayPrefix))
        {
            return Unauthorized();
        }

        string hash = AccessTokens.Hash(token!);
        ServerRecord? gateway = await dbContext.Servers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.AgentTokenHash == hash, http.RequestAborted)
            .ConfigureAwait(false);

        if (gateway is null)
        {
            return Unauthorized();
        }

        http.Items[ItemKey] = gateway;
        return await next(context).ConfigureAwait(false);
    }

    private static IResult Unauthorized() => Results.Problem(
        title: "Missing or invalid gateway token.",
        detail: "Send the agent token issued for this gateway as a bearer credential.",
        statusCode: StatusCodes.Status401Unauthorized);
}
