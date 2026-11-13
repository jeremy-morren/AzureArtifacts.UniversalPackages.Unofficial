// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>Options of a download</summary>
public class UniversalPackageDownloadOptions
{
    /// <summary>
    /// Creates the options with default values
    /// </summary>
    public UniversalPackageDownloadOptions()
    {
    }
    
    /// <summary>
    /// Creates the options as a copy of another instance
    /// </summary>
    /// <param name="other">The options to copy</param>
    public UniversalPackageDownloadOptions(UniversalPackageDownloadOptions other)
    {
        if (other == null)
            throw new ArgumentNullException(nameof(other));
        
        Parallelism = other.Parallelism;
        SessionId = other.SessionId;
        CopyBufferSize = other.CopyBufferSize;
        DownloadFilter = other.DownloadFilter;
    }
    
    
    /// <summary>The default <see cref="Parallelism"/></summary>
    internal const int DefaultParallelism = 192;

    /// <summary>The most blobs (chunks and nodes) downloaded at once. Default: 192</summary>
    public int Parallelism { get; set; } = DefaultParallelism;

    /// <summary>
    /// A callback to select the manifest items to download, or null to download all items.
    /// </summary>
    public Func<Blob, bool>? DownloadFilter { get; set; }
    
    /// <summary>
    /// A session ID that identifies the download's requests to Azure DevOps.
    /// It is sent as the <c>X-TFS-Session</c> header to Azure DevOps,
    /// and as <c>x-ms-client-request-id: X_TFS_Session: {id}</c> to storage.
    /// </summary>
    public Guid? SessionId { get; set; }
    
    /// <summary>The default value of <see cref="CopyBufferSize"/></summary>
    private const int DefaultCopyBufferSize = 81920;
    
    /// <summary>The buffer size of copies to files and streams. Default: 81920</summary>
    public int CopyBufferSize { get; set; } = DefaultCopyBufferSize;
    
    internal void Validate(string argumentName)
    {
        if (Parallelism < 1)
            throw new ArgumentOutOfRangeException(argumentName,
                $"{nameof(Parallelism)} must be at least 1");
        if (CopyBufferSize < 1)
            throw new ArgumentOutOfRangeException(argumentName,
                $"{nameof(CopyBufferSize)} must be at least 1");
    }

}