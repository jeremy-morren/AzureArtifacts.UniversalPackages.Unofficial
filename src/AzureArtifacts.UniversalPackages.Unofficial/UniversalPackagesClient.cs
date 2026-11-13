using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using AzureArtifacts.UniversalPackages.Unofficial.Dedup;
using AzureArtifacts.UniversalPackages.Unofficial.Json;

// ReSharper disable NotAccessedPositionalProperty.Local
// ReSharper disable ArrangeObjectCreationWhenTypeNotEvident

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// Lists and downloads Azure Artifacts Universal Packages over plain HTTP
/// </summary>
/// <remarks>
/// <para>
/// Credentials come from an <see cref="IAzureArtifactsCredentialProvider"/>,
/// which is asked for the <c>Authorization</c> header of each Azure DevOps request.
/// Storage rejects a signed URL whose request also carries an Azure DevOps token,
/// so the <see cref="HttpClient"/> itself must not add credentials.
/// </para>
/// <para>
/// The protocol is modelled on the <c>artifacts_download</c> module of Microsoft's Azure DevOps Rust API
/// (<see href="https://github.com/microsoft/azure-devops-rust-api">https://github.com/microsoft/azure-devops-rust-api</see>, MIT).
/// </para>
/// </remarks>
public sealed class UniversalPackagesClient
{
    private const string FeedsApiVersion = "7.1";
    private const string PackagingAccept = "application/json; api-version=7.1-preview.1";

    /// <summary>The <c>intent</c> of a package version request that downloads its content</summary>
    private const string DownloadIntent = "Download";

    /// <summary>The <c>protocolType</c> of Universal Packages in package listings</summary>
    private const string UniversalPackagesProtocol = "upack";

    /// <summary>The packages per listing request</summary>
    private const int PageSize = 200;

    private const char PathSeparator = '/';
    private const char WindowsPathSeparator = '\\';
    private const string CurrentDirectory = ".";
    private const string ParentDirectory = "..";

    /// <summary>The index of the entry being handed over, before the first</summary>
    private const int BeforeFirstEntry = -1;

    private readonly HttpClient _httpClient;
    private readonly AzureArtifactsLocation _location;
    private readonly IAzureArtifactsCredentialProvider _credentialProvider;
    private readonly UniversalPackagesClientOptions _options;
    private readonly ILogger<UniversalPackagesClient>? _logger;
    private readonly DedupBlobStore _blobs;

    /// <summary>Creates a new instance of <see cref="UniversalPackagesClient"/></summary>
    /// <param name="httpClient">The HTTP client to use. It must not add credentials.</param>
    /// <param name="location">The organization (and project, for project-scoped feeds)</param>
    /// <param name="credentialProvider">
    /// Supplies the <c>Authorization</c> header of Azure DevOps requests (see <see cref="AzureArtifactsCredentials"/>)
    /// </param>
    /// <param name="options">Options, or null for the defaults</param>
    /// <param name="logger">The logger</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="httpClient"/> has a default <c>Authorization</c> header, or an option is out of range
    /// </exception>
    public UniversalPackagesClient(
        HttpClient httpClient, 
        AzureArtifactsLocation location,
        IAzureArtifactsCredentialProvider credentialProvider, 
        UniversalPackagesClientOptions? options = null,
        ILogger<UniversalPackagesClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        
        _options = options ?? new UniversalPackagesClientOptions();
        _logger = logger;

        // It would also be sent to storage, which rejects it
        if (httpClient.DefaultRequestHeaders.Authorization != null)
            throw new ArgumentException(
                $"The HttpClient has a default Authorization header. Supply credentials with {nameof(credentialProvider)} instead",
                nameof(httpClient));
        
        _options.Validate(nameof(options));

        _blobs = new DedupBlobStore(httpClient, _credentialProvider, location, _options, logger);
    }

    /// <summary>Lists the feeds of the organization (or project)</summary>
    public async Task<IReadOnlyList<ArtifactFeed>> ListFeedsAsync(CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_location.FeedsUri,
            $"{_location.ProjectSegment}_apis/packaging/feeds?api-version={FeedsApiVersion}");
        var feeds = await GetJsonAsync(uri, HttpNames.JsonMediaType, ArtifactsJsonContext.Default.FeedListDto,
            "list feeds", sessionId: null, cancellationToken).ConfigureAwait(false);

