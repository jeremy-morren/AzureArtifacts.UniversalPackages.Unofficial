using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

// ReSharper disable ArrangeObjectCreationWhenTypeNotEvident
// ReSharper disable AccessToDisposedClosure

namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

public class CredentialTests
{
    [Fact]
    public async Task Provider_IsAskedForEachAzureDevOpsRequest_AndNeverForStorage()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.AddPackage("feed", "my-package", "1.0.0", 
            [new("a.txt", new byte[5000]), new("b.txt", [1, 2, 3])]);
        var asked = new ConcurrentQueue<Uri>();
        var credentials = AzureArtifactsCredentials.FromDelegate(
            (uri, _) =>
            {
                asked.Enqueue(uri);
                return Task.FromResult<AuthenticationHeaderValue?>(
                    new AuthenticationHeaderValue("Bearer", FakeAzureDevOps.Token));
            });

        await fake.CreateClient(credentials: credentials).DownloadAsync(
            "feed", "my-package", "1.0.0", 
            new CallbackDownloader((_, source, token) => source.CopyToAsync(Stream.Null, 81920, token)),
            null,
            ct);

        var devOps = fake.Requests.Where(r => r.Uri.Host != FakeAzureDevOps.StorageHost).ToList();
        var storage = fake.Requests.Where(r => r.Uri.Host == FakeAzureDevOps.StorageHost).ToList();
        storage.Should().NotBeEmpty()
            .And.OnlyContain(r => r.Authorization == null);
        devOps.Should().OnlyContain(r => r.Authorization == "Bearer " + FakeAzureDevOps.Token);
        asked.Should().Equal(devOps.Select(r => r.Uri));
    }

    [Fact]
    public async Task Provider_IsAskedAgainForEachRetry()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();
        fake.Feed("feed");
        fake.FailFirst["feeds.dev.azure.com"] = 2;
        var tokens = 0;
        var credentials = AzureArtifactsCredentials.BearerToken(_ =>
            Task.FromResult(Interlocked.Increment(ref tokens) == 3 ? FakeAzureDevOps.Token : "stale"));

        var feeds = await fake.CreateClient(
                new UniversalPackagesClientOptions { RetryDelay = TimeSpan.FromMilliseconds(1) },
                credentials)
            .ListFeedsAsync(ct);

        feeds.Should().ContainSingle();
        tokens.Should().Be(3);
    }

    [Fact]
    public async Task PersonalAccessToken_IsSentAsBasic()
    {
        var ct = Token();

        var header = await AzureArtifactsCredentials.PersonalAccessToken("my-pat")
            .GetAuthorizationAsync(new Uri("https://feeds.dev.azure.com/contoso/"), ct);

        header.Should().NotBeNull();
        header.Scheme.Should().Be("Basic");
        Encoding.ASCII.GetString(Convert.FromBase64String(header.Parameter!)).Should().Be(":my-pat");
    }

    [Fact]
    public async Task Anonymous_SendsNoAuthorization()
    {
        var ct = Token();
        using var fake = new FakeAzureDevOps();

        var act = () => fake.CreateClient(credentials: AzureArtifactsCredentials.Anonymous).ListFeedsAsync(ct);

        (await act.Should().ThrowExactlyAsync<UniversalPackagesException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fake.Requests.Should().ContainSingle()
            .Which.Authorization.Should().BeNull();
    }

    [Fact]
    public void HttpClientWithDefaultAuthorization_IsRejected()
    {
        using var fake = new FakeAzureDevOps();
        var http = fake.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FakeAzureDevOps.Token);

        var act = () => new UniversalPackagesClient(http,
            fake.Location, AzureArtifactsCredentials.BearerToken(FakeAzureDevOps.Token));

        act.Should().ThrowExactly<ArgumentException>()
            .WithParameterName("httpClient");
    }
}
