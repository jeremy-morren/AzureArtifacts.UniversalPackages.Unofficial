namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// An item of a package version's manifest: either a <see cref="UniversalPackageFile"/> or <see cref="UniversalPackageEmptyDirectory"/>
/// </summary>
public abstract record UniversalPackageBlob
{
    internal UniversalPackageBlob(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The path within the package, using forward slashes and without a leading slash</summary>
    public string Path { get; }
}