namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// An empty directory in a package version's manifest
/// </summary>
public sealed record UniversalPackageEmptyDirectory : UniversalPackageBlob
{
    /// <summary>
    /// An empty directory in a package version's manifest
    /// </summary>
    /// <param name="path">The path within the package, using forward slashes and without a leading slash</param>
    public UniversalPackageEmptyDirectory(string path) : base(path)
    {
    }
}
