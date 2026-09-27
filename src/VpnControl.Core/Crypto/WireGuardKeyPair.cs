using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace VpnControl.Core.Crypto;

/// <summary>
/// An X25519 key pair in the form WireGuard expects: 32 raw bytes each, carried
/// as standard base64.
/// </summary>
/// <remarks>
/// WireGuard identifies a peer by its Curve25519 public key and has no certificate
/// or handshake negotiation to speak of. Generating a fresh pair per session means a
/// key recovered from disk later cannot be tied back to a past session, which is why
/// the connection manager does not persist one.
/// <para>
/// The type implements <see cref="IDisposable"/> because it holds secret bytes.
/// Disposing overwrites the private key buffer, which narrows the window in which a
/// crash dump or a swapped page could still expose it. That is a real narrowing and
/// not a guarantee: the base64 string handed to a configuration file lives on the
/// managed heap and cannot be wiped the same way, which is noted in the design notes.
/// </para>
/// </remarks>
public sealed class WireGuardKeyPair : IDisposable
{
    /// <summary>Length of an X25519 key in bytes, fixed by the curve.</summary>
    public const int KeyLength = 32;

    private readonly byte[] _privateKey;
    private bool _disposed;

    private WireGuardKeyPair(byte[] privateKey, byte[] publicKey)
    {
        _privateKey = privateKey;
        PublicKeyBase64 = Convert.ToBase64String(publicKey);
    }

    /// <summary>Base64 encoded public key, safe to send to the control plane.</summary>
    public string PublicKeyBase64 { get; }

    /// <summary>
    /// Base64 encoded private key, for the <c>[Interface]</c> section of a tunnel
    /// configuration.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The pair has been disposed.</exception>
    public string PrivateKeyBase64
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Convert.ToBase64String(_privateKey);
        }
    }

    /// <summary>Generates a new random key pair.</summary>
    /// <returns>A pair whose private key is backed by the platform random source.</returns>
    public static WireGuardKeyPair Generate()
    {
        var generator = new X25519KeyPairGenerator();
        generator.Init(new X25519KeyGenerationParameters(new SecureRandom()));

        Org.BouncyCastle.Crypto.AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
        byte[] privateKey = ((X25519PrivateKeyParameters)pair.Private).GetEncoded();
        byte[] publicKey = ((X25519PublicKeyParameters)pair.Public).GetEncoded();

        return new WireGuardKeyPair(privateKey, publicKey);
    }

    /// <summary>Rebuilds a pair from a stored base64 private key.</summary>
    /// <param name="privateKeyBase64">The 32 byte private key, base64 encoded.</param>
    /// <returns>A pair whose public key is derived from the private key.</returns>
    /// <exception cref="ArgumentException">The input is not a 32 byte base64 value.</exception>
    /// <remarks>
    /// Present so a client that does persist a key can load it. Nothing in this
    /// solution calls it during a normal connection, because sessions use fresh keys.
    /// </remarks>
    public static WireGuardKeyPair FromPrivateKey(string privateKeyBase64)
    {
        byte[] privateKey = DecodeKey(privateKeyBase64, nameof(privateKeyBase64));
        var parameters = new X25519PrivateKeyParameters(privateKey, 0);
        return new WireGuardKeyPair(privateKey, parameters.GeneratePublicKey().GetEncoded());
    }

    /// <summary>
    /// Checks that a string is a plausible WireGuard key: base64 of exactly 32 bytes.
    /// </summary>
    /// <param name="keyBase64">Candidate key.</param>
    /// <returns><c>true</c> when the value decodes to 32 bytes.</returns>
    /// <remarks>
    /// Used to validate a public key arriving at the API. It proves the shape, not
    /// that anyone holds the matching private key, which only a handshake can show.
    /// </remarks>
    public static bool IsValidKey(string? keyBase64)
    {
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[KeyLength + 1];
        return Convert.TryFromBase64String(keyBase64, buffer, out int written) && written == KeyLength;
    }

    /// <summary>Overwrites the private key bytes held by this instance.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_privateKey);
        _disposed = true;
    }

    private static byte[] DecodeKey(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The key is not valid base64.", parameterName, ex);
        }

        if (decoded.Length != KeyLength)
        {
            throw new ArgumentException($"An X25519 key is {KeyLength} bytes, this one decoded to {decoded.Length}.", parameterName);
        }

        return decoded;
    }
}
