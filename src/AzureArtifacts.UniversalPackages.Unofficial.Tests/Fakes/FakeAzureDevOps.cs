using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

/// <summary>A request the fake received</summary>
/// <param name="Session">The <c>X-TFS-Session</c> header</param>
/// <param name="ClientRequestId">The <c>x-ms-client-request-id</c> header</param>
/// <param name="UserAgent">The <c>User-Agent</c> header (with the client's defaults)</param>
internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, bool SuppressesRedirect,
    string? Session, string? ClientRequestId, string? UserAgent);

internal sealed class FakeFeed
{
    public FakeFeed(string name)
    {
        Name = name;
    }

    public string Id { get; } = Guid.NewGuid().ToString();
    public string Name { get; }
    public List<FakePackage> Packages { get; } = [];
}

internal sealed class FakePackage
{
    public FakePackage(string name)
    {
        Name = name;
    }

    public string Id { get; } = Guid.NewGuid().ToString();
    public string Name { get; }

    /// <summary>Newest first</summary>
    public List<FakeVersion> Versions { get; } = [];
}

/// <param name="Files">The blob of each file, by path</param>
internal sealed record FakeVersion(string Version, string ManifestId, long Size, DateTimeOffset PublishDate,
    IReadOnlyDictionary<string, FakeBlob> Files);

/// <summary>
/// Azure DevOps (feeds, packaging and dedup services) and blob storage, as one message handler: serves the responses
/// the real services do, and checks requests the way they do (auth on the API, none on storage)
/// </summary>
internal sealed class FakeAzureDevOps : HttpMessageHandler
{
    public const string Organization = "contoso";
    public const string Token = "test-token";
    public const string StorageHost = "storage.test";
    public const string TableStorageHost = "account.table.core.windows.net";

    private readonly ConcurrentDictionary<string, bool> _expiredSignatures = new();
    private readonly Random _random = new(42);
    private int _signatures;

    /// <param name="project">The project feeds are scoped to, or null for the organization</param>
    public FakeAzureDevOps(string? project = null)
    {
        Project = project;
    }

    public string? Project { get; }
    public FakeBlobs Blobs { get; } = new();
    public List<FakeFeed> Feeds { get; } = [];

    /// <summary>The chunk size content is split into</summary>
    public int ChunkSize { get; set; } = 1024;

    /// <summary>The most children of a node (small, so that large files have nested nodes)</summary>
    public int MaxNodeChildren { get; set; } = 4;

    /// <summary>Whether storage serves chunks compressed (when that makes them smaller)</summary>
    public bool CompressChunks { get; set; } = true;

    /// <summary>Whether storage marks compressed chunks with <c>Content-Encoding: xpress</c></summary>
    public bool SendContentEncoding { get; set; } = true;

    /// <summary>Whether storage responses are delayed at random (so they complete out of order)</summary>
    public bool RandomStorageDelays { get; set; }

