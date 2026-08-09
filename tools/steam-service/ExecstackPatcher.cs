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
                Logger.Log($"{logPrefix} WARN: could not patch {lib}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Clears PF_X on the PT_GNU_STACK program header of an ELF64 little-endian binary.
    /// Returns true if the file was modified, false if the flag was already clear.
    /// </summary>
    private static bool TryClearExecstack(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        using var reader = new BinaryReader(fs);

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

        fs.Seek(0x20, SeekOrigin.Begin);
        var phOff = reader.ReadInt64();
        fs.Seek(0x36, SeekOrigin.Begin);
        var phEntSize = reader.ReadUInt16();
        var phNum = reader.ReadUInt16();

        for (var i = 0; i < phNum; i++)
        {
            var entryOffset = phOff + (long)i * phEntSize;
            fs.Seek(entryOffset, SeekOrigin.Begin);
            var pType = reader.ReadUInt32();
            var pFlags = reader.ReadUInt32();
            if (pType != PtGnuStack)
            {
                continue;
            }

            if ((pFlags & PfExec) == 0)
            {
                return false;
            }

            fs.Seek(entryOffset + 4, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(pFlags & ~PfExec));
            return true;
        }

        return false;
    }
}
