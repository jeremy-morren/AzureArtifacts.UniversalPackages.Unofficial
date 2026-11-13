# AzureArtifacts.UniversalPackages.Unofficial

[![CI](https://img.shields.io/github/actions/workflow/status/jeremy-morren/AzureArtifacts.UniversalPackages.Unofficial/ci.yml?label=CI)](https://github.com/jeremy-morren/AzureArtifacts.UniversalPackages.Unofficial/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/actions/workflow/status/jeremy-morren/AzureArtifacts.UniversalPackages.Unofficial/release.yml?label=Release)](https://github.com/jeremy-morren/AzureArtifacts.UniversalPackages.Unofficial/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/AzureArtifacts.UniversalPackages.Unofficial?label=NuGet)](https://www.nuget.org/packages/AzureArtifacts.UniversalPackages.Unofficial)

This is a library that lists and downloads [Azure Artifacts Universal Packages](https://learn.microsoft.com/azure/devops/artifacts/quickstarts/universal-packages)
over plain HTTP.

**Unofficial**: not affiliated with, endorsed or supported by Microsoft. It relies on the service's HTTP protocol, which
Microsoft doesn't document and could change.

## Installation

Install from NuGet:

```shell
dotnet add package AzureArtifacts.UniversalPackages.Unofficial
```

## Usage

```csharp
// Project MyProject in organization contoso
var location = new AzureArtifactsLocation("contoso", project: "MyProject");
// Or organization-scoped feeds:
var location = new AzureArtifactsLocation("contoso");

var client = new UniversalPackagesClient(httpClient, location, AzureArtifactsCredentials.BearerToken(token));

// List
var feeds = await client.ListFeedsAsync();
var packages = await client.ListPackagesAsync("my-feed");
var versions = await client.ListVersionsAsync("my-feed", "my-package");
var manifest = await client.GetManifestAsync("my-feed", "my-package", "1.2.3");

// Download into a directory, or one file to a stream
await client.DownloadToDirectoryAsync("my-feed", "my-package", "1.2.3", "packages/my-package");
await client.DownloadFileAsync("my-feed", "my-package", "1.2.3", "lib/tool.dll", stream);

// Or: handle each manifest item manually
await client.DownloadAsync("my-feed", "my-package", "1.2.3",
    new UniversalPackageDownloadOptions { DownloadFilter = blob => blob.Path.StartsWith("lib/") },
    new PackageDownloader());

class PackageDownloader : IPackageDownloader
{
    public Task DownloadFileAsync(FileBlob blob, Stream source, CancellationToken ct) => 
        Task.CompletedTask; // Handle a file

    public Task HandleEmptyDirectoryAsync(EmptyDirectoryBlob blob, CancellationToken ct) => 
        Task.CompletedTask; // Handle an empty directory
}

```

### Options

Per client:

```csharp
new UniversalPackagesClientOptions
{
    UrlBatchSize = 100,                 // IDs per dedup/urls request
    MaxRetries = 4,                     // retries of network errors and 408/429/5xx (Retry-After is honoured)
    RetryDelay = TimeSpan.FromSeconds(1),
    MaxRetryDelay = TimeSpan.FromMinutes(1),
}
```

Per download:

```csharp
new UniversalPackageDownloadOptions
{
    Parallelism = 192,                  // blobs downloaded at once. ArtifactTool uses 192
    SessionId = Guid.NewGuid(),         // Correlation ID for the download session, sent to the server
    DownloadFilter = blob => true,      // A filter to select which blobs to download.
    CopyBufferSize = 81920,             // Buffer size for copying from the HTTP stream
}
```

Requests use the `HttpClient`'s own timeout.

### Authentication

Credentials come from an `IAzureArtifactsCredentialProvider`, which the client asks for the `Authorization` header of
each Azure DevOps request (including each retry):

```csharp
// Implement this interface to provide credentials
// Bearer tokens must be for the Azure DevOps resource (499b84ac-1321-427f-aa17-267ca6975798)
public interface IAzureArtifactsCredentialProvider
{
    Task<AuthenticationHeaderValue?> GetAuthorizationAsync(Uri requestUri, CancellationToken cancellationToken);
}
```

`AzureArtifactsCredentials` has ready-made providers:

| Provider                         | Sends                                                                                                                                                                                         |
|----------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `PersonalAccessToken(pat)`       | A PAT with the Packaging (Read) scope, as `Basic base64(":" + pat)`                                                                                                                           |
| `BearerToken(token)`             | An Entra ID token for Azure DevOps (resource `499b84ac-1321-427f-aa17-267ca6975798`, e.g. from `az account get-access-token`), or a pipeline's `System.AccessToken`                           |
| `BearerToken(ct => ...)`         | Tokens from a function, asked per request (cache them), e.g. Azure.Identity: `async ct => (await credential.GetTokenAsync(new(["499b84ac-1321-427f-aa17-267ca6975798/.default"]), ct)).Token` |
| `FromDelegate((uri, ct) => ...)` | Any header                                                                                                                                                                                    |
| `Anonymous`                      | Nothing (public feeds)                                                                                                                                                                        |

The `HttpClient` must not add credentials itself, since the storage backends reject any requests with `Authorization` headers.

### Notes

- A downloader's exception fails the download, and is thrown as is.
- Manifest paths are validated: absolute paths, `..` and `.` segments, empty segments and backslashes are rejected.
- Failures throw `UniversalPackagesException`, with `StatusCode` for HTTP errors.
- `DownloadToDirectoryAsync` creates empty directories, overwrites files of the same name and leaves other files alone.

## Building and testing

```shell
dotnet build src/AzureArtifacts.UniversalPackages.Unofficial.slnx
dotnet test src/AzureArtifacts.UniversalPackages.Unofficial.slnx
```

The integration test is a tool, run by hand against a real feed (read-only).
- `list-versions` lists a package's versions
- `download` downloads a version and checks its files against the manifest. 
- `--azure-credential` signs in with `DefaultAzureCredential`. Alternatively, set `UPACK_TOKEN` 
  (an Entra token for resource `499b84ac-1321-427f-aa17-267ca6975798`) or `UPACK_PAT` (a PAT with Packaging (Read) scope).

```shell
cd src/AzureArtifacts.UniversalPackages.Unofficial.IntegrationTest
dotnet run -- list-versions \
    --organization contoso --project MyProject --feed my-feed --package my-package --azure-credential
dotnet run -- download \
    --organization contoso --project MyProject --feed my-feed --package my-package --version 1.2.3 --azure-credential
```

## Inspiration

The download protocol is modelled on the `artifacts_download` module of Microsoft's
[Azure DevOps Rust API](https://github.com/microsoft/azure-devops-rust-api) (MIT), in
[`azure_devops_rust_api/src/artifacts_download/`](https://github.com/microsoft/azure-devops-rust-api/tree/main/azure_devops_rust_api/src/artifacts_download).
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

Available under the [MIT](LICENSE) license.
