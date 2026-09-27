namespace VpnControl.Core.Servers;

/// <summary>
/// Raised when the control plane could not be reached or answered in a way the
/// client cannot use.
/// </summary>
/// <remarks>
/// Every implementation of <see cref="IServerCatalogClient"/> reports failure with
/// this one type. Without it, callers would have to catch <c>HttpRequestException</c>,
/// <c>JsonException</c> and <c>TaskCanceledException</c> separately, which would
/// leak the transport into code that has no business knowing about it.
/// </remarks>
public sealed class ServerCatalogException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong, in terms a log reader can act on.</param>
    public ServerCatalogException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying failure.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The transport or parsing failure behind it.</param>
    public ServerCatalogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>HTTP status code that caused the failure, when there was one.</summary>
    public int? StatusCode { get; init; }
}