    /// <summary>Blobs whose first storage URL is rejected (expired)</summary>
    public HashSet<string> ExpireFirstUrl { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Blobs whose storage URLs are always rejected</summary>
    public HashSet<string> RejectUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Blobs the dedup store resolves, but storage doesn't have (404)</summary>
    public HashSet<string> MissingFromStorage { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Blobs the dedup store resolves to Azure Table storage URLs</summary>
    public HashSet<string> InTableStorage { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>
    /// How many requests to each host fail transiently before it responds (503, or 429 with <c>Retry-After</c> from
    /// storage)
    /// </summary>
    public Dictionary<string, int> FailFirst { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AzureArtifactsLocation Location => new(Organization, Project);

    public HttpClient CreateHttpClient() => new(this, disposeHandler: false);

    /// <param name="credentials">The credentials, or null for <see cref="Token"/></param>
    public UniversalPackagesClient CreateClient(UniversalPackagesClientOptions? options = null,
        IAzureArtifactsCredentialProvider? credentials = null) =>
        new(CreateHttpClient(), Location, credentials ?? AzureArtifactsCredentials.BearerToken(Token),
            options);

    public FakeFeed Feed(string name)
    {
        var feed = Feeds.FirstOrDefault(f => f.Name == name);
        if (feed == null)
            Feeds.Add(feed = new FakeFeed(name));
        return feed;
    }

    /// <summary>
    /// Publishes a package version (the newest), storing its files and manifest (files, then empty directories, as
    /// ArtifactTool lists them)
    /// </summary>
    public FakeVersion AddPackage(string feedName, string packageName, string version,
        IEnumerable<KeyValuePair<string, byte[]>> files, IEnumerable<string>? emptyDirectories = null)
    {
        var blobs = files.Select(f => new KeyValuePair<string, FakeBlob>(f.Key,
            Blobs.AddContent(f.Value, ChunkSize, MaxNodeChildren))).ToList();

        var items = blobs.Select(b => (object)new { path = "/" + b.Key, blob = new { id = b.Value.Id, size = b.Value.Size } })
            .Concat((emptyDirectories ?? []).Select(d => (object)new { path = "/" + d, type = "EmptyDirectory" }))
            .ToList();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { manifestFormat = "1.1.0", items });
        var manifestBlob = Blobs.AddContent(manifest, ChunkSize, MaxNodeChildren);

        var feed = Feed(feedName);
        var package = feed.Packages.FirstOrDefault(p => p.Name == packageName);
        if (package == null)
            feed.Packages.Add(package = new FakePackage(packageName));

        var result = new FakeVersion(version, manifestBlob.Id, blobs.Sum(b => b.Value.Size),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(package.Versions.Count),
            blobs.ToDictionary(b => b.Key, b => b.Value));
        package.Versions.Insert(0, result);
        return result;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var authorization = request.Headers.Authorization?.ToString();
        Requests.Enqueue(new RecordedRequest(request.Method.Method, uri, authorization,
            request.Headers.Contains("X-TFS-FedAuthRedirect"), Header(request, "X-TFS-Session"),
            Header(request, "x-ms-client-request-id"),
            request.Headers.UserAgent.Count == 0 ? null : request.Headers.UserAgent.ToString()));

        lock (FailFirst)
        {
            if (FailFirst.TryGetValue(uri.Host, out var failures) && failures > 0)
            {
                FailFirst[uri.Host] = failures - 1;
                if (uri.Host != StorageHost)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };

                var throttled = new HttpResponseMessage((HttpStatusCode)429) { RequestMessage = request };
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return throttled;
            }
        }

        var response = uri.Host == StorageHost
            ? await StorageAsync(uri, authorization, cancellationToken)
            : authorization != "Bearer " + Token
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : await ApiAsync(request, cancellationToken);

        response.RequestMessage = request;
        return response;
    }

    private async Task<HttpResponseMessage> ApiAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var segments = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToList();
        var query = ParseQuery(uri.Query);

        if (uri.Host == "vsblob.dev.azure.com")
        {
            // Organization-scoped only
            return request.Method == HttpMethod.Post && Matches(segments, Organization, "_apis", "dedup", "urls")
                ? await ResolveUrlsAsync(request, cancellationToken)
                : NotFound("No such dedup API");
        }

        // Feeds and packaging are organization- or project-scoped
        var scope = Project == null ? new[] { Organization } : new[] { Organization, Project };
        if (!segments.Take(scope.Length).SequenceEqual(scope))
            return NotFound("No such organization or project");
        var path = segments.Skip(scope.Length).ToList();

        switch (uri.Host)
        {
            case "feeds.dev.azure.com" when query.TryGetValue("api-version", out var v) && v == "7.1":
                if (Matches(path, "_apis", "packaging", "feeds"))
                    return Json(new { count = Feeds.Count, value = Feeds.Select(f => new { id = f.Id, name = f.Name }) });
                if (path.Count == 5 && Matches(path.Take(3).ToList(), "_apis", "packaging", "feeds") && path[4] == "packages")
                    return ListPackages(path[3], query);
                break;

            case "pkgs.dev.azure.com":
                if (path.Count == 7 && path[0] == "_packaging" && path[2] == "upack" && path[3] == "packages" &&
                    path[5] == "versions")
                {
                    if (!query.TryGetValue("intent", out var intent) || intent != "Download" ||
                        !request.Headers.Accept.ToString().Contains("api-version=7.1-preview.1"))
                        return new HttpResponseMessage(HttpStatusCode.BadRequest);
                    return GetVersion(path[1], path[4], path[6]);
                }
                break;
        }

        return NotFound("No such API");
    }

    private HttpResponseMessage ListPackages(string feedName, Dictionary<string, string> query)
    {
        var feed = FindFeed(feedName);
        if (feed == null)
            return NotFound($"Feed {feedName} not found");
        if (!query.TryGetValue("protocolType", out var protocol) || protocol != "upack")
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        var allVersions = query.TryGetValue("includeAllVersions", out var all) && all == "true";
        var nameQuery = query.TryGetValue("packageNameQuery", out var q) ? q : "";
        var top = query.TryGetValue("$top", out var t) ? int.Parse(t) : 1000;
        var skip = query.TryGetValue("$skip", out var s) ? int.Parse(s) : 0;

        var page = feed.Packages
            .Where(p => p.Name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Skip(skip)
            .Take(top)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                normalizedName = p.Name.ToLowerInvariant(),
                protocolType = "UPack",
                versions = (allVersions ? p.Versions : p.Versions.Take(1)).Select(v => new
                {
                    id = Guid.NewGuid().ToString(),
                    version = v.Version,
                    normalizedVersion = v.Version,
                    isLatest = ReferenceEquals(v, p.Versions[0]),
                    isListed = true,
                    publishDate = v.PublishDate,
                    storageId = v.ManifestId
                })
            })
            .ToList();
        return Json(new { count = page.Count, value = page });
    }

