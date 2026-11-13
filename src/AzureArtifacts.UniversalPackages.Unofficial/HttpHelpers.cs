using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

#pragma warning disable CS1573

#if !NET
#nullable disable warnings
#endif

namespace AzureArtifacts.UniversalPackages.Unofficial;

internal static class HttpHelpers
{
    /// <summary>The most characters of an error response quoted in an exception</summary>
    private const int MaxErrorBody = 500;

    /// <summary>The most times the retry delay doubles (so that the shift can't overflow)</summary>
    private const int MaxBackoffDoublings = 20;

    /// <summary>429, which netstandard2.0's <see cref="HttpStatusCode"/> doesn't name</summary>
    private const HttpStatusCode TooManyRequests = (HttpStatusCode)429;

    /// <summary>
    /// Sends a request (buffering the response), retrying transient failures as
    /// <see cref="UniversalPackagesClientOptions.MaxRetries"/> allows
    /// </summary>
    /// <param name="createRequest">Creates the request (a request can only be sent once)</param>
    /// <param name="credentials">Authorizes the request (asked for each attempt), or null to send it without</param>
    /// <param name="logUri">The URI as logged</param>
    public static async Task<HttpResponseMessage> SendAsync(
        this HttpClient client, 
        Func<HttpRequestMessage> createRequest,
        IAzureArtifactsCredentialProvider? credentials, 
        string logUri, 
        UniversalPackagesClientOptions options,
        ILogger? logger, 
        CancellationToken ct)
    {
        var maxRetries = options.MaxRetries;
        for (var attempt = 1;; attempt++)
        {
            var request = createRequest();
            request.AddUserAgent(client);

            var method = request.Method.Method;

            string reason;
            TimeSpan? retryAfter = null;
            try
            {
                if (credentials != null)
                {
                    request.Headers.Authorization = 
                        await credentials.GetAuthorizationAsync(request.RequestUri!, ct).ConfigureAwait(false);
                }

                logger?.Request(method, logUri);
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (attempt > maxRetries || !response.StatusCode.IsTransient())
                    // The response is either successful or a permanent failure
                    return response;

                reason = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                retryAfter = response.RetryAfter();
                response.Dispose();
                request.Dispose();
            }
            catch (Exception ex) when (
                attempt <= maxRetries && 
                !ct.IsCancellationRequested &&
                ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // A network failure, or HttpClient's timeout
                reason = ex.Message;
                request.Dispose();
            }

            var delay = retryAfter ?? TimeSpan.FromTicks(options.RetryDelay.Ticks * (1L << Math.Min(attempt - 1, MaxBackoffDoublings)));
            if (delay > options.MaxRetryDelay)
                delay = options.MaxRetryDelay;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            logger?.Retrying(method, logUri, reason, attempt, maxRetries, (long)delay.TotalMilliseconds);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(this HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    extension(HttpResponseMessage response)
    {
        private TimeSpan? RetryAfter()
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta)
                return delta;
            if (retryAfter?.Date is { } date)
                return date - DateTimeOffset.UtcNow;
            return null;
        }

        /// <exception cref="UniversalPackagesException">The response is not successful</exception>
        public async Task EnsureSuccessAsync(string action)
        {
            if (response.IsSuccessStatusCode)
                return;

            // .NET Framework leaves Content null when the response has no body
            var body = response.Content is { } content
                ? await content.ReadAsStringAsync().ConfigureAwait(false)
                : "";
            if (body.Length > MaxErrorBody)
                body = body.Substring(0, MaxErrorBody) + "...";

            var reasonPhrase = string.IsNullOrEmpty(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase;
            body = string.IsNullOrWhiteSpace(body) ? "" : $": {body.Trim()}";
            
            throw new UniversalPackagesException(
                $"Could not {action}: {(int)response.StatusCode} {reasonPhrase}{body}",
                response.StatusCode);
        }

        public async Task<T> ReadJsonAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken ct)
        {
            response.EnsureContentType(HttpNames.JsonMediaType);
#if NET6_0_OR_GREATER
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            try
            {
                return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false)
                       ?? throw new UniversalPackagesException($"The response to {response.RequestMessage?.RequestUri} was empty");
            }
            catch (JsonException ex)
            {
                throw new UniversalPackagesException(
                    $"The response to {response.RequestMessage?.RequestUri} was not the expected JSON: {ex.Message}",
                    statusCode: response.StatusCode,
                    innerException: ex);
            }
        }
        
        private void EnsureContentType(string expected)
        {
            var actual = response.Content.Headers.ContentType?.MediaType;
            if (StringComparer.OrdinalIgnoreCase.Equals(actual, expected))
                return;
            
            if (string.IsNullOrEmpty(actual))
                throw new UniversalPackagesException(
                    $"The response to {response.RequestMessage?.RequestUri} was not the expected content type: it had no Content-Type header",
                    statusCode: response.StatusCode);
            throw new UniversalPackagesException(
                $"The response to {response.RequestMessage?.RequestUri} was not the expected content type: {actual ?? "(none)"}",
                statusCode: response.StatusCode);
        }
    }
}
