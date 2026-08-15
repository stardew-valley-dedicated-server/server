using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JunimoServer.Services.Commands;
using StardewModdingAPI;

namespace JunimoServer.Util;

/// <summary>
/// Writes the console-command catalog to a file in the container-shared /tmp, where the
/// attach-cli's TAB completion (server-completion.sh) reads it — same file-drop pattern as
/// <see cref="InviteCodeFile"/>. Line format (tab-separated, parsed with plain bash — the
/// images have no jq):
/// <code>
/// name\tsource            one header line per command ("ours", "smapi", or mod display name)
/// name sub\tflag flag     one line per subcommand of our commands (flags optional)
/// </code>
/// </summary>
public static class CommandCatalogFile
{
    private static readonly string FilePath = "/tmp/server-commands";

    /// <summary>
    /// Merges <see cref="CommandDescriptorRegistry"/> (our commands, with subcommands and
    /// flags) with <see cref="SmapiCommandCatalog"/> (all other commands, names only) and
    /// writes the catalog atomically. Call once after all command registration has run; a
    /// write failure never crashes the mod.
    /// </summary>
    public static void Write(IMonitor monitor)
    {
        try
        {
            var content = new StringBuilder();
            var covered = new HashSet<string>(
                CommandDescriptorRegistry.All.Select(d => d.Name),
                StringComparer.OrdinalIgnoreCase
            );

            foreach (var descriptor in CommandDescriptorRegistry.All)
            {
                content.Append(descriptor.Name).Append('\t').Append("ours").Append('\n');
                foreach (var sub in descriptor.Subcommands)
                {
                    content.Append(descriptor.Name).Append(' ').Append(sub.Name);
                    if (sub.Flags.Count > 0)
                    {
                        content.Append('\t').Append(string.Join(" ", sub.Flags));
                    }
                    content.Append('\n');
                }
            }

            foreach (var (name, source) in SmapiCommandCatalog.GetAll(monitor))
            {
                // Skip names already covered by a descriptor (our own commands come back from
                // the SMAPI enumeration too) and anything that would corrupt the line format
                // or the terminal (whitespace breaks the tab format, control chars the pane).
                if (!covered.Add(name) || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                {
                    continue;
                }
                content.Append(name).Append('\t').Append(SanitizeSource(source)).Append('\n');
            }

            var tmpPath = FilePath + ".tmp";
            File.WriteAllText(tmpPath, content.ToString());
            File.Move(tmpPath, FilePath, overwrite: true);
            monitor.Log($"Command catalog written to '{FilePath}'", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            monitor.Log(
                $"Failed to write command catalog to '{FilePath}': {ex.Message}",
                LogLevel.Warn
            );
        }
    }

    /// <summary>Keeps a mod display name on one clean tab-field: tabs/newlines/control
    /// characters become spaces.</summary>
    private static string SanitizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return SmapiCommandCatalog.SmapiSource;
        }

        var sanitized = new StringBuilder(source.Length);
        foreach (var c in source)
        {
            sanitized.Append(char.IsWhiteSpace(c) || char.IsControl(c) ? ' ' : c);
        }
        return sanitized.ToString().Trim();
    }
}
