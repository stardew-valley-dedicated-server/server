using Xunit;

namespace SteamService.Tests;

public class ExecstackPatcherTests
{
    private const uint PtGnuStack = 0x6474E551;
    private const long ProgramHeaderOffset = 64;

    /// <summary>Minimal ELF64 LE image: header + one PT_GNU_STACK program header with the given flags.</summary>
    private static byte[] Elf64WithGnuStack(uint pFlags)
    {
        var elf = new byte[ProgramHeaderOffset + 56 + 16];
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2; // ELFCLASS64
        elf[5] = 1; // little-endian
        BitConverter.GetBytes(ProgramHeaderOffset).CopyTo(elf, 0x20);
        BitConverter.GetBytes((ushort)56).CopyTo(elf, 0x36);
        BitConverter.GetBytes((ushort)1).CopyTo(elf, 0x38);
        BitConverter.GetBytes(PtGnuStack).CopyTo(elf, ProgramHeaderOffset);
        BitConverter.GetBytes(pFlags).CopyTo(elf, ProgramHeaderOffset + 4);
        for (var i = ProgramHeaderOffset + 8; i < elf.Length; i++)
        {
            elf[i] = (byte)(i * 31);
        }
        return elf;
    }

    [Fact]
    public void FindsTheGnuStackFlagField()
    {
        using var elf = new MemoryStream(Elf64WithGnuStack(7));

        Assert.Equal(ProgramHeaderOffset + 4, ExecstackPatcher.TryFindExecstackFlagOffset(elf));
    }

    [Fact]
    public void NoGnuStackHeaderYieldsNull()
    {
        var bytes = Elf64WithGnuStack(7);
        BitConverter.GetBytes(1u).CopyTo(bytes, ProgramHeaderOffset); // PT_LOAD instead
        using var elf = new MemoryStream(bytes);

        Assert.Null(ExecstackPatcher.TryFindExecstackFlagOffset(elf));
    }

    [Fact]
    public void NonElfInputThrows()
    {
        using var junk = new MemoryStream(new byte[64]);

        Assert.Throws<InvalidDataException>(() =>
            ExecstackPatcher.TryFindExecstackFlagOffset(junk)
        );
    }

    [Fact]
    public void ShippedView_HashesAPatchedLibraryAsSteamShippedIt()
    {
        // As shipped: RWE. After the patch: RW. The manifest chunk hash covers the shipped bytes,
        // so a validation pass over the patched file must see the shipped hash — otherwise the
        // patch reads as corruption and the chunk is re-downloaded on every boot.
        var shipped = Elf64WithGnuStack(7);
        var patched = Elf64WithGnuStack(6);
        using var shippedStream = new MemoryStream(shipped);
        using var patchedStream = new MemoryStream(patched);
        var expected = ChunkValidator.AdlerHash(shippedStream, shipped.Length);

        var view = ExecstackPatcher.AsShippedView(patchedStream, ProgramHeaderOffset + 4);
        view.Seek(0, SeekOrigin.Begin);

        Assert.Equal(expected, ChunkValidator.AdlerHash(view, patched.Length));
    }

    [Fact]
    public void ShippedView_LeavesEveryOtherByteAlone_SoRealDamageStillShows()
    {
        var patched = Elf64WithGnuStack(6);
        patched[^1] ^= 0xFF; // damage outside the flag
        var shipped = Elf64WithGnuStack(7);
        using var shippedStream = new MemoryStream(shipped);
        using var patchedStream = new MemoryStream(patched);
        var expected = ChunkValidator.AdlerHash(shippedStream, shipped.Length);

        var view = ExecstackPatcher.AsShippedView(patchedStream, ProgramHeaderOffset + 4);
        view.Seek(0, SeekOrigin.Begin);

        Assert.NotEqual(expected, ChunkValidator.AdlerHash(view, patched.Length));
    }

    [Fact]
    public void ShippedView_BufferedReadAlsoRestoresTheFlag()
    {
        var patched = Elf64WithGnuStack(6);
        using var patchedStream = new MemoryStream(patched);
        var view = ExecstackPatcher.AsShippedView(patchedStream, ProgramHeaderOffset + 4);
        var buffer = new byte[patched.Length];

        view.Seek(0, SeekOrigin.Begin);
        var read = view.Read(buffer, 0, buffer.Length);

        Assert.Equal(patched.Length, read);
        Assert.Equal(7u, BitConverter.ToUInt32(buffer, (int)ProgramHeaderOffset + 4));
    }

    [Theory]
    [InlineData("libGalaxy64.so", true)]
    [InlineData("libGalaxyCSharpGlue.so", true)]
    [InlineData("Stardew Valley.dll", false)]
    public void IsGalaxyLib_MatchesTheRewrittenLibrariesOnly(string depotFile, bool expected)
    {
        Assert.Equal(expected, ExecstackPatcher.IsGalaxyLib(depotFile));
    }
}
