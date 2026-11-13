#pragma warning disable CS8618

namespace AzureArtifacts.UniversalPackages.Unofficial.Json;

// The service's JSON, as mutable classes with explicit names (works with the .NET 6 source generator)

internal sealed class FeedListDto
{
    public List<FeedDto>? Value { get; set; }
}

internal sealed class FeedDto
{
    public string Id { get; set; }
    public string Name { get; set; }
}

internal sealed class PackageListDto
{
    public int Count { get; set; }
    public List<PackageDto>? Value { get; set; }
}

internal sealed class PackageDto
{
    public string Id { get; set; }
    public string Name { get; set; }
    public List<PackageVersionDto>? Versions { get; set; }
}

internal sealed class PackageVersionDto
{
    public string Version { get; set; }
    public bool IsLatest { get; set; }
    public bool IsListed { get; set; }
    public DateTimeOffset? PublishDate { get; set; }
}

/// <summary>A package version's content pointers (the packaging service, <c>intent=Download</c>)</summary>
internal sealed class VersionMetadataDto
{
    public string? Version { get; set; }
    public string ManifestId { get; set; }
    public string? SuperRootId { get; set; }
    public long PackageSize { get; set; }
}

/// <summary>The manifest blob: the package's files and the dedup blob of each</summary>
internal sealed class ManifestDto
{
    public string? ManifestFormat { get; set; }
    public List<ManifestItemDto>? Items { get; set; }
}

internal sealed class ManifestItemDto
{
    public string Path { get; set; }

    /// <summary>The content of a file; absent for an empty directory</summary>
    public ManifestBlobDto? Blob { get; set; }

    /// <summary>The item type: <c>File</c> or <c>EmptyDirectory</c>. Absent for a file</summary>
    public string? Type { get; set; }
}

internal sealed class ManifestBlobDto
{
    public string Id { get; set; }
    public long Size { get; set; }
}