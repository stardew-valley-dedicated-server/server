using System;
using System.Collections.Generic;
using System.Linq;

namespace JunimoServer.Services.Commands;

/// <summary>
/// Declarative grammar for one of our console commands: name, description, subcommands, and
/// per-subcommand flags. Each command registers its descriptor in <c>Register(...)</c> right
/// beside its <c>helper.ConsoleCommands.Add</c> call, so the attach-cli TAB completion
/// (<see cref="Util.CommandCatalogFile"/>) and the command's help output share one source.
/// Free-form arguments (a save name, an fps value) are deliberately not modeled — completion
/// stays silent where it can't help.
/// </summary>
public sealed class CommandDescriptor
{
    public string Name;
    public string Description;
    public List<SubcommandDescriptor> Subcommands = new();

    /// <summary>Subcommand names joined for usage strings, e.g. "info|import|reload".</summary>
    public string SubcommandNames => string.Join("|", Subcommands.Select(s => s.Name));

    /// <summary>Help lines listing each subcommand with its description.</summary>
    public IEnumerable<string> HelpLines()
    {
        yield return "Available subcommands:";
        foreach (var sub in Subcommands)
        {
            var usage = $"  {Name} {sub.Name}";
            // Pad to a fixed column, but always keep at least one space before the separator.
            yield return usage.PadRight(Math.Max(usage.Length + 1, 22)) + $"-- {sub.Description}";
        }
    }
}

public sealed class SubcommandDescriptor
{
    public string Name;
    public string Description;
    public List<string> Flags = new();
}

/// <summary>
/// Collects the <see cref="CommandDescriptor"/> each of our console commands declares at
/// registration. <see cref="Util.CommandCatalogFile"/> merges these (with subcommands and
/// flags) with the names-only SMAPI enumeration into the completion catalog.
/// </summary>
public static class CommandDescriptorRegistry
{
    private static readonly List<CommandDescriptor> Descriptors = new();

    public static IReadOnlyList<CommandDescriptor> All => Descriptors;

    public static void Add(CommandDescriptor descriptor)
    {
        Descriptors.Add(descriptor);
    }
}
