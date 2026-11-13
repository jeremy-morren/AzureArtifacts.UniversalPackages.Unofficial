using System.Net.Http.Headers;

namespace AzureArtifacts.UniversalPackages.Unofficial;

/// <summary>Header names, header values and media types of the requests sent</summary>
internal static class HttpNames
{
    /// <summary>Asks Azure DevOps for a 401, rather than a redirect to a sign-in page</summary>
    public const string FedAuthRedirectHeader = "X-TFS-FedAuthRedirect";

    public const string FedAuthRedirectSuppress = "Suppress";

    /// <summary>The session ID header of Azure DevOps requests (as VssHttpRequestSettings sends it)</summary>
    public const string SessionHeader = "X-TFS-Session";

    /// <summary>The client request ID header of storage requests, which ArtifactTool sets to the session</summary>
    public const string StorageClientRequestIdHeader = "x-ms-client-request-id";

    /// <summary>Precedes the session ID in <see cref="StorageClientRequestIdHeader"/> (as ArtifactTool's RequestHandler)</summary>
    public const string StorageSessionPrefix = "X_TFS_Session: ";

    /// <summary>The format of session IDs: 32 digits separated by hyphens</summary>
    public const string SessionIdFormat = "D";

    public const string JsonMediaType = "application/json";

    /// <summary>The content encoding storage marks compressed dedup blobs with ([MS-XCA] Plain LZ77)</summary>
    public const string XpressEncoding = "xpress";

    /// <summary>The host suffix of Azure Table storage, which the dedup store may serve small blobs from</summary>
    public const string AzureTableHostSuffix = ".table.core.windows.net";

    /// <summary>The product of the <c>User-Agent</c> header</summary>
    public const string UserAgentProduct = "AzureArtifacts.UniversalPackages.Unofficial";

    /// <summary>The library's version (major.minor.patch)</summary>
    private const int UserAgentVersionFields = 3;

    /// <summary>The <c>User-Agent</c> of requests: <c>AzureArtifacts.UniversalPackages.Unofficial/{version}</c></summary>
    private static readonly ProductInfoHeaderValue UserAgent = new(
        UserAgentProduct, typeof(HttpNames).Assembly.GetName().Version?.ToString(UserAgentVersionFields));

    /// <summary>
    /// Adds the library's <c>User-Agent</c>, unless the client (in its default headers) or the request already has one
    /// </summary>
    public static void AddUserAgent(this HttpRequestMessage request, HttpClient client)
    {
        if (client.DefaultRequestHeaders.UserAgent.Count == 0 && request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.Add(UserAgent);
    }

    /// <summary>Adds the session header of an Azure DevOps request</summary>
    public static void AddSession(this HttpRequestMessage request, Guid? sessionId)
    {
        if (sessionId is { } id)
            request.Headers.Add(SessionHeader, id.ToString(SessionIdFormat));
    }

    /// <summary>Adds the session header of a storage request</summary>
    public static void AddStorageSession(this HttpRequestMessage request, Guid? sessionId)
    {
        if (sessionId is { } id)
            request.Headers.Add(StorageClientRequestIdHeader, StorageSessionPrefix + id.ToString(SessionIdFormat));
    }
}