    private HttpResponseMessage GetVersion(string feedName, string packageName, string version)
    {
        var package = FindFeed(feedName)?.Packages.FirstOrDefault(p => p.Name == packageName);
        var match = package?.Versions.FirstOrDefault(v => v.Version == version);
        if (match == null)
            return NotFound($"Can't find the package '{packageName}' with version '{version}' in feed '{feedName}'");

        return Json(new
        {
            version = match.Version,
            superRootId = new string('A', 64) + "02",
            manifestId = match.ManifestId,
            description = "",
            packageSize = match.Size
        });
    }

    private async Task<HttpResponseMessage> ResolveUrlsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content?.Headers.ContentType?.ToString().Contains("api-version=1.0") != true)
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        var ids = JsonSerializer.Deserialize<string[]>(await request.Content.ReadAsStringAsync())!;
        var urls = new Dictionary<string, string>();
        foreach (var id in ids.Where(Blobs.All.ContainsKey))
        {
            var signature = Interlocked.Increment(ref _signatures).ToString();
            lock (ExpireFirstUrl)
            {
                if (ExpireFirstUrl.Remove(id))
                    _expiredSignatures[signature] = true;
            }
            var host = InTableStorage.Contains(id) ? TableStorageHost : StorageHost;
            urls[id.ToLowerInvariant()] = $"https://{host}/blobs/{id.ToLowerInvariant()}?sv=2020&sig={signature}";
        }
        return Json(urls);
    }

    private async Task<HttpResponseMessage> StorageAsync(Uri uri, string? authorization, CancellationToken cancellationToken)
    {
        if (RandomStorageDelays)
        {
            int delay;
            lock (_random)
                delay = _random.Next(0, 8);
            await Task.Delay(delay, cancellationToken);
        }

        // As Azure Storage does: a signed URL with an Authorization header fails
        if (authorization != null)
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<Error><Code>AuthenticationFailed</Code></Error>")
            };

        var id = uri.AbsolutePath.Substring("/blobs/".Length);
        var signature = ParseQuery(uri.Query)["sig"];
        if (RejectUrls.Contains(id) || _expiredSignatures.ContainsKey(signature))
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<Error><Code>AuthenticationFailed</Code><Message>Signed expiry time has passed</Message></Error>")
            };

        if (MissingFromStorage.Contains(id) || !Blobs.All.TryGetValue(id, out var content))
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("<Error><Code>BlobNotFound</Code></Error>")
            };

        // Chunks are served compressed when that helps; nodes as they are
        var body = content;
        var compressed = false;
        if (CompressChunks && id.EndsWith("01", StringComparison.Ordinal))
        {
            var candidate = TestXpressCompressor.Compress(content);
            if (candidate.Length < content.Length)
            {
                body = candidate;
                compressed = true;
            }
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (compressed && SendContentEncoding)
            response.Content.Headers.ContentEncoding.Add("xpress");
        return response;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private FakeFeed? FindFeed(string nameOrId) =>
        Feeds.FirstOrDefault(f => string.Equals(f.Name, nameOrId, StringComparison.OrdinalIgnoreCase) || f.Id == nameOrId);

    private static bool Matches(IReadOnlyList<string> segments, params string[] expected) =>
        segments.SequenceEqual(expected);

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(new[] { '=' }, 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage NotFound(string message) => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json")
    };
}
