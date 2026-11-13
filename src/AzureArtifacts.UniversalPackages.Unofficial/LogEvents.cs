using Microsoft.Extensions.Logging;

// ReSharper disable MemberHidesStaticFromOuterClass

namespace AzureArtifacts.UniversalPackages.Unofficial;

internal static partial class LogEvents
{
    /// <summary>The event IDs of the messages</summary>
    private static class EventIds
    {
        public const int Request = 1;
        public const int ResolvedUrls = 2;
        public const int RefreshingUrl = 3;
        public const int Downloading = 4;
        public const int Downloaded = 5;
        public const int DownloadingFile = 6;
        public const int Retrying = 7;
    }

    [LoggerMessage(EventIds.Request, LogLevel.Debug, "{Method} {Uri}")]
    public static partial void Request(this ILogger logger, string method, string uri);

    [LoggerMessage(EventIds.ResolvedUrls, LogLevel.Debug, "Resolved {Count} storage URLs")]
    public static partial void ResolvedUrls(this ILogger logger, int count);

    [LoggerMessage(EventIds.RefreshingUrl, LogLevel.Debug, "The storage URL of {BlobId} was rejected ({StatusCode}): resolving it again")]
    public static partial void RefreshingUrl(this ILogger logger, string blobId, int statusCode);

    [LoggerMessage(EventIds.Downloading, LogLevel.Information, "Downloading {PackageName} {Version} from feed {FeedName}: {ItemCount} items, {Size} bytes")]
    public static partial void Downloading(this ILogger logger, string packageName, string version, string feedName, int itemCount, long size);

    [LoggerMessage(EventIds.Downloaded, LogLevel.Information, "Downloaded {PackageName} {Version} in {ElapsedMilliseconds} ms")]
    public static partial void Downloaded(this ILogger logger, string packageName, string version, long elapsedMilliseconds);

    [LoggerMessage(EventIds.DownloadingFile, LogLevel.Debug, "Downloading {Path} ({Size} bytes, {ChunkCount} chunks)")]
    public static partial void DownloadingFile(this ILogger logger, string path, long size, int chunkCount);

    [LoggerMessage(EventIds.Retrying, LogLevel.Warning, "{Method} {Uri} failed ({Reason}): retry {Attempt} of {MaxRetries} in {DelayMilliseconds:0.##} ms")]
    public static partial void Retrying(this ILogger logger, 
        string method, string uri, string reason, int attempt, int maxRetries, double delayMilliseconds);
}
