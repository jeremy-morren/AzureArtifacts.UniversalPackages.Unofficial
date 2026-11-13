using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

// ReSharper disable MethodHasAsyncOverloadWithCancellation
// ReSharper disable AccessToDisposedClosure

namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

public class DownloadTests
{
    private const string Feed = "feed";
    private const string Package = "my-package";
    private const string Version = "1.0.0";

    /// <summary>
    /// Files covering each shape of content: one chunk, nested nodes (with the default chunk size and node width),
    /// empty, compressible and not
    /// </summary>
    private static KeyValuePair<string, byte[]>[] SampleFiles() =>
    [
        new("readme.txt", Encoding.UTF8.GetBytes("Hello, Universal Packages")),
        new("lib/data.bin", Random(10_000, 1)),
        new("lib/empty.txt", []),
        new("nested/deep/text.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible text ", 300)))),
        new("exact-chunk.bin", Random(1024, 2)),
        new("trailing-empty", [])
    ];

    /// <summary>What a download handed to its callback, in order</summary>
    private sealed record Received(Blob Blob, byte[] Content);

    [Theory]
    [InlineData(true, true, 1024)]
    [InlineData(true, false, 1024)] // compressed, without the Content-Encoding header
    [InlineData(false, false, 1024)]
    [InlineData(true, true, 64)] // tiny chunks: deep trees, and a manifest that is itself a node
    public async Task Download_HandsEachItemAndItsContentToTheCallback(bool compress, bool contentEncoding, int chunkSize)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps
        {
            CompressChunks = compress,
            SendContentEncoding = contentEncoding,
            ChunkSize = chunkSize,
            RandomStorageDelays = true
        };
        var files = SampleFiles();
        var version = fake.AddPackage(Feed, Package, Version, files);

        var (manifest, received) = await DownloadAsync(fake.CreateClient(), null, ct);

        manifest.Items.Select(i => i.Path).Should().Equal(files.Select(f => f.Key));
        AssertFiles(files, received);
        received.Should().AllSatisfy(r =>
        {
            var file = r.Blob.Should().BeOfType<FileBlob>().Subject;
            file.Id.Should().Be(version.Files[file.Path].Id);
            file.Size.Should().Be(r.Content.Length);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100_000)]
    public async Task Download_SourceCanBeReadInPiecesOfAnySize(int bufferSize)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { ChunkSize = 256, RandomStorageDelays = true };
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files);
        var received = new List<Received>();

        await fake.CreateClient().DownloadAsync(
            Feed, Package, Version, 
            new CallbackDownloader(async (blob, source, token) =>
            {
                using var content = new MemoryStream();
                var buffer = new byte[bufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    content.Write(buffer, 0, read);
                (await source.ReadAsync(buffer, 0, buffer.Length, token)).Should().Be(0, "the content has ended");
                received.Add(new Received(blob, content.ToArray()));
            }), 
            null, 
            ct);

        AssertFiles(files, received);
    }

    [Fact]
    public async Task Download_SourceCanBeReadSynchronously()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { ChunkSize = 256, RandomStorageDelays = true };
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files);
        var received = new List<Received>();

        await fake.CreateClient().DownloadAsync(
            Feed, Package, Version, 
            new CallbackDownloader((blob, source, _) =>
            {
                using var content = new MemoryStream();
                source.CopyTo(content);
                received.Add(new Received(blob, content.ToArray()));
                return Task.CompletedTask;
            }), 
            cancellationToken: ct);

        AssertFiles(files, received);
    }

    [Fact]
    public async Task Download_SourceIsForwardOnly()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());

        await fake.CreateClient().DownloadAsync(
            Feed, Package, Version,
            new CallbackDownloader(async (blob, source, token) =>
            {
                source.CanRead.Should().BeTrue();
                source.CanSeek.Should().BeFalse();
                source.CanWrite.Should().BeFalse();
                source.Length.Should().Be(blob.Size);
                source.Position.Should().Be(0);
                source.Invoking(s => s.Seek(0, SeekOrigin.Begin)).Should().ThrowExactly<NotSupportedException>();
                source.Invoking(s => s.Position = 0).Should().ThrowExactly<NotSupportedException>();
                source.Invoking(s => s.SetLength(0)).Should().ThrowExactly<NotSupportedException>();
                source.Invoking(s => s.Write([1], 0, 1)).Should().ThrowExactly<NotSupportedException>();
                source.Invoking(s => s.Flush()).Should().NotThrow();
                await source.Awaiting(s => s.FlushAsync(token)).Should().NotThrowAsync();
                await source.CopyToAsync(Stream.Null, 81920, token);
                source.Position.Should().Be(blob.Size);
            }), 
            cancellationToken: ct);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(16, 1000)]
    [InlineData(UniversalPackageDownloadOptions.DefaultParallelism, UniversalPackagesClientOptions.DefaultUrlBatchSize)]
    public async Task Download_AnyParallelismAndBatchSize(int parallelism, int batchSize)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { ChunkSize = 256, RandomStorageDelays = true };
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files);
        var client = fake.CreateClient(new UniversalPackagesClientOptions { UrlBatchSize = batchSize });

        var (_, received) = await DownloadAsync(client,
            new UniversalPackageDownloadOptions { Parallelism = parallelism }, 
            ct);

        AssertFiles(files, received);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Download_ParallelismBelowOne_Throws(int parallelism)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());

        var act = () => DownloadAsync(fake.CreateClient(),
            new UniversalPackageDownloadOptions { Parallelism = parallelism }, ct);

        await act.Should().ThrowExactlyAsync<ArgumentOutOfRangeException>();
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Download_ResolvesUrlsInBatches()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { ChunkSize = 1 << 20 }; // the manifest is one chunk
        var files = Enumerable.Range(0, 250).Select(i => new KeyValuePair<string, byte[]>($"file-{i}.txt", Random(10, i))).ToArray();
        fake.AddPackage(Feed, "many-files", Version, files);
        var client = fake.CreateClient(new UniversalPackagesClientOptions { UrlBatchSize = 100 });

        var received = new List<Received>();
        await client.DownloadAsync(Feed, "many-files", Version, Collect(received), cancellationToken: ct);

        AssertFiles(files, received);
        // The manifest, then 250 single-chunk files in batches of 100
        fake.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/dedup/urls")).Should().Be(1 + 3);
    }

    [Fact]
    public async Task Download_ResolvesEachLevelOfNodesTogether()
    {
        var ct = Token();
        // Two files of 64 chunks under 4-wide nodes: 3 levels of nodes each
        using var fake = new FakeAzureDevOps { ChunkSize = 16, MaxNodeChildren = 4 };
        KeyValuePair<string, byte[]>[] files = [new("a.bin", Random(1024, 1)), new("b.bin", Random(1024, 2))];
        var version = fake.AddPackage(Feed, Package, Version, files);
        var client = fake.CreateClient(new UniversalPackagesClientOptions { UrlBatchSize = 1000 });

        var (_, received) = await DownloadAsync(client, null, ct);

        AssertFiles(files, received);
        NodeLevels(fake.Blobs, version.Files["a.bin"].Id).Should().Be(3);
        // The manifest (each level of its nodes, then its chunks), then each level of both files' nodes, then all
        // their chunks
        fake.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/dedup/urls"))
            .Should().Be(NodeLevels(fake.Blobs, version.ManifestId) + 1 + 3 + 1);
    }

    [Fact]
    public async Task Download_Filter_DownloadsOnlyTheItemsItSelects()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var files = SampleFiles();
        var version = fake.AddPackage(Feed, Package, Version, files, ["empty-dir"]);
        var options = new UniversalPackageDownloadOptions { DownloadFilter = blob => blob.Path.StartsWith("lib/") };

        var (manifest, received) = await DownloadAsync(fake.CreateClient(), options, ct);

        manifest.Items.Should().HaveCount(files.Length + 1);
        AssertFiles(files.Where(f => f.Key.StartsWith("lib/")), received);
        var downloaded = fake.Requests.Where(r => r.Uri.Host == FakeAzureDevOps.StorageHost)
            .Select(r => r.Uri.AbsolutePath.Substring("/blobs/".Length)).ToList();
        downloaded.Should().NotContain(id => id.Equals(version.Files["readme.txt"].Id, StringComparison.OrdinalIgnoreCase))
            .And.NotContain(id => id.Equals(version.Files["exact-chunk.bin"].Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Download_EmptyDirectories_AreHandedOverWithNoContent()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files, ["empty-dir", "nested/empty"]);

        var (manifest, received) = await DownloadAsync(fake.CreateClient(), null, ct);

        received.Select(r => r.Blob.Path).Should().Equal(
            files.Select(f => f.Key).Concat(["empty-dir", "nested/empty"]));
        received.Select(r => r.Blob).Should().Equal(manifest.Items);
        foreach (var directory in received.Skip(files.Length))
        {
            directory.Blob.Should().Be(new EmptyDirectoryBlob(directory.Blob.Path));
            directory.Content.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Download_ContentLeftUnread_IsDiscarded()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { ChunkSize = 256 };
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files);
        var received = new List<Received>();

        await fake.CreateClient().DownloadAsync(
            Feed, Package, Version, 
            new CallbackDownloader(async (blob, source, token) =>
            {
                // Read only the first byte of the large file
                if (blob.Path == "lib/data.bin")
                {
                    var first = new byte[1];
                    (await source.ReadAsync(first, 0, 1, token)).Should().Be(1);
                    received.Add(new Received(blob, first));
                    return;
                }
                using var content = new MemoryStream();
                await source.CopyToAsync(content, 81920, token);
                received.Add(new Received(blob, content.ToArray()));
            }), 
            cancellationToken: ct);

        received.Select(r => r.Blob.Path).Should().Equal(files.Select(f => f.Key));
        AssertFiles(
            files.Where(f => f.Key != "lib/data.bin"),
            received.Where(r => r.Blob.Path != "lib/data.bin"));
        received.Should().ContainSingle(r => r.Blob.Path == "lib/data.bin")
            .Which.Content.Should().Equal(files.Single(f => f.Key == "lib/data.bin").Value.Take(1));
    }

    [Fact]
    public async Task Download_CallbackFailure_FailsTheDownload()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { ChunkSize = 256 };
        fake.AddPackage(Feed, Package, Version, SampleFiles());
        var handed = new List<string>();

        var act = () => fake.CreateClient().DownloadAsync(
            Feed, Package, Version, 
            new CallbackDownloader(async (blob, source, token) =>
            {
                handed.Add(blob.Path);
                var buffer = new byte[10];
                _ = await source.ReadAsync(buffer, 0, buffer.Length, token);
                if (blob.Path == "lib/data.bin")
                    throw new InvalidOperationException("callback failed");
                await source.CopyToAsync(Stream.Null, 81920, token);
            }), 
            cancellationToken: ct);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("callback failed");
        handed.Should().Equal("readme.txt", "lib/data.bin");
    }

    [Fact]
    public async Task Download_SessionId_IsSentToAzureDevOpsAndStorage()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());
        var session = Guid.NewGuid();

        await DownloadAsync(fake.CreateClient(), new UniversalPackageDownloadOptions { SessionId = session }, ct);

        var storage = fake.Requests.Where(r => r.Uri.Host == FakeAzureDevOps.StorageHost).ToList();
        storage.Should().NotBeEmpty()
            .And.AllSatisfy(r =>
            {
                r.ClientRequestId.Should().Be("X_TFS_Session: " + session.ToString("D"));
                r.Session.Should().BeNull();
            });
        fake.Requests.Except(storage)
            .Should().AllSatisfy(r =>
            {
                r.Session.Should().Be(session.ToString("D"));
                r.ClientRequestId.Should().BeNull();
            });
    }

    [Fact]
    public async Task Download_WithoutSessionId_SendsNoSessionHeaders()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());

        await DownloadAsync(fake.CreateClient(), null, ct);

        fake.Requests.Should().AllSatisfy(r =>
        {
            r.Session.Should().BeNull();
            r.ClientRequestId.Should().BeNull();
        });
    }

    [Fact]
    public async Task Download_SendsTheLibraryUserAgent()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());

        await DownloadAsync(fake.CreateClient(), null, ct);

        var version = typeof(UniversalPackagesClient).Assembly.GetName().Version!.ToString(3);
        fake.Requests.Should().Contain(r => r.Uri.Host == FakeAzureDevOps.StorageHost)
            .And.OnlyContain(r => r.UserAgent == "AzureArtifacts.UniversalPackages.Unofficial/" + version);
    }

    [Fact]
    public async Task Download_KeepsTheHttpClientsUserAgent()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());
        var http = fake.CreateHttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MyApp/2.0");
        var client = new UniversalPackagesClient(http, fake.Location, AzureArtifactsCredentials.BearerToken(FakeAzureDevOps.Token));

        await DownloadAsync(client, null, ct);

        fake.Requests.Should().NotBeEmpty()
            .And.OnlyContain(r => r.UserAgent == "MyApp/2.0");
    }

    [Fact]
    public async Task Download_RetriesTransientFailures()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files);
        foreach (var host in new[] { "pkgs.dev.azure.com", "vsblob.dev.azure.com", FakeAzureDevOps.StorageHost })
            fake.FailFirst[host] = 3;
        var client = fake.CreateClient(new UniversalPackagesClientOptions { RetryDelay = TimeSpan.FromMilliseconds(1) });

        var (_, received) = await DownloadAsync(client, null, ct);

        AssertFiles(files, received);
        fake.FailFirst.Values.Should().AllBeEquivalentTo(0, "every failure was retried");
    }

    [Fact]
    public async Task Download_GivesUpAfterMaxRetries()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());
        fake.FailFirst["pkgs.dev.azure.com"] = 10;
        var client = fake.CreateClient(new UniversalPackagesClientOptions
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.FromMilliseconds(1)
        });

        var act = () => DownloadAsync(client, null, ct);

        (await act.Should().ThrowExactlyAsync<UniversalPackagesException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.Requests.Count(r => r.Uri.Host == "pkgs.dev.azure.com").Should().Be(3);
    }

    [Fact]
    public async Task Download_SendsNoCredentialsToStorage()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles());

        await DownloadAsync(fake.CreateClient(), null, ct);

        var storage = fake.Requests.Where(r => r.Uri.Host == FakeAzureDevOps.StorageHost).ToList();
        storage.Should().NotBeEmpty()
            .And.OnlyContain(r => r.Authorization == null);
        fake.Requests.Except(storage)
            .Should().AllSatisfy(r =>
            {
                r.Authorization.Should().Be("Bearer " + FakeAzureDevOps.Token);
                r.SuppressesRedirect.Should().BeTrue();
            });
    }

    [Fact]
    public async Task Download_ExpiredUrl_IsResolvedAgain()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files);
        // Every blob that is downloaded (empty files are not)
        foreach (var blob in fake.Blobs.All.Where(b => b.Value.Length > 0))
            fake.ExpireFirstUrl.Add(blob.Key);

        var (_, received) = await DownloadAsync(fake.CreateClient(), null, ct);

        AssertFiles(files, received);
        fake.ExpireFirstUrl.Should().BeEmpty("every expired URL was resolved again");
    }

    [Fact]
    public async Task Download_RejectedUrl_Throws()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var version = fake.AddPackage(Feed, Package, Version, SampleFiles());
        fake.RejectUrls.Add(version.Files["readme.txt"].Id);

        var act = () => DownloadAsync(fake.CreateClient(), null, ct);

        (await act.Should().ThrowExactlyAsync<UniversalPackagesException>()
                .WithMessage($"*{version.Files["readme.txt"].Id}*"))
            .Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Download_BlobMissingFromStorage_ThrowsNotFoundWithoutResolvingAgain()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var version = fake.AddPackage(Feed, Package, Version, SampleFiles());
        var missing = version.Files["readme.txt"].Id;
        fake.MissingFromStorage.Add(missing);

        var act = () => DownloadAsync(fake.CreateClient(), null, ct);

        (await act.Should().ThrowExactlyAsync<UniversalPackagesException>().WithMessage($"*{missing}*"))
            .Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fake.Requests.Should()
            .ContainSingle(r => 
                    r.Uri.Host == FakeAzureDevOps.StorageHost &&
                    r.Uri.AbsolutePath.EndsWith(missing, StringComparison.OrdinalIgnoreCase), 
                "a missing blob is not resolved again");
    }

    [Fact]
    public async Task Download_BlobInAzureTableStorage_IsNotSupported()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var version = fake.AddPackage(Feed, Package, Version, SampleFiles());
        fake.InTableStorage.Add(version.Files["readme.txt"].Id);

        var act = () => DownloadAsync(fake.CreateClient(), null, ct);

        await act.Should().ThrowExactlyAsync<NotImplementedException>();
        fake.Requests.Should().NotContain(r => r.Uri.Host == FakeAzureDevOps.TableStorageHost);
    }

    [Theory]
    [InlineData("readme.txt")] // a chunk
    [InlineData("lib/data.bin")] // under nodes
    public async Task Download_CorruptContent_Throws(string path)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var version = fake.AddPackage(Feed, Package, Version, SampleFiles());

        // Corrupt the file's first chunk (keeping its size)
        var blob = version.Files[path].Id;
        while (blob.EndsWith("02", StringComparison.Ordinal))
        {
            var node = fake.Blobs.All[blob];
            var isChunk = node[4] == 0;
            blob = FakeBlobs.ToHex(node.Skip(4 + 1 + (isChunk ? 3 : 7)).ToArray(), 32) + (isChunk ? "01" : "02");
        }
        var corrupt = fake.Blobs.All[blob].ToArray();
        corrupt[0] ^= 0xFF;
        fake.Blobs.Replace(blob, corrupt);

        var act = () => DownloadAsync(fake.CreateClient(), null, ct);

        await act.Should().ThrowExactlyAsync<UniversalPackagesException>().WithMessage("*doesn't match its identifier*");
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("/lib/data.bin")]
    [InlineData("lib\\empty.txt")]
    public async Task DownloadFile(string path)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { RandomStorageDelays = true };
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files);
        using var output = new MemoryStream();

        var file = await fake.CreateClient().DownloadFileAsync(Feed, Package, Version, path, output, cancellationToken: ct);

        var expected = files.Single(f => f.Key == file.Path).Value;
        output.ToArray().Should().Equal(expected);
        file.Size.Should().Be(expected.Length);
        output.CanWrite.Should().BeTrue("the output is left open");
    }

    [Theory]
    [InlineData("nope.txt")]
    [InlineData("empty-dir")] // not a file
    public async Task DownloadFile_Missing_Throws(string path)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles(), ["empty-dir"]);

        var act = () =>
            fake.CreateClient().DownloadFileAsync(Feed, Package, Version, path, new MemoryStream(), cancellationToken: ct);

        await act.Should().ThrowExactlyAsync<UniversalPackagesException>();
    }

    [Fact]
    public async Task DownloadToDirectory()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps { RandomStorageDelays = true };
        var files = SampleFiles();
        fake.AddPackage(Feed, Package, Version, files, ["empty-dir", "nested/empty"]);
        var directory = Path.Combine(Path.GetTempPath(), "upack-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "lib"));
            File.WriteAllText(Path.Combine(directory, "readme.txt"), "an older, longer readme that is overwritten");
            File.WriteAllText(Path.Combine(directory, "lib", "other.txt"), "left alone");

            var manifest = await fake.CreateClient().DownloadToDirectoryAsync(
                Feed, Package, Version, directory, cancellationToken: ct);

            manifest.Items.Should().HaveCount(files.Length + 2);
            foreach (var file in files)
                File.ReadAllBytes(Path.Combine(directory, file.Key)).Should().Equal(file.Value, file.Key);
            Directory.Exists(Path.Combine(directory, "empty-dir")).Should().BeTrue();
            Directory.Exists(Path.Combine(directory, "nested", "empty")).Should().BeTrue();
            File.ReadAllText(Path.Combine(directory, "lib", "other.txt")).Should().Be("left alone");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadToDirectory_Filter()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage(Feed, Package, Version, SampleFiles(), ["empty-dir"]);
        var directory = Path.Combine(Path.GetTempPath(), "upack-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fake.CreateClient().DownloadToDirectoryAsync(Feed, Package, Version, directory,
                new UniversalPackageDownloadOptions { DownloadFilter = blob => blob.Path == "readme.txt" }, ct);

            Directory.GetFileSystemEntries(directory).Should().Equal(Path.Combine(directory, "readme.txt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Downloads <see cref="Package"/>, collecting what the callback is handed</summary>
    private static async Task<(UniversalPackageManifest Manifest, List<Received> Received)> DownloadAsync(
        UniversalPackagesClient client, UniversalPackageDownloadOptions? options, CancellationToken cancellationToken)
    {
        var received = new List<Received>();
        var manifest = await client.DownloadAsync(Feed, Package, Version, Collect(received), options, cancellationToken);
        return (manifest, received);
    }

    /// <summary>A downloader that reads each file's content whole, and records empty directories with no content</summary>
    private static IPackageDownloader Collect(List<Received> received)
    {
        var inProgress = new ConcurrentDictionary<string, bool>();
        return new CallbackDownloader(
            async (blob, source, token) =>
            {
                inProgress.TryAdd(blob.Path, true).Should().BeTrue("one item is handed over at a time");
                inProgress.Should().ContainSingle();
                using var content = new MemoryStream();
                await source.CopyToAsync(content, 81920, token);
                received.Add(new Received(blob, content.ToArray()));
                inProgress.TryRemove(blob.Path, out _);
            },
            (blob, _) =>
            {
                inProgress.Should().BeEmpty();
                received.Add(new Received(blob, []));
                return Task.CompletedTask;
            });
    }

    private static void AssertFiles(IEnumerable<KeyValuePair<string, byte[]>> files, IEnumerable<Received> received)
    {
        files = files.ToList();
        received = received.ToList();
        
        received.Select(r => r.Blob.Path).Should().Equal(files.Select(f => f.Key));
        foreach (var (file, item) in files.Zip(received, (f, r) => (f, r)))
            item.Content.Should().Equal(file.Value, file.Key);
    }

    /// <summary>The levels of nodes above a blob's chunks (0 for a chunk), in the fake's node format</summary>
    private static int NodeLevels(FakeBlobs blobs, string id)
    {
        if (id.EndsWith("01", StringComparison.Ordinal))
            return 0;

        var node = blobs.All[id];
        var levels = 0;
        for (var offset = 4; offset < node.Length;)
        {
            var isNode = node[offset] == 1;
            offset += 1 + (isNode ? 7 : 3);
            var child = FakeBlobs.ToHex(node.Skip(offset).ToArray(), 32) + (isNode ? "02" : "01");
            offset += 32;
            levels = Math.Max(levels, NodeLevels(blobs, child));
        }
        return levels + 1;
    }

    internal static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
