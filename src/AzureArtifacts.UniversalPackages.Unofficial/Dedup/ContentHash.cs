using System.Diagnostics;
using System.Security.Cryptography;

namespace AzureArtifacts.UniversalPackages.Unofficial.Dedup;

/// <summary>
/// The content hash of a dedup blob: SHA-512, truncated to its first 32 bytes.
/// A chunk's identifier is the hash of the chunk's (uncompressed) bytes.
/// A node's identifier is the hash of the node's serialized bytes
/// </summary>
internal static class ContentHash
{
    public const int Length = 32;

    /// <summary>The hash, as 64 upper-case hexadecimal characters</summary>
    public static string Compute(byte[] data)
    {
#if NET6_0_OR_GREATER
        var hash = SHA512.HashData(data);
#else
        using var sha512 = SHA512.Create();
        var hash = sha512.ComputeHash(data);
#endif
        Debug.Assert(hash.Length >= Length);
        return Hex.Encode(hash, 0, Length);
    }

    /// <summary>A hash of content appended a piece at a time (see <see cref="Finish"/>)</summary>
    public static IncrementalHash CreateIncremental() => IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

    /// <summary>The hash of the content appended (as from <see cref="Compute"/>); resets <paramref name="hash"/></summary>
    public static string Finish(IncrementalHash hash)
    {
        var value = hash.GetHashAndReset();
        Debug.Assert(value.Length >= Length);
        return Hex.Encode(value, 0, Length);
    }
}
