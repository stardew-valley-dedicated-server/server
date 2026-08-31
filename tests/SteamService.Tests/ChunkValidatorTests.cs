using SteamKit2;
using Xunit;

namespace SteamService.Tests;

public class ChunkValidatorTests
{
    private static readonly byte[] ChunkA = Bytes(0x10, 64);
    private static readonly byte[] ChunkB = Bytes(0x20, 64);
    private static readonly byte[] ChunkC = Bytes(0x30, 32);

    [Fact]
    public void AdlerHash_IsZeroSeededAdler32()
    {
        // Steam seeds a=0 rather than RFC 1950's a=1, so "Wikipedia" hashes to
        // 0x11E60398 minus 1 in the low half and minus 9 (one per byte) in the high half.
        using var stream = new MemoryStream("Wikipedia"u8.ToArray());

        Assert.Equal(0x11E60398u - 1 - (9u << 16), ChunkValidator.AdlerHash(stream, 9));
    }

    [Fact]
    public void AdlerHash_ReadsOnlyRequestedLength()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6]);

        var hash = ChunkValidator.AdlerHash(stream, 3);

        Assert.Equal(3, stream.Position);
        Assert.Equal(ReferenceAdler32([1, 2, 3]), hash);
    }

    [Fact]
    public void IntactFile_FlagsNoChunks()
    {
        var (stream, chunks) = BuildFile(ChunkA, ChunkB, ChunkC);

        Assert.Empty(ChunkValidator.ValidateFileChunks(stream, chunks));
    }

    [Fact]
    public void CorruptedChunk_FlagsOnlyThatChunk()
    {
        var (stream, chunks) = BuildFile(ChunkA, ChunkB, ChunkC);
        // Zero the first bytes of the file, as a truncated/corrupt XNB header would.
        var buffer = stream.GetBuffer();
        Array.Clear(buffer, 0, 8);

        var invalid = ChunkValidator.ValidateFileChunks(stream, chunks);

        Assert.Single(invalid);
        Assert.Equal(0ul, invalid[0].Offset);
    }

    [Fact]
    public void ChunksGivenOutOfOrder_AreValidatedByOffset()
    {
        var (stream, chunks) = BuildFile(ChunkA, ChunkB, ChunkC);
        chunks.Reverse();
        stream.GetBuffer()[ChunkA.Length + 3] ^= 0xFF; // inside ChunkB

        var invalid = ChunkValidator.ValidateFileChunks(stream, chunks);

        Assert.Single(invalid);
        Assert.Equal((ulong)ChunkA.Length, invalid[0].Offset);
    }

    [Fact]
    public void TruncatedFile_FlagsChunksPastTheEnd()
    {
        var (stream, chunks) = BuildFile(ChunkA, ChunkB, ChunkC);
        stream.SetLength(ChunkA.Length + 10); // cuts ChunkB short, drops ChunkC entirely

        var invalid = ChunkValidator.ValidateFileChunks(stream, chunks);

        Assert.Equal(new ulong[] { 64, 128 }, invalid.Select(c => c.Offset));
    }

    [Fact]
    public void AllChunksCorrupted_FlagsEveryChunkInOffsetOrder()
    {
        var (stream, chunks) = BuildFile(ChunkA, ChunkB, ChunkC);
        Array.Clear(stream.GetBuffer());

        var invalid = ChunkValidator.ValidateFileChunks(stream, chunks);

        Assert.Equal(new ulong[] { 0, 64, 128 }, invalid.Select(c => c.Offset));
    }

    /// <summary>
    /// Lays the chunks out back-to-back and builds matching manifest records.
    /// Checksums come from <see cref="ReferenceAdler32"/>, not the code under test;
    /// the seed it assumes is pinned by <see cref="AdlerHash_IsZeroSeededAdler32"/>.
    /// </summary>
    private static (MemoryStream Stream, List<DepotManifest.ChunkData> Chunks) BuildFile(
        params byte[][] chunkBytes
    )
    {
        var stream = new MemoryStream();
        var chunks = new List<DepotManifest.ChunkData>();
        foreach (var bytes in chunkBytes)
        {
            chunks.Add(
                new DepotManifest.ChunkData(
                    id: [],
                    checksum: ReferenceAdler32(bytes),
                    offset: (ulong)stream.Position,
                    comp_length: (uint)bytes.Length,
                    uncomp_length: (uint)bytes.Length
                )
            );
            stream.Write(bytes);
        }
        return (stream, chunks);
    }

    private static uint ReferenceAdler32(byte[] data)
    {
        uint a = 0,
            b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static byte[] Bytes(byte seed, int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();
}
