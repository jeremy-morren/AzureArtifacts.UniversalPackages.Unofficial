// ReSharper disable MemberCanBePrivate.Global

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// The Azure DevOps organization (and project, for project-scoped feeds)
/// whose feeds a client reads, and the service endpoints it calls
/// </summary>
public class AzureArtifactsLocation
{
    /// <param name="organization">The organization name, e.g. <c>contoso</c> for https://dev.azure.com/contoso</param>
    /// <param name="project">The project for project-scoped feeds, or null for organization-scoped feeds</param>
    /// <param name="instance">The Azure DevOps instance. Default: <c>dev.azure.com</c></param>
    public AzureArtifactsLocation(string organization, string? project = null, string? instance = null)
    {
        if (string.IsNullOrWhiteSpace(organization))
            throw new ArgumentException("An organization is required", nameof(organization));

        Instance = string.IsNullOrEmpty(instance) ? "dev.azure.com" : instance;
        Organization = organization;
        Project = string.IsNullOrEmpty(project) ? null : project;

        organization = Uri.EscapeDataString(organization);
        FeedsUri = new Uri($"https://feeds.{Instance}/{organization}/");
        PackagingUri = new Uri($"https://pkgs.{Instance}/{organization}/");
        DedupUri = new Uri($"https://vsblob.{Instance}/{organization}/");
    }
    
    /// <summary>The Azure DevOps instance, e.g. <c>dev.azure.com</c></summary>
    public string Instance { get; }

    /// <summary>The organization name</summary>
    public string Organization { get; }

    /// <summary>The project of project-scoped feeds, or null for organization-scoped feeds</summary>
    public string? Project { get; }

    /// <summary>The feed management service (feeds and package listings)</summary>
    public Uri FeedsUri { get; }

    /// <summary>The packaging service (package version metadata)</summary>
    public Uri PackagingUri { get; }

    /// <summary>The dedup (blob store) service, which resolves content to signed storage URLs</summary>
    public Uri DedupUri { get; }

    /// <summary>The project path segment (with a trailing slash), or empty for organization-scoped feeds</summary>
    internal string ProjectSegment => Project == null ? "" : Uri.EscapeDataString(Project) + "/";
}
