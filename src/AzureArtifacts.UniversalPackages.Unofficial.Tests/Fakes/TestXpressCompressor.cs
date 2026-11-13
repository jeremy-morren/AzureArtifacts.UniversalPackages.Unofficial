namespace AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

/// <summary>
/// A greedy [MS-XCA] Plain LZ77 compressor, following the specification's encoder (section 2.3), to produce the
/// compressed blobs storage serves
/// </summary>
internal static class TestXpressCompressor
{
    private const int MaxOffset = 8192;

    public static byte[] Compress(byte[] input)
    {
        var output = new List<byte>();
        var flagsPosition = Reserve(output);
        uint flags = 0;
        var flagCount = 0;
        var nibblePosition = -1;
        var lastPositions = new Dictionary<int, int>();

        var position = 0;
        while (position < input.Length)
        {
            var (offset, length) = FindMatch(input, position, lastPositions);
            if (length < 3)
            {
                output.Add(input[position]);
                Remember(input, position, lastPositions);
                position++;
                flags <<= 1;
            }
            else
            {
                WriteMatch(output, offset, length, ref nibblePosition);
                for (var i = 0; i < length; i++)
                    Remember(input, position + i, lastPositions);
                position += length;
                flags = (flags << 1) | 1;
            }

            if (++flagCount == 32)
            {
                WriteUInt32(output, flagsPosition, flags);
                flagsPosition = Reserve(output);
                flags = 0;
                flagCount = 0;
            }
        }

        // The unused flags are set: a match with no input ends the stream
        flags <<= 32 - flagCount;
        flags |= (uint)((1L << (32 - flagCount)) - 1);
        WriteUInt32(output, flagsPosition, flags);
        return output.ToArray();
    }

    private static (int Offset, int Length) FindMatch(byte[] input, int position, Dictionary<int, int> lastPositions)
    {
        if (position + 3 > input.Length || !lastPositions.TryGetValue(Key(input, position), out var candidate))
            return (0, 0);

        var offset = position - candidate;
        if (offset > MaxOffset)
            return (0, 0);

        var length = 0;
        while (position + length < input.Length && input[candidate + length] == input[position + length])
            length++;
        return (offset, length);
    }

    private static void WriteMatch(List<byte> output, int offset, int length, ref int nibblePosition)
    {
        length -= 3;
        offset -= 1;
        if (length < 7)
        {
            WriteUInt16(output, (offset << 3) | length);
            return;
        }

        WriteUInt16(output, (offset << 3) | 7);
        length -= 7;

        var nibble = Math.Min(length, 15);
        if (nibblePosition < 0)
        {
            nibblePosition = output.Count;
            output.Add((byte)nibble);
        }
        else
        {
            output[nibblePosition] |= (byte)(nibble << 4);
            nibblePosition = -1;
        }

        if (length < 15)
            return;

        length -= 15;
        if (length < 255)
        {
            output.Add((byte)length);
            return;
        }

        output.Add(255);
        length += 7 + 15;
        if (length < 1 << 16)
        {
            WriteUInt16(output, length);
        }
        else
        {
            WriteUInt16(output, 0);
            var position = Reserve(output);
            WriteUInt32(output, position, (uint)length);
        }
    }

    private static void Remember(byte[] input, int position, Dictionary<int, int> lastPositions)
    {
        if (position + 3 <= input.Length)
            lastPositions[Key(input, position)] = position;
    }

    private static int Key(byte[] input, int position) =>
        input[position] | input[position + 1] << 8 | input[position + 2] << 16;

    private static int Reserve(List<byte> output)
    {
        var position = output.Count;
        output.AddRange(new byte[4]);
        return position;
    }

    private static void WriteUInt16(List<byte> output, int value)
    {
        output.Add((byte)value);
        output.Add((byte)(value >> 8));
    }

    private static void WriteUInt32(List<byte> output, int position, uint value)
    {
        for (var i = 0; i < 4; i++)
            output[position + i] = (byte)(value >> (8 * i));
    }
}
