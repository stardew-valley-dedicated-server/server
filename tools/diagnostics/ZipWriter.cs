using System.IO.Compression;

namespace Diagnostics;

/// <summary>Bundles the report and the server's logs into a single timestamped zip on the host.</summary>
internal static class ZipWriter
{
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

    private static void AddIfExists(ZipArchive archive, string path, string entryName)
    {
        try
        {
            if (File.Exists(path))
            {
                archive.CreateEntryFromFile(path, entryName);
            }
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
