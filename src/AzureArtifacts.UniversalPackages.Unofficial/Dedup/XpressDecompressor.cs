namespace AzureArtifacts.UniversalPackages.Unofficial.Dedup;

/// <summary>
/// Decompresses the "Plain LZ77" format of [MS-XCA] (Xpress), which storage serves dedup blobs in
/// (<c>Content-Encoding: xpress</c>)
/// </summary>
/// <remarks>
/// Modelled on decompress.rs in Microsoft's azure-devops-rust-api (MIT, see THIRD-PARTY-NOTICES.md). The format is a
/// sequence of 32-bit flag words (most significant bit first), each flag choosing between a literal byte and a match:
/// a 16-bit token of 13 offset bits and 3 length bits, with lengths of 10 or more extended by a shared half byte, then
/// a byte, then a 16- or 32-bit value
/// </remarks>
internal static class XpressDecompressor
{
    /// <summary>The bytes of a flag word</summary>
    private const int FlagWordLength = 4;

    /// <summary>The flags in a flag word</summary>
    private const int FlagsPerWord = 32;

    /// <summary>The bytes of a match token</summary>
    private const int TokenLength = 2;

    /// <summary>The low bits of a token that hold the length; the rest hold the offset</summary>
    private const int TokenLengthBits = 3;

    private const int TokenLengthMask = (1 << TokenLengthBits) - 1;

    /// <summary>The token length that means the length continues in a half byte</summary>
    private const int TokenLengthMax = TokenLengthMask;

    /// <summary>A token's offset is stored less one</summary>
    private const int OffsetBias = 1;

    /// <summary>The half byte length that means the length continues in a byte</summary>
    private const int HalfByteLengthMax = 0x0F;

    private const int HalfByteBits = 4;

    /// <summary>The byte length that means the length continues in 16 bits</summary>
    private const int ByteLengthMax = byte.MaxValue;

    /// <summary>The bytes of a 16-bit length</summary>
    private const int WordLength = 2;

    /// <summary>The bytes of a 32-bit length, which follows a 16-bit length of 0</summary>
    private const int DoubleWordLength = 4;

    /// <summary>The 16-bit length that means the length continues in 32 bits</summary>
    private const int WordLengthContinues = 0;

    /// <summary>The shortest match</summary>
    private const int MinMatchLength = 3;

    /// <summary>No half byte is waiting to be shared</summary>
    private const int NoSharedHalfByte = -1;

    /// <summary>The output buffer, when the size isn't known: this many times the input, and at least
    /// <see cref="MinOutputLength"/> bytes</summary>
    private const int OutputGrowthFactor = 4;

    private const int MinOutputLength = 256;

    /// <summary>How much the output buffer grows by when it is full (the size not being known)</summary>
    private const int OutputResizeFactor = 2;

    private const int BitsPerByte = 8;

    /// <param name="input">The compressed bytes</param>
    /// <param name="expectedSize">
    /// The uncompressed size, when known (chunks); otherwise decompression runs to the end of the input
    /// </param>
    /// <exception cref="InvalidDataException">The input is not valid, or doesn't decompress to the expected size</exception>
    public static byte[] Decompress(byte[] input, long? expectedSize = null)
    {
        if (expectedSize is > int.MaxValue)
            throw new InvalidDataException("A blob larger than 2 GB can't be decompressed in memory");

        var output = new byte[(int?)expectedSize ?? Math.Max(input.Length * OutputGrowthFactor, MinOutputLength)];
        
        var outputLength = 0;
        var position = 0;
        uint flags = 0;
        var flagCount = 0;
        var sharedHalfByte = NoSharedHalfByte;

        while (expectedSize is { } size ? outputLength < size : position < input.Length)
        {
            if (flagCount == 0)
            {
                flags = (uint)Read(FlagWordLength);
                flagCount = FlagsPerWord;
            }
            flagCount--;

            if ((flags & (1u << flagCount)) == 0)
            {
                if (position >= input.Length)
                    break;

                Grow(1);
                output[outputLength++] = input[position++];
                continue;
            }

            // A match with no input left marks the end of the stream
            if (position >= input.Length)
                break;

            var token = (int)Read(TokenLength);
            var offset = (token >> TokenLengthBits) + OffsetBias;
            var length = token & TokenLengthMask;

            if (length == TokenLengthMax)
            {
                if (sharedHalfByte == NoSharedHalfByte)
                {
                    Need(1);
                    length = input[position] & HalfByteLengthMax;
                    sharedHalfByte = position++;
                }
                else
                {
                    length = input[sharedHalfByte] >> HalfByteBits;
                    sharedHalfByte = NoSharedHalfByte;
                }

                if (length == HalfByteLengthMax)
                {
                    Need(1);
                    length = input[position++];
                    if (length == ByteLengthMax)
                    {
                        length = (int)Read(WordLength);
                        if (length == WordLengthContinues)
                            length = (int)Read(DoubleWordLength);
                        if (length < HalfByteLengthMax + TokenLengthMax)
                            throw new InvalidDataException("Invalid match length in compressed data");
                        length -= HalfByteLengthMax + TokenLengthMax;
                    }
                    length += HalfByteLengthMax;
                }
                length += TokenLengthMax;
            }
            length += MinMatchLength;

            if (offset > outputLength)
                throw new InvalidDataException("A match refers to data before the start of the output");

            Grow(length);
            // Byte by byte: a match may overlap the bytes it produces
            for (var i = 0; i < length; i++, outputLength++)
                output[outputLength] = output[outputLength - offset];
        }

        if (expectedSize is { } expected && outputLength != expected)
            throw new InvalidDataException($"Decompressed {outputLength} bytes, expected {expected}");

        if (outputLength == output.Length)
            return output;

        var result = new byte[outputLength];
        Array.Copy(output, result, outputLength);
        return result;

        // Reads a little-endian value of count bytes
        uint Read(int count)
        {
            Need(count);
            uint value = 0;
            for (var b = 0; b < count; b++)
                value |= (uint)input[position + b] << (BitsPerByte * b);
            position += count;
            return value;
        }

        void Need(int count)
        {
            if (position + count > input.Length)
                throw new InvalidDataException("Compressed data ends unexpectedly");
        }

        void Grow(int count)
        {
            var required = (long)outputLength + count;
            if (expectedSize is { } size)
            {
                if (required > size)
                    throw new InvalidDataException($"Compressed data decompresses to more than {size} bytes");
                return;
            }

            if (required <= output.Length)
                return;
            if (required > int.MaxValue)
                throw new InvalidDataException("A blob larger than 2 GB can't be decompressed in memory");

            Array.Resize(ref output, (int)Math.Min(int.MaxValue, Math.Max(required, (long)output.Length * OutputResizeFactor)));
        }
    }
}
