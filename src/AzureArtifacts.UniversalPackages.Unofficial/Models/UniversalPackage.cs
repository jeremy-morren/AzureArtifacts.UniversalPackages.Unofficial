using AzureArtifacts.UniversalPackages.Unofficial.Json;

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>A Universal Package in a feed, with its versions (newest first, as the service lists them)</summary>
public sealed record UniversalPackage
{
    /// <summary>A Universal Package in a feed, with its versions (newest first, as the service lists them)</summary>
    /// <param name="id">The package ID</param>
    /// <param name="name">The package name</param>
    /// <param name="versions">The versions</param>
    public UniversalPackage(string id, string name, IReadOnlyList<UniversalPackageVersion> versions)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Versions = versions ?? throw new ArgumentNullException(nameof(versions));
    }

    /// <summary>The package ID</summary>
    public string Id { get; }

    /// <summary>The package name</summary>
    public string Name { get; }

    /// <summary>The versions</summary>
    public IReadOnlyList<UniversalPackageVersion> Versions { get; }
    
    internal static UniversalPackage FromDto(PackageDto dto) =>
        new(dto.Id, dto.Name, (dto.Versions ?? []).Select(UniversalPackageVersion.FromDto).ToList());
}