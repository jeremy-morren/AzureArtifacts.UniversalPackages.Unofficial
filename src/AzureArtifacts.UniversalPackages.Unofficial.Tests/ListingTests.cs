using System.Net;
using AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

public class ListingTests
{
    private static readonly KeyValuePair<string, byte[]>[] OneFile = [new("a.txt", [1, 2, 3])];

    [Theory]
    [InlineData(null)]
    [InlineData("My Project")]
    public async Task ListFeeds(string? project)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps(project);
        fake.Feed("first");
        fake.Feed("second");

        var feeds = await fake.CreateClient().ListFeedsAsync(ct);

        feeds.Select(f => f.Name).Should().Equal("first", "second");
        feeds.Select(f => f.Id).Should().Equal(fake.Feeds.Select(f => f.Id));

        var request = fake.Requests.Should().ContainSingle().Which;
        var expected = project == null
            ? "https://feeds.dev.azure.com/contoso/_apis/packaging/feeds?api-version=7.1"
            : "https://feeds.dev.azure.com/contoso/My%20Project/_apis/packaging/feeds?api-version=7.1";
        request.Uri.AbsoluteUri.Should().Be(expected);
        request.SuppressesRedirect.Should().BeTrue();
    }

    [Fact]
    public async Task ListPackages_ReadsEveryPage()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        for (var i = 0; i < 450; i++)
            fake.AddPackage("feed", $"package-{i:D3}", "1.0.0", OneFile);

        var packages = await fake.CreateClient().ListPackagesAsync("feed", cancellationToken: ct);

        packages.Select(p => p.Name).Should().Equal(Enumerable.Range(0, 450).Select(i => $"package-{i:D3}"));
        fake.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/packages")).Should().Be(3, "450 packages are 3 pages");
    }

    [Fact]
    public async Task ListPackages_LatestOrAllVersions()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage("feed", "tool", "1.0.0", OneFile);
        fake.AddPackage("feed", "tool", "1.1.0", OneFile);
        var client = fake.CreateClient();

        var latest = (await client.ListPackagesAsync("feed", cancellationToken: ct)).Should().ContainSingle().Which;
        latest.Versions.Should().ContainSingle().Which.Should().Be(
            new UniversalPackageVersion("1.1.0", IsLatest: true, IsListed: true,
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        var all = (await client.ListPackagesAsync("feed", includeAllVersions: true, cancellationToken: ct))
            .Should().ContainSingle().Which;
        all.Versions.Select(v => v.Version).Should().Equal("1.1.0", "1.0.0");
        all.Versions.Select(v => v.IsLatest).Should().Equal(true, false);
    }

    [Fact]
    public async Task GetPackage_MatchesTheExactName()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage("feed", "my-tool", "9.0.0", OneFile);
        fake.AddPackage("feed", "tool", "1.0.0", OneFile);
        fake.AddPackage("feed", "tool", "2.0.0", OneFile);
        fake.AddPackage("feed", "tool-extras", "3.0.0", OneFile);
        var client = fake.CreateClient();

        var package = await client.GetPackageAsync("feed", "tool", ct);

        package.Should().NotBeNull();
        package.Name.Should().Be("tool");
        package.Versions.Select(v => v.Version).Should().Equal("2.0.0", "1.0.0");
        (await client.ListVersionsAsync("feed", "tool", ct)).Select(v => v.Version).Should().Equal("2.0.0", "1.0.0");
        fake.Requests.Should().Contain(r => r.Uri.Query.Contains("packageNameQuery=tool"));
    }

    [Fact]
    public async Task GetPackage_Missing()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage("feed", "tool-extras", "1.0.0", OneFile);
        var client = fake.CreateClient();

        (await client.GetPackageAsync("feed", "tool", ct)).Should().BeNull();
        await client.Awaiting(c => c.ListVersionsAsync("feed", "tool", ct))
            .Should().ThrowExactlyAsync<UniversalPackagesException>();
    }

    [Fact]
    public async Task MissingFeed_ThrowsWithTheStatus()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();

        var act = () => fake.CreateClient().ListPackagesAsync("nope", cancellationToken: ct);

        (await act.Should().ThrowExactlyAsync<UniversalPackagesException>().WithMessage("*Feed nope not found*"))
            .Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unauthenticated_ThrowsWithTheStatus()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var client = fake.CreateClient(credentials: AzureArtifactsCredentials.Anonymous);

        var act = () => client.ListFeedsAsync(ct);

        (await act.Should().ThrowExactlyAsync<UniversalPackagesException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetManifest()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        var version = fake.AddPackage("feed", "tool", "1.0.0",
        [
            new("bin/tool.dll", new byte[5000]),
            new("readme.md", [1, 2]),
            new("empty", [])
        ], ["logs"]);

        var manifest = await fake.CreateClient().GetManifestAsync("feed", "tool", "1.0.0", ct);

        manifest.PackageName.Should().Be("tool");
        manifest.Version.Should().Be("1.0.0");
        manifest.Items.Should().Equal(
            new FileBlob("bin/tool.dll", version.Files["bin/tool.dll"].Id, 5000),
            new FileBlob("readme.md", version.Files["readme.md"].Id, 2),
            new FileBlob("empty", version.Files["empty"].Id, 0),
            new EmptyDirectoryBlob("logs"));
        manifest.TotalSize.Should().Be(5002);
    }

    [Fact]
    public async Task GetManifest_MissingVersion_ThrowsWithTheStatus()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage("feed", "tool", "1.0.0", OneFile);

        var act = () => fake.CreateClient().GetManifestAsync("feed", "tool", "2.0.0", ct);

        (await act.Should().ThrowExactlyAsync<UniversalPackagesException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a\\b.txt")]
    public async Task GetManifest_UnsafePath_Throws(string path)
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage("feed", "tool", "1.0.0", [new(path, [1])]);

        var act = () => fake.CreateClient().GetManifestAsync("feed", "tool", "1.0.0", ct);

        await act.Should().ThrowExactlyAsync<UniversalPackagesException>().WithMessage("*unsafe path*");
    }
}
