namespace SteamService;

/// <summary>
/// Clears the executable-stack marker (PT_GNU_STACK PF_X) on the GOG Galaxy native libraries in a
/// game install. The flag is a vestige of old assembler sources; glibc >= 2.41 (Debian 13) refuses
/// to dlopen any library that carries it, which kills the game's Galaxy init with
/// DllNotFoundException. Clearing the bit at the download layer fixes every consumer (server,
/// test client) without loader workarounds. Only libraries the game loads via dlopen are listed:
/// for those, a cleared flag is strictly better than a refused dlopen, while startup-loaded
/// libraries are out of scope (glibc still honors their flag at process start).
/// </summary>
public static class ExecstackPatcher
{
    // Loaded via DllImport (dlopen) by the game's GalaxyCSharp bindings.
    private static readonly string[] GalaxyLibs = ["libGalaxy64.so", "libGalaxyCSharpGlue.so"];

    private const uint PtGnuStack = 0x6474E551;
    private const uint PfExec = 1;

    /// <summary>Clears the execstack flag on the known Galaxy libraries under <paramref name="gameDir"/>.</summary>
    public static void ClearGalaxyLibs(string gameDir, string logPrefix)
    {
        foreach (var lib in GalaxyLibs)
        {
            var path = Path.Combine(gameDir, lib);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (TryClearExecstack(path))
                {
                    Logger.Log($"{logPrefix} Cleared executable-stack flag on {lib}");
                }
            }
            catch (Exception ex)
            {
                // Deliberately non-fatal: a throw here would keep the download marker from being
                // written (or fail startup), bricking the whole server over a feature-scoped
                // problem. Unpatched libs degrade to "Galaxy init fails, LAN still works", and
                // the startup hook retries the patch on every boot.
                Logger.Log(
                    $"{logPrefix} WARN: could not clear executable-stack flag on {lib} "
                        + $"({ex.Message}) — Galaxy/invite codes will fail on glibc >= 2.41 hosts"
                );
            }
        }
    }

    /// <summary>True for a depot file this patcher rewrites after download.</summary>
    public static bool IsGalaxyLib(string depotFileName) =>
        Array.IndexOf(GalaxyLibs, Path.GetFileName(depotFileName)) >= 0;

    /// <summary>
    /// Clears PF_X on the PT_GNU_STACK program header of an ELF64 little-endian binary.
    /// Returns true if the file was modified, false if the flag was already clear.
    /// </summary>
    private static bool TryClearExecstack(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        if (TryFindExecstackFlagOffset(fs) is not { } flagOffset)
        {
            return false;
        }

        fs.Seek(flagOffset, SeekOrigin.Begin);
        var pFlags = new BinaryReader(fs).ReadUInt32();
        if ((pFlags & PfExec) == 0)
        {
            return false;
        }

        fs.Seek(flagOffset, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(pFlags & ~PfExec));
        return true;
    }

    /// <summary>
    /// File offset of the PT_GNU_STACK <c>p_flags</c> field (little-endian uint32) in an ELF64
    /// little-endian binary, or null when the binary has no such header.
    /// </summary>
    /// <exception cref="InvalidDataException">Not an ELF64 little-endian binary.</exception>
    public static long? TryFindExecstackFlagOffset(Stream elf)
    {
        var reader = new BinaryReader(elf);
        elf.Seek(0, SeekOrigin.Begin);
        var ident = reader.ReadBytes(16);
        if (
            ident.Length < 16
            || ident[0] != 0x7F
            || ident[1] != (byte)'E'
            || ident[2] != (byte)'L'
            || ident[3] != (byte)'F'
            || ident[4] != 2 // ELFCLASS64
            || ident[5] != 1 // little-endian
        )
        {
            throw new InvalidDataException("not an ELF64 little-endian binary");
        }

        elf.Seek(0x20, SeekOrigin.Begin);
        var phOff = reader.ReadInt64();
        elf.Seek(0x36, SeekOrigin.Begin);
        var phEntSize = reader.ReadUInt16();
        var phNum = reader.ReadUInt16();

        for (var i = 0; i < phNum; i++)
        {
            var entryOffset = phOff + (long)i * phEntSize;
            elf.Seek(entryOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() == PtGnuStack)
            {
                return entryOffset + 4;
            }
        }

        return null;
    }

    /// <summary>
    /// Read-only view of a patched library as Steam shipped it: the PF_X bit reads as set again,
    /// so manifest chunk hashes match and the patch is not mistaken for corruption on every
    /// validation pass. Bytes outside the flag are served verbatim, so real damage still shows.
    /// </summary>
    public static Stream AsShippedView(Stream patched, long flagOffset) =>
        new ShippedFlagView(patched, flagOffset);

    private sealed class ShippedFlagView(Stream inner, long flagOffset) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int ReadByte()
        {
            var at = inner.Position;
            var b = inner.ReadByte();
            return b >= 0 && at == flagOffset ? b | (int)PfExec : b;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var start = inner.Position;
            var read = inner.Read(buffer, offset, count);
            var rel = flagOffset - start;
            if (rel >= 0 && rel < read)
            {
                buffer[offset + rel] |= (byte)PfExec;
            }
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
