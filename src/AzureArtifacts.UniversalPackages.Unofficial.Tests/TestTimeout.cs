namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

/// <summary>Cancels a test that hangs</summary>
internal static class TestTimeout
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    /// <summary>A new token, cancelled after a minute</summary>
    public static CancellationToken Token() => new CancellationTokenSource(Timeout).Token;
}
