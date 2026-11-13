namespace AzureArtifacts.UniversalPackages.Unofficial.Dedup;

/// <summary>
/// A dedup node: the ordered children (chunks or nested nodes) whose content, concatenated, is the node's content
/// </summary>
/// <remarks>
/// Serialized format (BuildXL DedupNode, MIT, see THIRD-PARTY-NOTICES.md), little-endian:
/// <code>
/// u16 version (0) | u16 child count - 1 | per child: u8 type (0 chunk, 1 node), size (3 bytes for a chunk, 7 for a
/// node), 32-byte hash
/// </code>
/// </remarks>
internal static class DedupNode
{
    public const int MaxChildren = 512;

    private const int Version = 0;
    private const int VersionLength = 2;
    private const int CountLength = 2;
    private const int HeaderLength = VersionLength + CountLength;

    /// <summary>The child count is stored less one (a node has at least one child)</summary>
    private const int CountBias = 1;

    private const int ChunkSizeLength = 3;
    private const int NodeSizeLength = 7;
    private const int BitsPerByte = 8;

    /// <exception cref="InvalidDataException">The bytes are not a dedup node</exception>
    public static IReadOnlyList<DedupChild> Parse(byte[] data)
    {
        if (data.Length < HeaderLength)
            throw new InvalidDataException("A dedup node is too short");

        var version = ReadLittleEndian(data, 0, VersionLength);
        if (version != Version)
            throw new InvalidDataException($"Unknown dedup node version {version}");

        var count = (int)ReadLittleEndian(data, VersionLength, CountLength) + CountBias;
        var children = new List<DedupChild>(count);
        var offset = HeaderLength;
        for (var i = 0; i < count; i++)
        {
            if (offset >= data.Length)
                throw new InvalidDataException("A dedup node ends before its last child");

            var type = (DedupNodeChildType)data[offset++];
            var sizeLength = type switch
            {
                DedupNodeChildType.Chunk => ChunkSizeLength,
                DedupNodeChildType.Node => NodeSizeLength,
                _ => throw new InvalidDataException($"Unknown dedup node child type {(byte)type}")
            };
            if (offset + sizeLength + ContentHash.Length > data.Length)
                throw new InvalidDataException("A dedup node ends before its last child");

            var size = ReadLittleEndian(data, offset, sizeLength);
            offset += sizeLength;

            var hash = Hex.Encode(data, offset, ContentHash.Length);
            offset += ContentHash.Length;

            children.Add(new DedupChild(
                type == DedupNodeChildType.Chunk ? DedupIdentifier.Chunk(hash) : DedupIdentifier.Node(hash), size));
        }

        if (offset != data.Length)
            throw new InvalidDataException("A dedup node has trailing bytes");
        return children;
    }

    private static long ReadLittleEndian(byte[] data, int offset, int length)
    {
        long value = 0;
        for (var b = 0; b < length; b++)
            value |= (long)data[offset + b] << (BitsPerByte * b);
        return value;
    }
}

/// <summary>A child of a dedup node, with the size of its (uncompressed) content</summary>
internal readonly record struct DedupChild(DedupIdentifier Id, long Size)
{
    /// <summary>The <see cref="Size"/> of a blob whose content size isn't known</summary>
    public const long UnknownSize = -1;
}

/// <summary>The type of a child in a serialized dedup node</summary>
internal enum DedupNodeChildType : byte
{
    Chunk = 0,
    Node = 1
}
