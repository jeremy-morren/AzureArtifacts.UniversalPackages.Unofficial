namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// A callback that receives each item of a package version as it downloads
/// </summary>
public interface IPackageDownloader
{
    /// <summary>
    /// Gets a file's existing content so that it is downloaded only when it differs
    /// </summary>
    /// <remarks>
    /// Called for every selected file, in manifest order, before any content is downloaded.
    /// A stream that is returned is read to its end, hashed chunk by chunk against <see cref="UniversalPackageFile.Id"/>, and disposed.
    /// When its content matches, the file's content is not downloaded and <see cref="DownloadFileAsync"/> is not called for it.
    /// </remarks>
    /// <param name="blob">The manifest item representing the file</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <returns>
    /// A readable stream of the file's existing content (positioned at its start), or null to download the file
    /// </returns>
    Task<Stream?> ShouldDownloadFileAsync(UniversalPackageFile blob, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a file from a package version, given its manifest item and a stream of its content.
    /// </summary>
    /// <param name="blob">The manifest item representing the file to download</param>
    /// <param name="source">
    /// The item's content: a forward-only, readable stream of exactly <see cref="UniversalPackageFile.Size"/> bytes,
    /// fed as the content downloads. It is only valid until the returned task completes,
    /// after which content left unread is downloaded and discarded
    /// </param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the download</param>
    /// <returns>A Task representing the asynchronous operation</returns>
    Task DownloadFileAsync(UniversalPackageFile blob, Stream source, CancellationToken cancellationToken);
    
    /// <summary>
    /// Handles an empty directory from a package version, given its manifest item.
    /// </summary>
    /// <param name="blob">The manifest item representing the empty directory to handle</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <returns>A Task representing the asynchronous operation</returns>
    Task HandleEmptyDirectoryAsync(UniversalPackageEmptyDirectory blob, CancellationToken cancellationToken);
}
