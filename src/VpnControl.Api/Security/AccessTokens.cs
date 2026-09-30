using System.Security.Cryptography;
using System.Text;

namespace VpnControl.Api.Security;

/// <summary>
/// Creates access tokens and derives the hash that is stored in their place.
/// </summary>
/// <remarks>
/// A token is 32 bytes from the operating system's CSPRNG, base64url encoded behind a short
/// prefix that says what kind of token it is. The prefix costs nothing and makes a leaked
/// token recognisable in a log or a secret scanner.
/// <para>
/// Only a SHA-256 hash is stored. A password needs a slow, salted hash because people choose
/// guessable ones; a 256 bit random value cannot be guessed, so a fast unsalted hash is
/// enough to make a stolen database useless while keeping the lookup a single indexed query.
/// </para>
/// </remarks>
public static class AccessTokens
{
    /// <summary>Prefix of a token that identifies a client device.</summary>
    public const string DevicePrefix = "vpd_";

    /// <summary>Prefix of a token that identifies a gateway's sync agent.</summary>
    public const string GatewayPrefix = "vpg_";

    /// <summary>Length of the encoded random part: 32 bytes in unpadded base64url.</summary>
    private const int EncodedLength = 43;

    /// <summary>Generates a new token.</summary>
    /// <param name="prefix"><see cref="DevicePrefix"/> or <see cref="GatewayPrefix"/>.</param>
    /// <returns>The token, to be shown once and then forgotten.</returns>
    public static string Create(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        byte[] bytes = RandomNumberGenerator.GetBytes(32);

        string encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return prefix + encoded;
    }

    /// <summary>Hashes a token for storage or lookup.</summary>
    /// <param name="token">The token as presented.</param>
    /// <returns>SHA-256 of the UTF-8 bytes, as lower case hex.</returns>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    /// <summary>
    /// Checks that a string has the shape of a token of the given kind.
    /// </summary>
    /// <param name="token">Candidate token.</param>
    /// <param name="prefix">Expected prefix.</param>
    /// <returns><c>true</c> when the prefix matches and the rest is 43 base64url characters.</returns>
    /// <remarks>
    /// Checked before hashing so that a device token presented to a gateway endpoint, or a
    /// megabyte of garbage in the header, is refused without a database query.
    /// </remarks>
    public static bool HasShape(string? token, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        if (token is null || !token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> body = token.AsSpan(prefix.Length);
        if (body.Length != EncodedLength)
        {
            return false;
        }

        foreach (char c in body)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
