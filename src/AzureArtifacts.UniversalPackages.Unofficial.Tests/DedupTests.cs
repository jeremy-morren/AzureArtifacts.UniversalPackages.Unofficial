using System.Text;
using AzureArtifacts.UniversalPackages.Unofficial.Dedup;
using AzureArtifacts.UniversalPackages.Unofficial.Tests.Fakes;

// ReSharper disable StringLiteralTypo

namespace AzureArtifacts.UniversalPackages.Unofficial.Tests;

public class DedupTests
{
    [Fact]
    public void ContentHash_IsTruncatedSha512()
    {
        // SHA-512("abc"), first 32 bytes (FIPS 180-2 example)
        ContentHash.Compute(Encoding.ASCII.GetBytes("abc"))
            .Should().Be("DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A");
    }

    [Fact]
    public void Identifier_Parse()
    {
        var hash = new string('a', 64);

        var chunk = DedupIdentifier.Parse(hash + "01");
        chunk.IsChunk.Should().BeTrue();
        chunk.Hash.Should().Be(hash.ToUpperInvariant());
        chunk.ToString().Should().Be(hash.ToUpperInvariant() + "01");

        var node = DedupIdentifier.Parse(hash + "02");
        node.IsNode.Should().BeTrue();
        node.Should().NotBe(chunk);
        node.Should().Be(DedupIdentifier.Parse(hash.ToUpperInvariant() + "02"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("ZZAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A01")]
    [InlineData("DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A03")]
    [InlineData("DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A0101")]
    public void Identifier_Parse_Invalid_Throws(string value)
    {
        var act = () => DedupIdentifier.Parse(value);

        act.Should().ThrowExactly<FormatException>();
    }

    [Fact]
    public void Node_Parse_ReadsChildren()
    {
        var blobs = new FakeBlobs();
        var chunk = new FakeBlob(blobs.AddChunk([1, 2, 3]), 3);
        var big = new FakeBlob(blobs.AddChunk(new byte[70_000]), 70_000); // a size needing all 3 bytes
        var inner = new FakeBlob(blobs.AddNode([chunk, big]), 70_003);
        var outer = blobs.AddNode([inner, chunk]);

        var children = DedupNode.Parse(blobs.All[outer]);

        children.Should().Equal(
            new DedupChild(DedupIdentifier.Parse(inner.Id), 70_003),
            new DedupChild(DedupIdentifier.Parse(chunk.Id), 3));
        ContentHash.Compute(blobs.All[outer]).Should().Be(DedupIdentifier.Parse(outer).Hash);
    }

    [Fact]
    public void Node_Parse_Invalid_Throws()
    {
        var blobs = new FakeBlobs();
        var chunk = new FakeBlob(blobs.AddChunk([1]), 1);
        var node = blobs.All[blobs.AddNode([chunk, chunk])];

        byte[][] invalid =
        [
            [0, 0], // too short
            [1, 0, .. node.Skip(2)], // version
            [.. node, 0], // trailing bytes
            node.Take(node.Length - 1).ToArray(), // truncated
            [0, 0, 0, 0, 9, .. new byte[35]] // child type
        ];

        foreach (var data in invalid)
            data.Invoking(DedupNode.Parse).Should().ThrowExactly<InvalidDataException>();
    }
}
