using System.CommandLine;
using System.Diagnostics;
using Azure.Identity;
using AzureArtifacts.UniversalPackages.Unofficial;
using AzureArtifacts.UniversalPackages.Unofficial.IntegrationTest;

// ReSharper disable StringLiteralTypo

// Tests the client against a real feed (read-only):
//   list-versions  lists a package's versions
//   download       downloads a version, and checks every file's content against the manifest
// Credentials come from --azure-credential (DefaultAzureCredential), or else UPACK_TOKEN (an Entra token:
// az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798) or UPACK_PAT.

const string tokenEnvVar = "UPACK_TOKEN";
const string patEnvVar = "UPACK_PAT";

// Options of every command
var organization = new Option<string>("--organization", "--organisation")
{
    Description = "The Azure DevOps organization",
    Required = true,
    Recursive = true
};
var project = new Option<string?>("--project")
{
    Description = "The project, for a project-scoped feed",
    Recursive = true
};
var feed = new Option<string>("--feed")
{
    Description = "The feed name (or ID)",
    Required = true,
    Recursive = true
};
var package = new Option<string>("--package")
{
    Description = "The package name",
    Required = true,
    Recursive = true
};
var azureCredential = new Option<bool>("--azure-credential")
{
    Description = $"Authenticate with DefaultAzureCredential, instead of {tokenEnvVar} or {patEnvVar}",
    Recursive = true
};

var version = new Option<string>("--version")
{
    Description = "The package version",
    Required = true
};

var listVersions = new Command(
    "list-versions", 
    "Lists the versions of a package, newest first");

listVersions.SetAction((parseResult, ct) => RunAsync(parseResult, async client =>
{
    var versions = await client.ListVersionsAsync(
        parseResult.GetRequiredValue(feed), parseResult.GetRequiredValue(package), ct);

    foreach (var v in versions)
        Console.WriteLine(
            $"{v.Version,-20} {v.PublishDate:yyyy-MM-dd hh:mm tt}" + (v.IsLatest ? "  latest" : "") + (v.IsListed ? "" : "  unlisted"));
    Console.WriteLine($"{versions.Count} versions");
}));

var download = new Command(
    "download", 
    "Downloads a package version, and checks its files against the manifest")
    {
        version
    };
download.SetAction((parseResult, ct) => RunAsync(parseResult, async client =>
{
    Console.WriteLine("Downloading {0} {1}...", parseResult.GetRequiredValue(package), parseResult.GetRequiredValue(version));
    
    var packageName = parseResult.GetRequiredValue(package);
    
    var stopwatch = Stopwatch.StartNew();
    var downloader = new SizeCheckingDownloader();
    var manifest = await client.DownloadAsync(
        parseResult.GetRequiredValue(feed), packageName, parseResult.GetRequiredValue(version), downloader,
        cancellationToken: ct);

    var expected = manifest.Files.Select(f => (f.Path, f.Size)).ToList();
    if (!downloader.Received.SequenceEqual(expected))
        throw new UniversalPackagesException("The files downloaded don't match the manifest");

    Console.WriteLine(
        $"Downloaded {packageName} {manifest.Version}: {expected.Count} files, {manifest.TotalSize:N0} bytes in {stopwatch.Elapsed.TotalSeconds:N1}s");
}));

var root = new RootCommand(
    $"Tests the Universal Packages client against a real feed (read-only). " +
    $"Authenticates with --azure-credential, or {tokenEnvVar} (an Entra token) or {patEnvVar} (a personal access token)")
{
    listVersions,
    download
};
foreach (var option in new Option[] { organization, project, feed, package, azureCredential })
    root.Options.Add(option);

return await root.Parse(args).InvokeAsync();

// Runs a command with a client: 1 when there are no credentials or the client fails
async Task<int> RunAsync(ParseResult parseResult, Func<UniversalPackagesClient, Task> command)
{
    IAzureArtifactsCredentialProvider credentials;
    if (parseResult.GetValue(azureCredential))
    {
        credentials = AzureArtifactsCredentials.BearerToken(new AzureDevOpsTokens(new DefaultAzureCredential()).GetAsync);
    }
    else
    {
        var token = Environment.GetEnvironmentVariable(tokenEnvVar);
        var pat = Environment.GetEnvironmentVariable(patEnvVar);
        if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(pat))
        {
            Console.Error.WriteLine($"Use --azure-credential, or set {tokenEnvVar} or {patEnvVar}");
            return 1;
        }
        credentials = string.IsNullOrEmpty(token)
            ? AzureArtifactsCredentials.PersonalAccessToken(pat!)
            : AzureArtifactsCredentials.BearerToken(token);
    }

    using var httpClient = new HttpClient();

    var client = new UniversalPackagesClient(
        httpClient,
        new AzureArtifactsLocation(parseResult.GetRequiredValue(organization), parseResult.GetValue(project)),
        credentials);
    try
    {
        await command(client);
        return 0;
    }
    catch (UniversalPackagesException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

/// <summary>Reads each file's content whole, and records its path and the bytes read</summary>
internal sealed class SizeCheckingDownloader : IPackageDownloader
{
    private const int BufferSize = 81920;

    public List<(string Path, long Size)> Received { get; } = [];

    public Task<Stream?> ShouldDownloadFileAsync(UniversalPackageFile blob, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(null);

    public async Task DownloadFileAsync(UniversalPackageFile blob, Stream source, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            size += read;
        Received.Add((blob.Path, size));
    }

    public Task HandleEmptyDirectoryAsync(UniversalPackageEmptyDirectory blob, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
