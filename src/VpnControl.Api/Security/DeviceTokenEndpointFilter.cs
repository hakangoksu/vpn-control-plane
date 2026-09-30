using Microsoft.EntityFrameworkCore;
using VpnControl.Api.Data;

namespace VpnControl.Api.Security;

/// <summary>
/// Admits a request only when it carries the token of an enrolled, unrevoked device, and
/// makes that device available to the handler.
/// </summary>
/// <remarks>
/// A filter on the route group rather than a check in each handler, so an endpoint added to
/// the group later cannot forget it. Handlers read the caller with <see cref="GetDevice"/>,
/// and every ownership decision is made against that value rather than against anything the
/// request body claims.
/// <para>
/// Every failure gets the same 401 with the same text. Answering "revoked" to one caller and
/// "unknown" to another would tell someone holding an old token that it was once valid.
/// </para>
/// </remarks>
/// <param name="dbContext">Database session for this request.</param>
public sealed class DeviceTokenEndpointFilter(ControlPlaneDbContext dbContext) : IEndpointFilter
{
    private const string ItemKey = "vpn-control-plane.device";

    /// <summary>Returns the device the filter authenticated for this request.</summary>
    /// <param name="context">Current request.</param>
    /// <returns>The device.</returns>
    /// <exception cref="InvalidOperationException">The endpoint is not behind this filter.</exception>
    public static DeviceRecord GetDevice(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items[ItemKey] as DeviceRecord
            ?? throw new InvalidOperationException("The endpoint is not protected by the device token filter.");
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;
        string? token = BearerToken.Read(http.Request);

        if (!AccessTokens.HasShape(token, AccessTokens.DevicePrefix))
        {
            return Unauthorized();
        }

        string hash = AccessTokens.Hash(token!);
        DeviceRecord? device = await dbContext.Devices
            .FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAt == null, http.RequestAborted)
            .ConfigureAwait(false);

        if (device is null)
        {
            return Unauthorized();
        }

        http.Items[ItemKey] = device;
        return await next(context).ConfigureAwait(false);
    }

    private static IResult Unauthorized() => Results.Problem(
        title: "Missing or invalid device token.",
        detail: "Send the token issued for this device as a bearer credential.",
        statusCode: StatusCodes.Status401Unauthorized);
}
