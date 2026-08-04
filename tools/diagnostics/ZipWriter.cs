using System.IO.Compression;
using System.Text;

namespace Diagnostics;

/// <summary>Bundles the report and the server's logs into a single timestamped zip on the host.</summary>
internal static class ZipWriter
{
    /// <summary>
    /// Per-log cap. Nothing rotates the console typescript (truncated only on container restart), so
    /// long uptime grows it unbounded. The tail keeps the zip attachable and safe to write on a
    /// disk-starved host.
    /// </summary>
    private const long MaxLogBytes = 32L * 1024 * 1024;

    public static string Write(string report)
    {
        Directory.CreateDirectory(Config.OutputDir);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
        var zipPath = Path.Combine(Config.OutputDir, $"state-{timestamp}.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        using (var writer = new StreamWriter(archive.CreateEntry("report.md").Open()))
        {
            writer.Write(report);
        }

        // SMAPI console typescript (carries early boot output; has ANSI escapes).
        AddIfExists(archive, Config.ConsoleLogPath, "server-output.log");

        // SMAPI's canonical structured log (cleaner; what SMAPI's own bug-report guidance asks for).
        AddIfExists(archive, Config.SmapiLogPath, "SMAPI-latest.txt");

        // Crash log: real path first, then a glob fallback under the same root.
        var crashPath = File.Exists(Config.CrashLogPath)
            ? Config.CrashLogPath
            : FindFirst(Config.ConfigRoot, "SMAPI-crash.txt");
        if (crashPath != null)
        {
            AddIfExists(archive, crashPath, "SMAPI-crash.txt");
        }

        return zipPath;
    }

    /// <summary>Adds a log, keeping only its last <see cref="MaxLogBytes"/> and saying so in-band.</summary>
    private static void AddIfExists(ZipArchive archive, string path, string entryName)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            // Shared read: the server is still appending to these files while we copy.
            using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            var entry = archive.CreateEntry(entryName);
            entry.LastWriteTime = File.GetLastWriteTimeUtc(path);
            using var target = entry.Open();

            var skipped = source.Length - MaxLogBytes;
            if (skipped > 0)
            {
                source.Seek(skipped, SeekOrigin.Begin);
                var note =
                    $"[diagnostics] Truncated: kept the last {Format.Bytes(MaxLogBytes)} of "
                    + $"{Format.Bytes(source.Length)}; older output was dropped. The first line "
                    + $"below may start mid-line.\n";
                target.Write(Encoding.UTF8.GetBytes(note));
            }
            source.CopyTo(target);
        }
        catch
        {
            // Optional log; a rotation/permission race between the check and the read must not abort
            // the archive — report.md is already written, so skip this entry and keep the zip.
        }
    }

    private static string? FindFirst(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }
        try
        {
            return Directory
                .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
