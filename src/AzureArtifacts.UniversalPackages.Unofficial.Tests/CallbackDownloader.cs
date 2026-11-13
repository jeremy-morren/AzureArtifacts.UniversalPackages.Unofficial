namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

/// <summary>An <see cref="IPackageDownloader"/> of callbacks</summary>
internal sealed class CallbackDownloader : IPackageDownloader
{
    private readonly Func<FileBlob, Stream, CancellationToken, Task> _file;
    private readonly Func<EmptyDirectoryBlob, CancellationToken, Task>? _directory;

    /// <param name="file">Handles each file</param>
    /// <param name="directory">Handles each empty directory, or null to ignore them</param>
    public CallbackDownloader(
        Func<FileBlob, Stream, CancellationToken, Task> file,
        Func<EmptyDirectoryBlob, CancellationToken, Task>? directory = null)
    {
        _file = file;
        _directory = directory;
    }

    public Task DownloadFileAsync(FileBlob blob, Stream source, CancellationToken cancellationToken) =>
        _file(blob, source, cancellationToken);

    public Task HandleEmptyDirectoryAsync(EmptyDirectoryBlob blob, CancellationToken cancellationToken) =>
        _directory?.Invoke(blob, cancellationToken) ?? Task.CompletedTask;
}
