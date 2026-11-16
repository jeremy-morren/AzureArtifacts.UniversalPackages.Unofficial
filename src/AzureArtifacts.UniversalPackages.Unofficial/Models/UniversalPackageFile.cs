namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// A file in a package version's manifest, with its content's dedup identifier and size
/// </summary>
public sealed record UniversalPackageFile : UniversalPackageBlob
{
    /// <summary>
    /// A file in a package version's manifest, with its content's dedup identifier and size
    /// </summary>
    /// <param name="path">The filename within the package, using forward slashes and without a leading slash</param>
    /// <param name="id">The dedup identifier of the file's content (66 hexadecimal characters)</param>
    /// <param name="size">The content size in bytes</param>
    public UniversalPackageFile(string path, string id, long size) : base(path)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegative(size);
#else
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be non-negative");
#endif
        
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Size = size;
    }

    /// <summary>The dedup identifier of the file's content (66 hexadecimal characters)</summary>
    public string Id { get; }

    /// <summary>The content size in bytes</summary>
    public long Size { get; }
}