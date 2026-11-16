namespace AzureArtifacts.UniversalPackages.Unofficial.Dedup;

/// <summary>
/// Checks content against a blob's chunks without downloading them: the content is split at the chunks' boundaries
/// (their sizes, from the blob's nodes, which are checked against their identifiers when downloaded) and each piece is
/// hashed against its chunk's identifier
/// </summary>
/// <remarks>
/// Re-chunking the content to recompute a node's identifier would need the service's content-defined chunker; the
/// boundaries make that unnecessary. The chunk hashes authenticate the content, as the node hashes authenticate them
/// </remarks>
internal static class ContentVerifier
{
    /// <summary>Whether <paramref name="content"/> is exactly the chunks' content, read to its end</summary>
    /// <param name="content">The content, read from its current position</param>
    /// <param name="chunks">The blob's chunks, in order (none for empty content)</param>
    /// <param name="bufferSize">The read buffer size</param>
    /// <param name="ct">Cancels the reads</param>
    public static async Task<bool> MatchesAsync(
        Stream content, IReadOnlyList<DedupChild> chunks, int bufferSize, CancellationToken ct)
    {
        var buffer = new byte[bufferSize];
        using var hash = ContentHash.CreateIncremental();
        foreach (var chunk in chunks)
        {
            var remaining = chunk.Size;
            while (remaining > 0)
            {
                var toRead = remaining < buffer.Length ? (int)remaining : buffer.Length;
                var read = await content.ReadIntoBuffer(buffer, toRead, ct).ConfigureAwait(false);
                if (read == 0)
                    return false; // Shorter than the blob

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            if (!string.Equals(ContentHash.Finish(hash), chunk.Id.Hash, StringComparison.Ordinal))
                return false;
        }

        // Longer than the blob?
        return await content.ReadIntoBuffer(buffer, 1, ct).ConfigureAwait(false) == 0;
    }

    private static async Task<int> ReadIntoBuffer(this Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
#if NET6_0_OR_GREATER
        return await stream.ReadAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
#else
        return await stream.ReadAsync(buffer, 0, count, ct).ConfigureAwait(false);
#endif
    }
}
