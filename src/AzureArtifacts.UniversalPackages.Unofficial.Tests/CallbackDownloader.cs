namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

/// <summary>An <see cref="IPackageDownloader"/> of callbacks</summary>
internal sealed class CallbackDownloader : IPackageDownloader
{
    private readonly Func<UniversalPackageFile, Stream?> _existing;
    private readonly Func<UniversalPackageFile, Stream, CancellationToken, Task> _file;
    private readonly Func<UniversalPackageEmptyDirectory, CancellationToken, Task> _directory;

    /// <param name="file">Handles each file</param>
    /// <param name="existing">Offers each file's existing content, or null to offer none</param>
    /// <param name="directory">Handles each empty directory, or null to ignore them</param>
    public CallbackDownloader(
        Func<UniversalPackageFile, Stream, CancellationToken, Task> file,
        Func<UniversalPackageFile, Stream?>? existing = null,
        Func<UniversalPackageEmptyDirectory, CancellationToken, Task>? directory = null)
    {
        _file = file;
        _existing = existing ?? (_ => null);
        _directory = directory ?? ((_, _) => Task.CompletedTask);
    }

    public Task<Stream?> ShouldDownloadFileAsync(UniversalPackageFile blob, CancellationToken cancellationToken) =>
        Task.FromResult(_existing?.Invoke(blob));

    public Task DownloadFileAsync(UniversalPackageFile blob, Stream source, CancellationToken cancellationToken) =>
        _file(blob, source, cancellationToken);

    public Task HandleEmptyDirectoryAsync(UniversalPackageEmptyDirectory blob, CancellationToken cancellationToken) =>
        _directory(blob, cancellationToken);
}