        return
        [
            .. (feeds.Value ?? [])
            .Select(f => new ArtifactFeed(f.Id, f.Name))
        ];
    }

    /// <summary>Lists the Universal Packages of a feed</summary>
    /// <param name="feedName">The feed name (or ID)</param>
    /// <param name="nameQuery">Only packages whose name contains this text, or null for all</param>
    /// <param name="includeAllVersions">Whether to list every version of each package, or only the latest</param>
    /// <param name="cancellationToken">A token that can be used to cancel the async operation</param>
    public async Task<IReadOnlyList<UniversalPackage>> ListPackagesAsync(
        string feedName, 
        string? nameQuery = null,
        bool includeAllVersions = false, 
        CancellationToken cancellationToken = default)
    {
        Required(feedName, nameof(feedName));

        var packages = new List<UniversalPackage>();
        for (var skip = 0;; skip += PageSize)
        {
            var query = $"protocolType={UniversalPackagesProtocol}" +
                        $"&includeAllVersions={(includeAllVersions ? "true" : "false")}" +
                        (nameQuery is { Length: > 0 } name ? $"&packageNameQuery={Uri.EscapeDataString(name)}" : "") +
                        $"&$top={PageSize}&$skip={skip}&api-version={FeedsApiVersion}";
            var uri = new Uri(_location.FeedsUri,
                $"{_location.ProjectSegment}_apis/packaging/feeds/{Uri.EscapeDataString(feedName)}/packages?{query}");

            var page = await GetJsonAsync(uri, HttpNames.JsonMediaType, ArtifactsJsonContext.Default.PackageListDto,
                $"list the packages of feed {feedName}", sessionId: null, cancellationToken).ConfigureAwait(false);
            var values = page.Value ?? [];

            packages.AddRange(values
                .Select(p => new UniversalPackage(
                    p.Id, 
                    p.Name,
                    [.. (p.Versions ?? []).Select(v =>
                            new UniversalPackageVersion(v.Version, v.IsLatest, v.IsListed, v.PublishDate))])
                ));

            if (values.Count < PageSize)
                return packages;
        }
    }

    /// <summary>Gets a package, with all its versions</summary>
    /// <param name="feedName">The feed name (or ID)</param>
    /// <param name="packageName">The package name</param>
    /// <param name="cancellationToken">A token that can be used to cancel the async operation</param>
    /// <returns>The package, or null when the feed has no package of that name</returns>
    public async Task<UniversalPackage?> GetPackageAsync(
        string feedName, 
        string packageName,
        CancellationToken cancellationToken = default)
    {
        Required(packageName, nameof(packageName));

        // The name query is a substring match: pick the exact name
        var packages = await ListPackagesAsync(
                feedName, packageName, includeAllVersions: true, cancellationToken)
            .ConfigureAwait(false);
        return packages.FirstOrDefault(p => string.Equals(p.Name, packageName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lists the versions of a package</summary>
    /// <param name="feedName">The feed name (or ID)</param>
    /// <param name="packageName">The package name</param>
    /// <param name="cancellationToken">A token that can be used to cancel the async operation</param>
    public async Task<IReadOnlyList<UniversalPackageVersion>> ListVersionsAsync(
        string feedName, 
        string packageName,
        CancellationToken cancellationToken = default)
    {
        var package = await GetPackageAsync(feedName, packageName, cancellationToken).ConfigureAwait(false)
                      ?? throw new UniversalPackagesException($"Feed {feedName} has no package {packageName}");
        return package.Versions;
    }

    /// <summary>Gets the manifest of a package version: its files and empty directories</summary>
    /// <param name="feedName">The feed name (or ID)</param>
    /// <param name="packageName">The package name</param>
    /// <param name="version">The version</param>
    /// <param name="cancellationToken">A token that can be used to cancel the async operation</param>
    public async Task<UniversalPackageManifest> GetManifestAsync(
        string feedName, 
        string packageName, 
        string version,
        CancellationToken cancellationToken = default)
    {
        var (manifest, _) = await GetContentAsync(feedName, packageName, version, DownloadSettings.Default,
            cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>
    /// Downloads a package version, handing each manifest item (that <see cref="UniversalPackageDownloadOptions.DownloadFilter"/>
    /// selects) and its content to <paramref name="downloader"/> as it downloads
    /// </summary>
    /// <remarks>
    /// Items are handed over one at a time, in manifest order, each callback on the thread pool.
    /// Content is downloaded ahead (up to <see cref="UniversalPackageDownloadOptions.Parallelism"/> blobs at once) while a callback reads.
    /// A failure returned from <paramref name="downloader"/> fails the download.
    /// </remarks>
    /// <param name="feedName">The feed name (or ID)</param>
    /// <param name="packageName">The package name</param>
    /// <param name="version">The version</param>
    /// <param name="downloader">Receives each item and its content</param>
    /// <param name="options">Download options, or null for the defaults</param>
    /// <param name="cancellationToken">A token that can be used to cancel the download operation</param>
    /// <returns>The manifest (all its items, including those not selected)</returns>
    public async Task<UniversalPackageManifest> DownloadAsync(
        string feedName, 
        string packageName, 
        string version,
        IPackageDownloader downloader, 
        UniversalPackageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (downloader == null)
            throw new ArgumentNullException(nameof(downloader));
        
        options ??= new();
        options.Validate(nameof(options));

        var settings = new DownloadSettings(options.Parallelism, options.SessionId);
        var (manifest, entries) = await GetContentAsync(
                feedName, packageName, version, settings, cancellationToken)
            .ConfigureAwait(false);
        
        var selected = options.DownloadFilter == null 
            ? entries 
            : [.. entries.Where(e => options.DownloadFilter(e.Blob))];

        var stopwatch = Stopwatch.StartNew();
        _logger?.Downloading(packageName, version, feedName, selected.Count, selected.OfType<FileManifestEntry>().Sum(e => e.Size));
        await DownloadEntriesAsync(selected, downloader, settings, cancellationToken).ConfigureAwait(false);
        _logger?.Downloaded(packageName, version, stopwatch.ElapsedMilliseconds);
        return manifest;
    }

    /// <summary>
    /// Downloads one file of a package version to a stream
    /// </summary>
    /// <param name="feedName">The feed name (or ID)</param>
    /// <param name="packageName">The package name</param>
    /// <param name="version">The version</param>
    /// <param name="path">The file's path in the package (see <see cref="Blob.Path"/>)</param>
    /// <param name="output">The stream to write the file's content to</param>
    /// <param name="options">Download options, or null for the defaults. A filter must also select the file</param>
    /// <param name="cancellationToken">A token that can be used to cancel the download operation</param>
    /// <exception cref="UniversalPackagesException">The package has no such file</exception>
    public async Task<FileBlob> DownloadFileAsync(
        string feedName,
        string packageName,
        string version,
        string path,
        Stream output,
        UniversalPackageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Required(path, nameof(path));
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        options ??= new();
        options.Validate(nameof(options));

        var normalized = path.Replace(WindowsPathSeparator, PathSeparator).TrimStart(PathSeparator);
        var filter = options.DownloadFilter;
        var downloader = new StreamDownloader(output, options.CopyBufferSize);

        await DownloadAsync(
                feedName,
                packageName,
                version,
                downloader,
                new UniversalPackageDownloadOptions(options)
                {
                    DownloadFilter = blob => blob is FileBlob && blob.Path == normalized && (filter == null || filter(blob))
                },
                cancellationToken)
            .ConfigureAwait(false);

        return downloader.Downloaded ?? throw new UniversalPackagesException($"{packageName} {version} has no file {path}");
    }

    /// <summary>
    /// Downloads a package version's files and empty directories into <paramref name="directory"/>
    /// (created if needed), overwriting files of the same name. Other files in the directory are left alone.
    /// </summary>
    /// <returns>The package manifest</returns>
    public async Task<UniversalPackageManifest> DownloadToDirectoryAsync(
        string feedName, 
        string packageName,
        string version, 
        string directory, 
        UniversalPackageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Required(directory, nameof(directory));
        
        options ??= new();
        options.Validate(nameof(options));

        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);

        return await DownloadAsync(
                feedName,
                packageName,
                version,
                new DirectoryDownloader(root, options.CopyBufferSize),
                options, 
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads a package version's metadata, then its manifest</summary>
    private async Task<(UniversalPackageManifest Manifest, IReadOnlyList<ManifestEntry> Entries)> GetContentAsync(
        string feedName, string packageName, string version, DownloadSettings settings, CancellationToken ct)
    {
        Required(feedName, nameof(feedName));
        Required(packageName, nameof(packageName));
        Required(version, nameof(version));

        var uri = new Uri(_location.PackagingUri,
            $"{_location.ProjectSegment}_packaging/{Uri.EscapeDataString(feedName)}/upack/packages/" +
            $"{Uri.EscapeDataString(packageName)}/versions/{Uri.EscapeDataString(version)}?intent={DownloadIntent}");
        
        var metadata = await GetJsonAsync(
                uri,
                PackagingAccept, 
                ArtifactsJsonContext.Default.VersionMetadataDto,
                $"get {packageName} {version} from feed {feedName}", 
                settings.SessionId, 
                ct)
            .ConfigureAwait(false);

        var manifestBytes = await _blobs.ReadAsync(
                ParseId(metadata.ManifestId, "manifest"), settings, ct)
            .ConfigureAwait(false);

        ManifestDto manifestDto;
        try
        {
            manifestDto = JsonSerializer.Deserialize(manifestBytes, ArtifactsJsonContext.Default.ManifestDto)
                       ?? throw new UniversalPackagesException($"The manifest of {packageName} {version} is empty");
        }
        catch (JsonException ex)
        {
            throw new UniversalPackagesException(
                $"The manifest of {packageName} {version} is not valid: {ex.Message}",
                innerException: ex);
        }

        var entries = (manifestDto.Items ?? []).Select(item => ToEntry(item, packageName, version)).ToList();
        var manifest = new UniversalPackageManifest(
            packageName,
            metadata.Version?.Length > 0 ? metadata.Version : version,
            [.. entries.Select(e => e.Blob)]);
        return (manifest,entries);
    }

    private static ManifestEntry ToEntry(ManifestItemDto item, string packageName, string version)
    {
        var path = item.Path.TrimStart(PathSeparator);
        
        // Ensure the service does not return an unsafe path: should be relative with / separators, no empty segments, no . or .. segments
        if (path.Length == 0 || 
            path.Contains(WindowsPathSeparator) ||
            path.Split(PathSeparator).Any(s => s is "" or CurrentDirectory or ParentDirectory))
            throw new UniversalPackagesException($"The manifest of {packageName} {version} has an unsafe path: {item.Path}");

        switch (item.Type)
        {
            case "EmptyDirectory":
                // The item is an empty directory
                return new EmptyDirectoryManifestEntry(new EmptyDirectoryBlob(path));
            
            case null or "File":
                // The item is a file with content
                var content = 
                    item.Blob ?? throw new UniversalPackagesException($"The manifest of {packageName} {version} has no blob for {item.Path}");
                if (content.Size < 0)
                    throw new UniversalPackagesException($"The manifest of {packageName} {version} has a negative size for {item.Path}");
                
                var id = ParseId(content.Id, item.Path);
                var blob = new FileBlob(path, id.ToString(), content.Size);
                return new FileManifestEntry(blob, id);
            default:
                throw new UniversalPackagesException(
                    $"The manifest of {packageName} {version} has an item of unknown type {item.Type}: {item.Path}");
        }
    }

    [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
    [SuppressMessage("ReSharper", "AccessToModifiedClosure")]
    private async Task DownloadEntriesAsync(
        IReadOnlyList<ManifestEntry> entries, 
        IPackageDownloader downloader,
        DownloadSettings settings, 
        CancellationToken ct)
    {
        // The chunks of every entry, in order: entry i has chunks [starts[i], starts[i + 1])
        var withContent = entries.OfType<FileManifestEntry>().Where(e => e.Size > 0).ToList();
        
        var expanded = await _blobs.GetChunksAsync(
                [.. withContent.Select(e => new DedupChild(e.Content, e.Size))],
                settings,
                ct)
            .ConfigureAwait(false);

        var chunks = new List<DedupChild>();
        var starts = new int[entries.Count + 1];
        var expandedIndex = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            starts[i] = chunks.Count;
            if (entries[i] is not FileManifestEntry f || f.Size == 0)
                continue;

            var entryChunks = expanded[expandedIndex++];
            _logger?.DownloadingFile(f.File.Path, f.Size, entryChunks.Count);
            chunks.AddRange(entryChunks);
        }
        starts[entries.Count] = chunks.Count;

        // Cancelled on failure, to end a callback waiting for content that won't come
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var current = BeforeFirstEntry;
        BlobContentStream? stream = null;
        Task? reader = null;
        try
        {
            await _blobs.DownloadInOrderAsync(
                    chunks, 
                    async (index, data) =>
                    {
                        // Move to the entry the chunk belongs to (handing over any entries without content on the way)
                        while (index >= starts[current + 1])
                            await NextEntryAsync().ConfigureAwait(false);

                        Debug.Assert(stream != null && reader != null, "An entry with content is being read");
                        await stream.FeedAsync(data, reader, abort.Token).ConfigureAwait(false);
                    }, 
                    settings,
                    abort.Token)
                .ConfigureAwait(false);

            while (current + 1 < entries.Count)
                await NextEntryAsync().ConfigureAwait(false);
            await FinishEntryAsync().ConfigureAwait(false);
        }
        catch
        {
            abort.Cancel();
            if (reader != null)
            {
                try
                {
                    await reader.ConfigureAwait(false);
                }
                catch
                {
                    // The download's failure is the one reported
                }
            }
            throw;
        }

        return;

        async Task NextEntryAsync()
        {
            await FinishEntryAsync().ConfigureAwait(false);
            current++;

            switch (entries[current])
            {
                case FileManifestEntry file:
                    var source = new BlobContentStream(file.Size);
                    stream = source;
                    reader = Task.Run(() => downloader.DownloadFileAsync(file.File, source, abort.Token), abort.Token);
                    break;
                case EmptyDirectoryManifestEntry directory:
                    reader = Task.Run(() => downloader.HandleEmptyDirectoryAsync(directory.Directory, abort.Token), abort.Token);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown manifest entry {entries[current].GetType().Name}");
            }
        }

        async Task FinishEntryAsync()
        {
            var finishing = reader;
            if (finishing == null)
                return;

            await finishing.ConfigureAwait(false);
            reader = null;
            stream = null;
        }
    }

    private async Task<T> GetJsonAsync<T>(
        Uri uri, 
        string accept, 
        JsonTypeInfo<T> typeInfo,
        string action, 
        Guid? sessionId, 
        CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Accept.ParseAdd(accept);
                    request.Headers.Add(HttpNames.FedAuthRedirectHeader, HttpNames.FedAuthRedirectSuppress);
                    request.AddSession(sessionId);
                    return request;
                }, 
                _credentialProvider, 
                uri.ToString(),
                _options, 
                _logger, 
                ct)
            .ConfigureAwait(false);
        await response.EnsureSuccessAsync(action).ConfigureAwait(false);
        return await response.ReadJsonAsync(typeInfo, ct).ConfigureAwait(false);
    }

    private static DedupIdentifier ParseId(string value, string what)
    {
        try
        {
            return DedupIdentifier.Parse(value);
        }
        catch (FormatException e)
        {
            throw new UniversalPackagesException($"The blob identifier of {what} is not valid: {e.Message}", innerException: e);
        }
    }

    private static void Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required", name);
    }

    /// <summary>Copies the one file selected to a stream, and remembers it</summary>
    private sealed class StreamDownloader : IPackageDownloader
    {
        private readonly Stream _output;
        private readonly int _bufferSize;

        public StreamDownloader(Stream output, int bufferSize)
        {
            _output = output;
            _bufferSize = bufferSize;
        }

        /// <summary>The file downloaded, or null when none was selected</summary>
        public FileBlob? Downloaded { get; private set; }

        public async Task DownloadFileAsync(FileBlob blob, Stream source, CancellationToken cancellationToken)
        {
            Downloaded = blob;
            await source.CopyToAsync(_output, _bufferSize, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Only files are selected
        public Task HandleEmptyDirectoryAsync(EmptyDirectoryBlob blob, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Writes files and creates empty directories under a root directory</summary>
    private sealed class DirectoryDownloader : IPackageDownloader
    {
        private readonly string _root;
        private readonly string _rootPrefix;
        private readonly int _bufferSize;

        /// <param name="root">The root directory, a full path</param>
        /// <param name="bufferSize">The buffer size of file writes</param>
        public DirectoryDownloader(string root, int bufferSize)
        {
            _root = root;
            _rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
            _bufferSize = bufferSize;
        }

        public async Task DownloadFileAsync(FileBlob blob, Stream source, CancellationToken cancellationToken)
        {
            var target = GetTarget(blob);
            var parent = Path.GetDirectoryName(target);
            Debug.Assert(parent != null, "A path under the root has a parent");
            Directory.CreateDirectory(parent);
#if NET6_0_OR_GREATER
            await using
#else
            using
#endif
                var file = new FileStream(target,
                    FileMode.Create, FileAccess.Write, FileShare.None, _bufferSize, useAsync: true);

            await source.CopyToAsync(file, _bufferSize, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task HandleEmptyDirectoryAsync(EmptyDirectoryBlob blob, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(GetTarget(blob));
            return Task.CompletedTask;
        }

        /// <summary>The item's full path, checked to be under the root (manifest paths are also checked when read)</summary>
        private string GetTarget(Blob blob)
        {
            var target = Path.GetFullPath(Path.Combine(_root, blob.Path.Replace(PathSeparator, Path.DirectorySeparatorChar)));
            if (!target.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new UniversalPackagesException($"Item {blob.Path} is outside the directory {_root}");
            return target;
        }
    }

    private record ManifestEntry(Blob Blob);

    /// <summary>
    /// An empty directory manifest item and the dedup blob of its content (null for an empty directory, which has no content)
    /// </summary>
    private sealed record EmptyDirectoryManifestEntry(EmptyDirectoryBlob Directory) : ManifestEntry(Directory);

    /// <summary>A file manifest item and the dedup blob of its content</summary>
    private sealed record FileManifestEntry(FileBlob File, DedupIdentifier Content) : ManifestEntry(File)
    {
        public long Size => File.Size;
    }
}
