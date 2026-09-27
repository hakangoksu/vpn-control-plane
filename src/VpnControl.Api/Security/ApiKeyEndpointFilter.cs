using Microsoft.Extensions.Options;
using VpnControl.Api.Configuration;

namespace VpnControl.Api.Security;

/// <summary>
/// Rejects a request that does not carry the expected <c>X-Api-Key</c> header.
/// </summary>
/// <remarks>
/// An endpoint filter rather than an authentication handler, because it is deliberately not
/// authentication: it proves the caller knows one shared string, which identifies nobody.
/// It is applied to the peer endpoints only, so the catalog stays readable without a
/// credential, and it shows where a real token check would sit.
/// <para>
/// Illustrative, and marked as such wherever it appears. A static key cannot be revoked for
/// one device, ends up in configuration files, and gives the server no idea who is calling.
/// </para>
/// </remarks>
/// <param name="options">Holds the expected key.</param>
public sealed class ApiKeyEndpointFilter(IOptions<ControlPlaneOptions> options) : IEndpointFilter
{
    /// <summary>Header the key is read from.</summary>
    public const string HeaderName = "X-Api-Key";

    private readonly ControlPlaneOptions _options = options.Value;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided) ||
            !IsExpectedKey(provided.ToString()))
        {
            return Results.Problem(
                title: "Missing or invalid API key.",
                detail: $"Send the shared key in the {HeaderName} header.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares the supplied key with the expected one in a way that does not leak its
    /// length or content through timing.
    /// </summary>
    /// <remarks>
    /// The scheme is weak by design, but comparing a secret with <c>==</c> is a habit worth
    /// not forming: an ordinary string comparison returns as soon as two characters differ,
    /// which over many requests tells an attacker how much of a guess was right.
    /// </remarks>
    private bool IsExpectedKey(string provided) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(_options.ApiKey));
}
