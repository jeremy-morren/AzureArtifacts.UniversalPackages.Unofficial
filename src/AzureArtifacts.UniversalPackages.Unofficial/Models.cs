// ReSharper disable NotAccessedPositionalProperty.Global

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>An Azure Artifacts feed</summary>
/// <param name="Id">The feed ID</param>
/// <param name="Name">The feed name</param>
public sealed record ArtifactFeed(string Id, string Name);

/// <summary>A Universal Package in a feed, with its versions (newest first, as the service lists them)</summary>
/// <param name="Id">The package ID</param>
/// <param name="Name">The package name</param>
/// <param name="Versions">The versions</param>
public sealed record UniversalPackage(string Id, string Name, IReadOnlyList<UniversalPackageVersion> Versions);

/// <summary>A version of a Universal Package</summary>
/// <param name="Version">The version, e.g. <c>1.2.3</c></param>
/// <param name="IsLatest">Whether this is the latest version</param>
/// <param name="IsListed">Whether the version is listed</param>
/// <param name="PublishDate">When the version was published, if known</param>
public sealed record UniversalPackageVersion(string Version, bool IsLatest, bool IsListed, DateTimeOffset? PublishDate);

/// <summary>The manifest of a Universal Package version: its files and empty directories</summary>
/// <param name="PackageName">The package name</param>
/// <param name="Version">The version</param>
/// <param name="Items">The items, in manifest order</param>
public sealed record UniversalPackageManifest(string PackageName, string Version, IReadOnlyList<Blob> Items)
{
    /// <summary>The items that are files, in manifest order</summary>
    public IEnumerable<FileBlob> Files => Items.OfType<FileBlob>();
    
    /// <summary>The total size of the files, in bytes</summary>
    public long TotalSize => Files.Sum(i => i.Size);
}

/// <summary>
/// An item of a package version's manifest: either a <see cref="FileBlob"/> or <see cref="EmptyDirectoryBlob"/>
/// </summary>
/// <param name="Path">The path within the package, using forward slashes and without a leading slash</param>
public abstract record Blob(string Path);

/// <summary>
/// A file in a package version's manifest, with its content's dedup identifier and size
/// </summary>
/// <param name="Path">The filename within the package, using forward slashes and without a leading slash</param>
/// <param name="Id">The dedup identifier of the file's content (66 hexadecimal characters)</param>
/// <param name="Size">The content size in bytes</param>
public sealed record FileBlob(string Path, string Id, long Size) : Blob(Path);

/// <summary>
/// An empty directory in a package version's manifest
/// </summary>
/// <param name="Path">The path within the package, using forward slashes and without a leading slash</param>
public sealed record EmptyDirectoryBlob(string Path) : Blob(Path);
