using SteamKit2;

namespace SteamService;

/// <summary>
/// Chunk-level integrity check for downloaded depot files. Steam validates
/// each chunk's uncompressed bytes with Adler-32, so a local file can be
/// verified against the manifest without contacting the CDN.
/// </summary>
internal static class ChunkValidator
{
    /// <summary>
    /// Returns the chunks whose bytes in <paramref name="stream"/> don't match
    /// their manifest checksum, i.e. the chunks that need to be (re)downloaded.
    /// </summary>
    public static List<DepotManifest.ChunkData> ValidateFileChunks(
        Stream stream,
        IEnumerable<DepotManifest.ChunkData> chunks
    )
    {
        var invalidChunks = new List<DepotManifest.ChunkData>();

        foreach (var chunk in chunks.OrderBy(c => c.Offset))
        {
            stream.Seek((long)chunk.Offset, SeekOrigin.Begin);
            var actualChecksum = AdlerHash(stream, (int)chunk.UncompressedLength);

            if (actualChecksum != chunk.Checksum)
            {
                invalidChunks.Add(chunk);
            }
        }

        return invalidChunks;
    }

    /// <summary>
    /// Adler-32 over the next <paramref name="length"/> bytes of the stream —
    /// the checksum Steam stores per chunk in the depot manifest. A stream that
    /// ends early (truncated file) yields <see cref="uint.MaxValue"/>, which no
    /// Adler-32 can produce, so the chunk is reported as invalid.
    /// </summary>
    public static uint AdlerHash(Stream stream, int length)
    {
        uint a = 0,
            b = 0;
        for (var i = 0; i < length; i++)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                return uint.MaxValue;
            }
            var c = (uint)next;
            a = (a + c) % 65521;
            b = (b + a) % 65521;
        }
        return a | (b << 16);
    }
}
