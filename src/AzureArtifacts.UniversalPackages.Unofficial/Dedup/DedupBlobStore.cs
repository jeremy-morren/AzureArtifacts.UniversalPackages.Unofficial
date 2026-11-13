using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AzureArtifacts.UniversalPackages.Unofficial.Json;

namespace AzureArtifacts.UniversalPackages.Unofficial.Dedup;

/// <summary>
/// Reads blobs from the Azure DevOps dedup store: resolves identifiers to signed storage URLs (<c>dedup/urls</c>),
/// downloads them, decompresses them (<c>Content-Encoding: xpress</c>), and checks every blob against its identifier
/// </summary>
/// <remarks>
/// Signed storage URLs are fetched without credentials: storage rejects a signed URL whose request also carries an Azure
/// DevOps token
/// </remarks>
internal sealed class DedupBlobStore
{
    private const string UrlsPath = "_apis/dedup/urls?allowEdge=true";
    private const string ApiMediaType = "application/json; api-version=1.0-preview";

    private readonly HttpClient _httpClient;
    private readonly IAzureArtifactsCredentialProvider _credentials;
    private readonly AzureArtifactsLocation _location;
    private readonly UniversalPackagesClientOptions _options;
    private readonly ILogger? _logger;

    public DedupBlobStore(
        HttpClient httpClient, 
        IAzureArtifactsCredentialProvider credentials,
        AzureArtifactsLocation location, 
        UniversalPackagesClientOptions options, 
        ILogger? logger)
    {
        _httpClient = httpClient;
        _credentials = credentials;
        _location = location;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Downloads blobs, up to <see cref="DownloadSettings.Parallelism"/> at a time, and hands each one's content to
    /// <paramref name="consume"/> in order
    /// </summary>
    /// <param name="blobs">The blobs, with their content sizes (or <see cref="DedupChild.UnknownSize"/>)</param>
    /// <param name="consume">Receives the index and content of each blob, in order</param>
    /// <param name="settings">The parallelism and session</param>
    /// <param name="ct"></param>
    public async Task DownloadInOrderAsync(
        IReadOnlyList<DedupChild> blobs, 
        Func<int, byte[], Task> consume,
        DownloadSettings settings, 
        CancellationToken ct)
    {
        var urls = new Dictionary<DedupIdentifier, Uri>();
        var resolvedUpTo = 0;
        var pending = new Queue<Task<byte[]>>();

        try
        {
            var next = 0;
            var consumed = 0;
            while (consumed < blobs.Count)
            {
                while (pending.Count < settings.Parallelism && next < blobs.Count)
                {
                    // Resolve URLs shortly before use, a batch at a time (they expire)
                    if (next >= resolvedUpTo)
                    {
                        var batch = blobs
                            .Skip(next)
                            .Take(_options.UrlBatchSize)
                            .Select(c => c.Id)
                            .Distinct()
                            .ToList();
                        foreach (var pair in await ResolveAsync(batch, settings, ct).ConfigureAwait(false))
                            urls[pair.Key] = pair.Value;
                        resolvedUpTo = Math.Min(next + _options.UrlBatchSize, blobs.Count);
                    }

                    var blob = blobs[next++];
                    pending.Enqueue(DownloadAsync(
                        blob.Id,
                        urls[blob.Id],
                        blob.Size == DedupChild.UnknownSize ? null : blob.Size, 
                        settings, 
                        ct));
                }

                var data = await pending.Dequeue().ConfigureAwait(false);
                await consume(consumed++, data).ConfigureAwait(false);
            }
        }
        finally
        {
            // Don't leave downloads running (or their failures unobserved) when a download or the consumer fails
            foreach (var task in pending)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // The first failure is the one reported
                }
            }
        }
    }

    /// <summary>
    /// The chunks of each blob: the chunks whose content, concatenated, is the blob's content (the blob itself, for a
    /// chunk). Nodes are fetched a tree level at a time, every blob's nodes together
    /// </summary>
    /// <param name="blobs">The blobs, with their content sizes (checked), or <see cref="DedupChild.UnknownSize"/></param>
    /// <param name="settings">The parallelism and session</param>
    /// <param name="ct">Cancels the downloads</param>
    public async Task<IReadOnlyList<IReadOnlyList<DedupChild>>> GetChunksAsync(
        IReadOnlyList<DedupChild> blobs,
        DownloadSettings settings, 
        CancellationToken ct)
    {
        var nodes = new Dictionary<DedupIdentifier, IReadOnlyList<DedupChild>>();
        var level = blobs.Where(b => b.Id.IsNode).Select(b => b.Id).Distinct().ToList();
        while (level.Count > 0)
        {
            var current = level;
            var next = new HashSet<DedupIdentifier>();
            await DownloadInOrderAsync(
                    [.. current.Select(id => new DedupChild(id, DedupChild.UnknownSize))], 
                    (index, data) =>
                    {
                        var children = ParseNode(current[index], data);
                        nodes[current[index]] = children;
                        foreach (var child in children.Where(c => c.Id.IsNode))
                            next.Add(child.Id);
                        return Task.CompletedTask;
                    }, 
                    settings, 
                    ct)
                .ConfigureAwait(false);

            level = [.. next.Where(id => !nodes.ContainsKey(id))];
        }

        return [.. blobs.Select(blob => Flatten(blob, nodes))];
    }

    /// <summary>Downloads a small blob (a manifest) whole</summary>
    public async Task<byte[]> ReadAsync(DedupIdentifier id, DownloadSettings settings, CancellationToken ct)
    {
        var blob = new DedupChild(id, DedupChild.UnknownSize);
        var chunks = (await GetChunksAsync([blob], settings, ct).ConfigureAwait(false))[0];

        using var buffer = new MemoryStream();
        await DownloadInOrderAsync(chunks,
            (_, data) =>
            {
                // ReSharper disable once AccessToDisposedClosure
                buffer.Write(data, 0, data.Length);
                return Task.CompletedTask;
            }, 
            settings, 
            ct)
            .ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static IReadOnlyList<DedupChild> ParseNode(DedupIdentifier id, byte[] data)
    {
        try
        {
            return DedupNode.Parse(data);
        }
        catch (InvalidDataException e)
        {
            throw new UniversalPackagesException($"Node {id} is not valid: {e.Message}", innerException: e);
        }
    }

    /// <summary>A blob's chunks, walking its nodes depth-first</summary>
    private static IReadOnlyList<DedupChild> Flatten(DedupChild blob,
        IReadOnlyDictionary<DedupIdentifier, IReadOnlyList<DedupChild>> nodes)
    {
        if (blob.Id.IsChunk)
            return [blob];

        var chunks = new List<DedupChild>();
        AddChunks(blob.Id);

        var total = chunks.Sum(c => c.Size);
        if (blob.Size != DedupChild.UnknownSize && total != blob.Size)
            throw new UniversalPackagesException($"Node {blob.Id} has {total} bytes of content, expected {blob.Size}");
        return chunks;

        void AddChunks(DedupIdentifier node)
        {
            foreach (var child in nodes[node])
            {
                if (child.Id.IsChunk)
                    chunks.Add(child);
                else
                    AddChunks(child.Id);
            }
        }
    }

    private async Task<Uri> ResolveAsync(DedupIdentifier id, DownloadSettings settings, CancellationToken ct) =>
        (await ResolveAsync([id], settings, ct).ConfigureAwait(false))[id];

    /// <summary>Resolves identifiers to signed storage URLs</summary>
    private async Task<IReadOnlyDictionary<DedupIdentifier, Uri>> ResolveAsync(
        IReadOnlyList<DedupIdentifier> ids,
        DownloadSettings settings, 
        CancellationToken ct)
    {
        var uri = new Uri(_location.DedupUri, UrlsPath);
        var body = JsonSerializer.SerializeToUtf8Bytes(ids.Select(i => i.ToString()), ArtifactsJsonContext.Default.IEnumerableString);

        using var response = await _httpClient.SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, uri);
                    request.Headers.Accept.ParseAdd(ApiMediaType);
                    request.Headers.Add(HttpNames.FedAuthRedirectHeader, HttpNames.FedAuthRedirectSuppress);
                    request.AddSession(settings.SessionId);

                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ApiMediaType);
                    return request;
                },
                _credentials, 
                uri.ToString(), 
                _options, 
                _logger, 
                ct)
            .ConfigureAwait(false);
        
        await response.EnsureSuccessAsync("resolve blob URLs").ConfigureAwait(false);

        var urls = await response.ReadJsonAsync(ArtifactsJsonContext.Default.DictionaryStringString, ct).ConfigureAwait(false);

        var result = new Dictionary<DedupIdentifier, Uri>();
        foreach (var id in ids)
        {
            var url = urls.FirstOrDefault(u => id.Equals(u.Key)).Value
                      ?? throw new UniversalPackagesException($"Blob {id} was not found: the dedup store returned no URL for it");
            
            var resolved = new Uri(url);
            if (resolved.Host.EndsWith(HttpNames.AzureTableHostSuffix, StringComparison.OrdinalIgnoreCase))
                throw new NotImplementedException($"Blob {id} is in Azure Table storage, which is not supported");
            
            result[id] = resolved;
        }

        _logger?.ResolvedUrls(result.Count);
        return result;
    }

    /// <summary>
    /// Downloads a blob, decompresses it if needed, and checks it against its identifier. A URL that storage rejects
    /// as unauthorized (expired) is resolved again, once
    /// </summary>
    /// <param name="id">The blob</param>
    /// <param name="url">Its signed storage URL</param>
    /// <param name="size">The uncompressed size, when known</param>
    /// <param name="settings">The session</param>
    /// <param name="ct">Cancels the download</param>
    private async Task<byte[]> DownloadAsync(
        DedupIdentifier id, 
        Uri url, 
        long? size, 
        DownloadSettings settings,
        CancellationToken ct)
    {
        var (bytes, compressed) = 
            await GetAsync(id, url, settings, ct).ConfigureAwait(false)
            ?? await GetAsync(id, await ResolveAsync(id, settings, ct).ConfigureAwait(false), settings, ct).ConfigureAwait(false)
            ?? throw new UniversalPackagesException(
                $"Storage rejected the URL of blob {id}, also after resolving it again",
                HttpStatusCode.Forbidden);

        var content = Decode(id, bytes, compressed, size);
        if (size is { } expected && content.Length != expected)
            throw new UniversalPackagesException($"Blob {id} has {content.Length} bytes, expected {expected}");
        return content;
    }

    /// <returns>The body and whether it is compressed, or null when storage rejected the URL as unauthorized</returns>
    private async Task<(byte[] Bytes, bool Compressed)?> GetAsync(
        DedupIdentifier id, Uri url, DownloadSettings settings, CancellationToken ct)
    {
        // The signature is in the query: log without it
        using var response = await _httpClient.SendAsync(
                () => 
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.AddStorageSession(settings.SessionId);
                    return request;
                }, 
                null, 
                url.GetLeftPart(UriPartial.Path),
                _options, 
                _logger, 
                ct)
            .ConfigureAwait(false);

        switch (response.StatusCode)
        {
            // An expired signature
            case HttpStatusCode.Forbidden:
                _logger?.RefreshingUrl(id.ToString(), (int)response.StatusCode);
                return null;
            case HttpStatusCode.NotFound:
                throw new UniversalPackagesException($"Blob {id} was not found in storage", HttpStatusCode.NotFound);
        }
        await response.EnsureSuccessAsync($"download blob {id}").ConfigureAwait(false);

        var compressed = response.Content.Headers.ContentEncoding
            .Any(e => e.Equals(HttpNames.XpressEncoding, StringComparison.OrdinalIgnoreCase));
        var content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        
        return (content, compressed);
    }

    /// <summary>
    /// The blob's content, checked against its identifier. Storage marks compressed blobs with
    /// <c>Content-Encoding: xpress</c>; without the header, a body that doesn't match is tried as compressed
    /// </summary>
    private static byte[] Decode(DedupIdentifier id, byte[] bytes, bool compressed, long? size)
    {
        byte[] content;
        if (compressed)
        {
            content = Decompress(id, bytes, size);
        }
        else if (Matches(id, bytes))
        {
            return bytes;
        }
        else
        {
            try
            {
                content = XpressDecompressor.Decompress(bytes, size);
            }
            catch (InvalidDataException)
            {
                content = bytes;
            }
        }

        if (!Matches(id, content))
            throw new UniversalPackagesException($"Blob {id} doesn't match its identifier (hash {ContentHash.Compute(content)})");
        
        return content;
    }

    private static byte[] Decompress(DedupIdentifier id, byte[] bytes, long? size)
    {
        try
        {
            return XpressDecompressor.Decompress(bytes, size);
        }
        catch (InvalidDataException ex)
        {
            throw new UniversalPackagesException($"Blob {id} could not be decompressed: {ex.Message}", innerException: ex);
        }
    }

    private static bool Matches(DedupIdentifier id, byte[] content) =>
        string.Equals(ContentHash.Compute(content), id.Hash, StringComparison.Ordinal);
}

/// <summary>The settings of one operation's requests</summary>
/// <param name="Parallelism">The most blobs downloaded at once</param>
/// <param name="SessionId">The session the requests are sent with, or null for none</param>
internal sealed record DownloadSettings(int Parallelism, Guid? SessionId)
{
    /// <summary>For operations without download options (reading a manifest)</summary>
    public static DownloadSettings Default { get; } = new(UniversalPackageDownloadOptions.DefaultParallelism, null);
}
