using System.Net.Http.Headers;
using System.Text;

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// Supplies the <c>Authorization</c> header of requests to Azure DevOps (the feeds, packaging and dedup services)
/// </summary>
/// <remarks>
/// It is asked before each Azure DevOps request (including each retry), so an implementation that gets tokens from
/// elsewhere should cache them. It is never asked for package content downloads: those use signed storage URLs, which
/// storage rejects when the request also carries an Azure DevOps token. See <see cref="AzureArtifactsCredentials"/> for
/// ready-made providers
/// </remarks>
public interface IAzureArtifactsCredentialProvider
{
    /// <summary>The <c>Authorization</c> header for a request to Azure DevOps</summary>
    /// <param name="requestUri">The request's URI</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <returns>The header, or null to send the request anonymously (public feeds)</returns>
    Task<AuthenticationHeaderValue?> GetAuthorizationAsync(Uri requestUri, CancellationToken cancellationToken);
}

/// <summary>Ready-made <see cref="IAzureArtifactsCredentialProvider"/>s</summary>
public static class AzureArtifactsCredentials
{
    /// <summary>No credentials: for public feeds</summary>
    public static IAzureArtifactsCredentialProvider Anonymous { get; } =
        new DelegateProvider((_, _) => Task.FromResult<AuthenticationHeaderValue?>(null));

    /// <summary>A personal access token with the Packaging (Read) scope, sent as Basic authentication</summary>
    public static IAzureArtifactsCredentialProvider PersonalAccessToken(string token)
    {
        Required(token, nameof(token));
        var header = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token)));
        return new DelegateProvider((_, _) => Task.FromResult<AuthenticationHeaderValue?>(header));
    }

    /// <summary>
    /// An Entra ID access token for Azure DevOps (resource <c>499b84ac-1321-427f-aa17-267ca6975798</c>), or a pipeline's
    /// <c>System.AccessToken</c>, sent as a Bearer token
    /// </summary>
    public static IAzureArtifactsCredentialProvider BearerToken(string token)
    {
        Required(token, nameof(token));
        var header = new AuthenticationHeaderValue("Bearer", token);
        return new DelegateProvider((_, _) => Task.FromResult<AuthenticationHeaderValue?>(header));
    }

    /// <summary>
    /// Bearer tokens from a function, asked before each request (it should cache them, as Azure.Identity's credentials
    /// do), e.g. <c>async ct => (await credential.GetTokenAsync(new(["499b84ac-1321-427f-aa17-267ca6975798/.default"]), ct)).Token</c>
    /// </summary>
    public static IAzureArtifactsCredentialProvider BearerToken(Func<CancellationToken, Task<string>> getToken)
    {
        if (getToken == null)
            throw new ArgumentNullException(nameof(getToken));

        return new DelegateProvider(async (_, cancellationToken) =>
            new AuthenticationHeaderValue("Bearer", await getToken(cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Headers from a function, asked before each request</summary>
    public static IAzureArtifactsCredentialProvider FromDelegate(
        Func<Uri, CancellationToken, Task<AuthenticationHeaderValue?>> getAuthorization) =>
        new DelegateProvider(getAuthorization ?? throw new ArgumentNullException(nameof(getAuthorization)));

    private static void Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A token is required", name);
    }

    private sealed class DelegateProvider : IAzureArtifactsCredentialProvider
    {
        private readonly Func<Uri, CancellationToken, Task<AuthenticationHeaderValue?>> _getAuthorization;

        public DelegateProvider(Func<Uri, CancellationToken, Task<AuthenticationHeaderValue?>> getAuthorization)
        {
            _getAuthorization = getAuthorization;
        }

        public Task<AuthenticationHeaderValue?> GetAuthorizationAsync(Uri requestUri, CancellationToken cancellationToken) =>
            _getAuthorization(requestUri, cancellationToken);
    }
}
