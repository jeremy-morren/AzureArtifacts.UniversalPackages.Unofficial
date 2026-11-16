using AzureArtifacts.UniversalPackages.Unofficial.Json;

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>A version of a Universal Package</summary>
public sealed record UniversalPackageVersion
{
    /// <summary>A version of a Universal Package</summary>
    /// <param name="version">The version, e.g. <c>1.2.3</c></param>
    /// <param name="isLatest">Whether this is the latest version</param>
    /// <param name="isListed">Whether the version is listed</param>
    /// <param name="publishDate">When the version was published, if known</param>
    public UniversalPackageVersion(string version, bool isLatest, bool isListed, DateTimeOffset? publishDate)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        IsLatest = isLatest;
        IsListed = isListed;
        PublishDate = publishDate;
    }

    /// <summary>The version, e.g. <c>1.2.3</c></summary>
    public string Version { get; }

    /// <summary>Whether this is the latest version</summary>
    public bool IsLatest { get; }

    /// <summary>Whether the version is listed</summary>
    public bool IsListed { get; }

    /// <summary>When the version was published, if known</summary>
    public DateTimeOffset? PublishDate { get; }
    
    internal static UniversalPackageVersion FromDto(PackageVersionDto dto) =>
        new(dto.Version, dto.IsLatest, dto.IsListed, dto.PublishDate);
}