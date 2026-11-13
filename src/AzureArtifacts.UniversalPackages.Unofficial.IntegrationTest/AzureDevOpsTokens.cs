using Azure.Core;

namespace AzureArtifacts.UniversalPackages.Unofficial.IntegrationTest;

/// <summary>
/// Azure DevOps tokens from an Azure credential, cache until expiry
/// </summary>
internal sealed class AzureDevOpsTokens
{
    /// <summary>Azure DevOps Scope</summary>
    private const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken _token;

    public AzureDevOpsTokens(TokenCredential credential)
    {
        _credential = credential;
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token.ExpiresOn - DateTimeOffset.UtcNow < RefreshBeforeExpiry)
                _token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), ct);
            return _token.Token;
        }
        finally
        {
            _lock.Release();
        }
    }
}