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
}
