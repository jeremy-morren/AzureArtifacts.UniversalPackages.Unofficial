namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// A callback that receives each item of a package version as it downloads
/// </summary>
public interface IPackageDownloader
{
    /// <summary>
    /// Downloads a file from a package version, given its manifest item and a stream of its content.
    /// </summary>
    /// <param name="blob">The manifest item representing the file to download</param>
    /// <param name="source">
    /// The item's content: a forward-only, readable stream of exactly <see cref="FileBlob.Size"/> bytes,
    /// fed as the content downloads. It is only valid until the returned task completes,
    /// after which content left unread is downloaded and discarded
    /// </param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the download</param>
    /// <returns>A Task representing the asynchronous operation</returns>
    Task DownloadFileAsync(FileBlob blob, Stream source, CancellationToken cancellationToken);
    
    /// <summary>
    /// Handles an empty directory from a package version, given its manifest item.
    /// </summary>
    /// <param name="blob">The manifest item representing the empty directory to handle</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <returns>A Task representing the asynchronous operation</returns>
    Task HandleEmptyDirectoryAsync(EmptyDirectoryBlob blob, CancellationToken cancellationToken);
}
