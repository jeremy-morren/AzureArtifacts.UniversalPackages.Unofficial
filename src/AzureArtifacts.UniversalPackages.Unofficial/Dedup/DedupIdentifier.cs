// ReSharper disable MemberCanBePrivate.Global

namespace AzureArtifacts.UniversalPackages.Unofficial.Dedup;

/// <summary>
/// The identifier of a blob in the dedup store:
/// a 32-byte content hash followed by a <see cref="DedupType"/> byte, as 66 hexadecimal characters
/// </summary>
internal readonly record struct DedupIdentifier : IEquatable<string>
{
    /// <summary>The bytes of the type</summary>
    private const int TypeLength = 1;

    /// <summary>The characters of the hash</summary>
    private const int HashChars = ContentHash.Length * Hex.CharsPerByte;

    /// <summary>The characters of an identifier</summary>
    public const int Chars = (ContentHash.Length + TypeLength) * Hex.CharsPerByte;

    private DedupIdentifier(string hash, DedupType type)
    {
        Hash = hash;
        Type = type;
    }

    /// <summary>The content hash, as 64 upper-case hexadecimal characters</summary>
    public string Hash { get; }

    /// <summary>The type</summary>
    public DedupType Type { get; }

    public bool IsChunk => Type == DedupType.Chunk;

    public bool IsNode => Type == DedupType.Node;

    public static DedupIdentifier Chunk(string hash) => new(hash, DedupType.Chunk);

    public static DedupIdentifier Node(string hash) => new(hash, DedupType.Node);

    /// <exception cref="FormatException">The value is not a chunk or node identifier</exception>
    public static DedupIdentifier Parse(string value)
    {
        if (value.Length != Chars || !Hex.IsHex(value))
            throw new FormatException($"'{value}' is not a dedup identifier");

        var hash = value.Substring(0, HashChars).ToUpperInvariant();
        var type = (DedupType)Convert.ToByte(value.Substring(HashChars), Hex.Base);
        return type switch
        {
            DedupType.Chunk => Chunk(hash),
            DedupType.Node => Node(hash),
            _ => throw new FormatException($"'{value}' has an unknown dedup type {value.Substring(HashChars)}")
        };
    }

    public override string ToString() => Hash + ((byte)Type).ToString(Hex.ByteFormat);
    
    /// <summary>
    /// Checks whether the identifier is equal to a string representation of a dedup identifier, ignoring case
    /// </summary>
    public bool Equals(string? other) => string.Equals(ToString(), other, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The type byte of a dedup identifier (BuildXL's algorithm ID)</summary>
internal enum DedupType : byte
{
    /// <summary>A chunk: raw content</summary>
    Chunk = 0x01,

    /// <summary>A node with 64K chunking: an ordered list of chunks and nodes</summary>
    Node = 0x02
}
