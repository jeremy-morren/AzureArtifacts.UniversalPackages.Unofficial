namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>The manifest of a Universal Package version: its files and empty directories</summary>
public sealed record UniversalPackageManifest
{
    /// <summary>The manifest of a Universal Package version: its files and empty directories</summary>
    /// <param name="packageName">The package name</param>
    /// <param name="version">The version</param>
    /// <param name="items">The items, in manifest order</param>
    public UniversalPackageManifest(string packageName, string version, IReadOnlyList<UniversalPackageBlob> items)
    {
        PackageName = packageName ?? throw new ArgumentNullException(nameof(packageName));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    /// <summary>The package name</summary>
    public string PackageName { get; }

    /// <summary>The version</summary>
    public string Version { get; }

    /// <summary>The items, in manifest order</summary>
    public IReadOnlyList<UniversalPackageBlob> Items { get; }
    
    /// <summary>The items that are files, in manifest order</summary>
    public IEnumerable<UniversalPackageFile> Files => Items.OfType<UniversalPackageFile>();

    /// <summary>The total size of the files, in bytes</summary>
    public long TotalSize => Files.Sum(i => i.Size);
}