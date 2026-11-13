using System.Net;

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>
/// A call to Azure Artifacts failed, or downloaded content didn't match its identifier
/// </summary>
public sealed class UniversalPackagesException : Exception
{
    /// <summary>Creates the exception</summary>
    public UniversalPackagesException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>The response status, when the failure was an HTTP error</summary>
    public HttpStatusCode? StatusCode { get; }
}
