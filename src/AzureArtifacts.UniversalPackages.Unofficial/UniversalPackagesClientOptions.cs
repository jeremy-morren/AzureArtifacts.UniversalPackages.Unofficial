using System.Net;

#if NETSTANDARD
#pragma warning disable CS1574
#endif

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// Options of a <see cref="UniversalPackagesClient"/>
/// </summary>
public sealed class UniversalPackagesClientOptions
{
    /// <summary>The default <see cref="UrlBatchSize"/></summary>
    public const int DefaultUrlBatchSize = 100;

    /// <summary>The default <see cref="MaxRetries"/></summary>
    public const int DefaultMaxRetries = 4;

    /// <summary>The default <see cref="RetryDelay"/></summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The default <see cref="MaxRetryDelay"/></summary>
    public static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most blob identifiers resolved to storage URLs per request (default 100). At least 1. Signed URLs expire, so
    /// they are resolved shortly before they are used
    /// </summary>
    public int UrlBatchSize { get; set; } = DefaultUrlBatchSize;

    /// <summary>
    /// How many times a request is retried after a transient failure (default 4): a network error or transient HTTP error.
    /// 0 disables retries.
    /// </summary>
    /// <remarks>
    /// Transient HTTP errors are <see cref="HttpStatusCode.RequestTimeout"/>, <see cref="HttpStatusCode.TooManyRequests"/>,
    /// <see cref="HttpStatusCode.InternalServerError"/>, <see cref="HttpStatusCode.BadGateway"/>,
    /// <see cref="HttpStatusCode.ServiceUnavailable"/> or <see cref="HttpStatusCode.GatewayTimeout"/>
    /// </remarks>
    public int MaxRetries { get; set; } = DefaultMaxRetries;

    /// <summary>
    /// The delay before the first retry (default 1 second), doubling for each further retry. A <c>Retry-After</c>
    /// header, when sent, is used instead (up to <see cref="MaxRetryDelay"/>)
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = DefaultRetryDelay;

    /// <summary>The longest delay before a retry (default 1 minute)</summary>
    public TimeSpan MaxRetryDelay { get; set; } = DefaultMaxRetryDelay;

    internal void Validate(string argumentName)
    {
        if (UrlBatchSize < 1)
            throw new ArgumentOutOfRangeException(argumentName,
                $"{nameof(UrlBatchSize)} must be at least 1");
        if (MaxRetries < 0)
            throw new ArgumentOutOfRangeException(argumentName,
                $"{nameof(MaxRetries)} must not be negative");
    }
}
