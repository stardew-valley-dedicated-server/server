using System.Globalization;
using System.Text.Json;

namespace Diagnostics;

/// <summary>One installed mod, read from its manifest.json.</summary>
internal sealed record ModInfo(string Name, string UniqueId, string Version, string Author);

/// <summary>
/// Container/OS-level facts the server process itself can't report: boot uptime, disk headroom,
/// installed mods, and crash-log presence. Every method degrades to null/empty rather than throwing.
/// </summary>
internal static class HostInspector
{
    /// <summary>
    /// Container uptime = system uptime minus PID 1's age. PID 1 is the base image's init supervisor,
    /// so its start marks the container boot. Uses /proc/uptime (seconds since boot) and /proc/1/stat
    /// field 22 (PID 1 start, in clock ticks since boot). Null off Linux or if unreadable.
    /// </summary>
    public static TimeSpan? ContainerUptime()
    {
        try
        {
            var seconds = double.Parse(
                File.ReadAllText("/proc/uptime").Split(' ')[0],
                CultureInfo.InvariantCulture
            );

            var stat = File.ReadAllText("/proc/1/stat");
            // Skip the comm field (parenthesized, may contain spaces) before splitting on spaces.
            var fields = stat.Substring(stat.LastIndexOf(')') + 1).Trim().Split(' ');
            // Field 22 overall = index 19 after the comm field (fields 3.. map to index 0..).
            var startTicks = long.Parse(fields[19], CultureInfo.InvariantCulture);
            const double ticksPerSecond = 100.0; // USER_HZ is 100 on all supported kernels.
            var pid1AgeSeconds = seconds - (startTicks / ticksPerSecond);
            return pid1AgeSeconds >= 0 ? TimeSpan.FromSeconds(pid1AgeSeconds) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Usable free space and total size for the volume backing <paramref name="path"/>.</summary>
    public static (long? free, long? total) DiskUsage(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return (null, null);
            }
            // A volume may be its own mount (docker bind/volume), so match the drive whose mount
            // point is the longest prefix of the path — not the root filesystem.
            var drive = DriveInfo
                .GetDrives()
                .Where(d =>
                    d.IsReady && path.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal)
                )
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive == null)
            {
                return (null, null);
            }
            // AvailableFreeSpace (not TotalFreeSpace) is the headroom actually usable by the app —
            // it excludes root-reserved blocks, so it's the honest "how much room is left" number.
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch
        {
            return (null, null);
        }
    }

    public static List<ModInfo> EnumerateMods()
    {
        var result = new List<ModInfo>();
        if (!Directory.Exists(Config.ModsPath))
        {
            return result;
        }
        // Recurse: SMAPI's bundled mods live under /data/Mods/smapi/<Mod>/, so a non-recursive scan
        // would omit them (mirrors SMAPI's own recursive scan).
        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(
                Config.ModsPath,
                "manifest.json",
                SearchOption.AllDirectories
            );
        }
        catch
        {
            return result;
        }
        foreach (var manifest in manifests)
        {
            try
            {
                // SMAPI tolerates comments/trailing commas in manifest.json; match it or a mod with
                // either would be silently dropped from the table.
                using var doc = JsonDocument.Parse(
                    File.ReadAllText(manifest),
                    Json.ManifestOptions
                );
                var root = doc.RootElement;
                result.Add(
                    new ModInfo(
                        Json.Field(root, "Name"),
                        Json.Field(root, "UniqueID"),
                        Json.Field(root, "Version"),
                        Json.Field(root, "Author")
                    )
                );
            }
            catch
            {
                // Tolerate malformed / partial manifests.
            }
        }
        return result;
    }

    /// <summary>The crash log's last-modified time if present, formatted ISO 8601 UTC; else null.</summary>
    public static string? CrashLogModifiedUtc() =>
        File.Exists(Config.CrashLogPath)
            ? File.GetLastWriteTimeUtc(Config.CrashLogPath).ToString("o")
            : null;
}
