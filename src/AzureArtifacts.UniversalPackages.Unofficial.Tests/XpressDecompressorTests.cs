using System.Text;
using AzureArtifacts.UniversalPackages.Unofficial.Dedup;
using AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

public class XpressDecompressorTests
{
    // The Plain LZ77 examples of [MS-XCA] section 3.1

    private static readonly byte[] AbcCompressed = [0xff, 0xff, 0xff, 0x1f, 0x61, 0x62, 0x63, 0x17, 0x00, 0x0f, 0xff, 0x26, 0x01];

    private static readonly byte[] AlphabetCompressed =
        [0x3f, 0x00, 0x00, 0x00, .. Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz")];

    private static byte[] Abc => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abc", 100)));

    [Fact]
    public void Decompress_SpecificationExamples()
    {
        XpressDecompressor.Decompress(AbcCompressed).Should().Equal(Abc);
        XpressDecompressor.Decompress(AbcCompressed, Abc.Length).Should().Equal(Abc);
        XpressDecompressor.Decompress(AlphabetCompressed).Should().Equal(Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz"));
    }

    [Fact]
    public void TestCompressor_ProducesTheSpecificationExamples()
    {
        TestXpressCompressor.Compress(Abc).Should().Equal(AbcCompressed);
        TestXpressCompressor.Compress(Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz")).Should().Equal(AlphabetCompressed);
    }

    public static TheoryData<string, byte[]> RoundTripData() => new()
    {
        { "empty", [] },
        { "one byte", [7] },
        { "three bytes", [1, 2, 3] },
        { "random", Random(70_000, 1) },
        { "text", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i % 37}: {i * 7919 % 101}\n"))) },
        // Matches of every length band: short, shared half byte, byte, 16-bit and 32-bit lengths
        { "match lengths", MatchLengths() },
        { "32 literals exactly", Random(32, 2) },
        { "long run", Enumerable.Repeat((byte)'a', 300_000).ToArray() },
    };

    [Theory]
    [MemberData(nameof(RoundTripData))]
    public void Decompress_RoundTrips(string name, byte[] data)
    {
        var compressed = TestXpressCompressor.Compress(data);

        XpressDecompressor.Decompress(compressed).Should().Equal(data, name);
        XpressDecompressor.Decompress(compressed, data.Length).Should().Equal(data, name);
    }

    [Fact]
    public void Decompress_TruncatedInput_Throws()
    {
        var truncated = () => XpressDecompressor.Decompress(AbcCompressed.Take(9).ToArray());
        var flagsOnly = () => XpressDecompressor.Decompress([0x00, 0x00]);

        truncated.Should().ThrowExactly<InvalidDataException>();
        flagsOnly.Should().ThrowExactly<InvalidDataException>();
    }

    [Fact]
    public void Decompress_MatchBeforeTheStart_Throws()
    {
        // A match first, with offset 1
        var act = () => XpressDecompressor.Decompress([0x00, 0x00, 0x00, 0x80, 0x00, 0x00]);

        act.Should().ThrowExactly<InvalidDataException>();
    }

    [Fact]
    public void Decompress_WrongExpectedSize_Throws()
    {
        var tooLong = () => XpressDecompressor.Decompress(AlphabetCompressed, 30);
        var tooShort = () => XpressDecompressor.Decompress(AbcCompressed, 200);

        tooLong.Should().ThrowExactly<InvalidDataException>();
        tooShort.Should().ThrowExactly<InvalidDataException>();
    }

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] MatchLengths()
    {
        var random = new Random(3);
        var output = new List<byte>();
        foreach (var length in new[] { 3, 4, 9, 10, 11, 17, 24, 25, 26, 100, 279, 280, 281, 1000, 65_000, 65_538, 70_000 })
        {
            // Fresh random bytes, then a repeat of them
            var block = new byte[length];
            random.NextBytes(block);
            output.AddRange(block);
            output.Add(0);
            output.AddRange(block);
            output.Add(1);
        }
        return output.ToArray();
    }
}
