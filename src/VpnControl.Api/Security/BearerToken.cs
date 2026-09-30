namespace VpnControl.Api.Security;

/// <summary>Reads a bearer token from the <c>Authorization</c> header.</summary>
/// <remarks>
/// The standard header rather than a custom one, so reverse proxies and log scrubbers that
/// already know to redact <c>Authorization</c> treat these tokens as the secrets they are.
/// </remarks>
internal static class BearerToken
{
    private const string Scheme = "Bearer ";

    /// <summary>Returns the token, or <c>null</c> when the header is missing or not a bearer credential.</summary>
    /// <param name="request">Incoming request.</param>
    /// <returns>The token without the scheme.</returns>
    public static string? Read(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();

        return header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..].Trim()
            : null;
    }
}
