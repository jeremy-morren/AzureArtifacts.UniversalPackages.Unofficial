using AzureArtifacts.UniversalPackages.Unofficial.Json;

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>An Azure Artifacts feed</summary>
public sealed record ArtifactFeed
{
    /// <summary>An Azure Artifacts feed</summary>
    /// <param name="id">The feed ID</param>
    /// <param name="name">The feed name</param>
    /// <param name="projectId">The ID of the project the feed belongs to, or null for an organization-scoped feed</param>
    /// <param name="projectName">The name of the project the feed belongs to, or null for an organization-scoped feed</param>
    public ArtifactFeed(string id, string name, string? projectId, string? projectName)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ProjectId = projectId;
        ProjectName = projectName;
    }

    /// <summary>The feed ID</summary>
    public string Id { get; }

    /// <summary>The feed name</summary>
    public string Name { get; }
    
    /// <summary>The ID of the project the feed belongs to, or null for an organization-scoped feed</summary>
    public string? ProjectId { get; }

    /// <summary>The name of the project the feed belongs to, or null for an organization-scoped feed</summary>
    public string? ProjectName { get; }

    internal static ArtifactFeed FromDto(FeedDto dto) =>
        new(dto.Id, dto.Name, dto.Project?.Id, dto.Project?.Name);
}
