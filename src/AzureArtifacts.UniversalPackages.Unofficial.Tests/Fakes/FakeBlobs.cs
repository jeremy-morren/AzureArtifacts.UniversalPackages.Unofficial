using System.Security.Cryptography;

namespace AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

/// <summary>A blob's identifier and the size of its content</summary>
internal sealed record FakeBlob(string Id, long Size);

/// <summary>
/// The content of a fake dedup store: chunks and nodes, hashed and serialized independently of the library's code
/// </summary>
internal sealed class FakeBlobs
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every blob, by identifier (uncompressed)</summary>
    public IReadOnlyDictionary<string, byte[]> All => _blobs;

    /// <summary>Replaces a blob's content without changing its identifier (corruption)</summary>
    public void Replace(string id, byte[] content) => _blobs[id] = content;

    public string AddChunk(byte[] data)
    {
        var id = Hash(data) + "01";
        _blobs[id] = data;
        return id;
    }

    public string AddNode(IReadOnlyList<FakeBlob> children)
    {
        var bytes = new List<byte> { 0, 0, (byte)(children.Count - 1), (byte)((children.Count - 1) >> 8) };
        foreach (var child in children)
        {
            var isNode = child.Id.EndsWith("02", StringComparison.Ordinal);
            bytes.Add((byte)(isNode ? 1 : 0));
            for (var b = 0; b < (isNode ? 7 : 3); b++)
                bytes.Add((byte)(child.Size >> (8 * b)));
            bytes.AddRange(FromHex(child.Id.Substring(0, 64)));
        }

        var data = bytes.ToArray();
        var id = Hash(data) + "02";
        _blobs[id] = data;
        return id;
    }

    /// <summary>
    /// Stores content as a chunk, or (larger than <paramref name="chunkSize"/>) as chunks under a tree of nodes of up to
    /// <paramref name="maxChildren"/> children
    /// </summary>
    public FakeBlob AddContent(byte[] data, int chunkSize, int maxChildren)
    {
        if (data.Length <= chunkSize)
            return new FakeBlob(AddChunk(data), data.Length);

        var level = new List<FakeBlob>();
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var chunk = new byte[Math.Min(chunkSize, data.Length - offset)];
            Array.Copy(data, offset, chunk, 0, chunk.Length);
            level.Add(new FakeBlob(AddChunk(chunk), chunk.Length));
        }

        do
        {
            level = level
                .Select((blob, index) => (blob, index))
                .GroupBy(x => x.index / maxChildren)
                .Select(group =>
                {
                    var children = group.Select(x => x.blob).ToList();
                    return new FakeBlob(AddNode(children), children.Sum(c => c.Size));
                })
                .ToList();
        } while (level.Count > 1);

        return level[0];
    }

    /// <summary>SHA-512, truncated to 32 bytes, as upper-case hex</summary>
    public static string Hash(byte[] data)
    {
        using var sha512 = SHA512.Create();
        return ToHex(sha512.ComputeHash(data), 32);
    }

    public static string ToHex(byte[] bytes, int count) =>
        string.Concat(bytes.Take(count).Select(b => b.ToString("X2")));

    public static byte[] FromHex(string hex) =>
        Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
}
