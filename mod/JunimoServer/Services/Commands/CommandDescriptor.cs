using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace JunimoServer.Services.Commands;

/// <summary>
/// Declarative grammar for one of our console commands: name, description, subcommands, and
/// per-subcommand flags. Commands register via
/// <see cref="CommandDescriptorRegistry.Register(ICommandHelper, CommandDescriptor, Action{string, string[]})"/>,
/// which records the descriptor and adds the SMAPI command in one call, so the attach-cli TAB
/// completion (<see cref="Util.CommandCatalogFile"/>) and the command's help output share one source.
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

    /// <summary>
    /// The one way to register one of our console commands: records the descriptor for the
    /// completion catalog and adds the SMAPI command from the same data, so neither half can
    /// be forgotten or drift from the other. Named Register (not Add) on purpose — an Add
    /// extension with SMAPI's (name, description, callback) signature would lose overload
    /// resolution to the instance method and silently skip the registry.
    /// </summary>
    public static void Register(
        this ICommandHelper commands,
        CommandDescriptor descriptor,
        Action<string, string[]> callback
    )
    {
        Descriptors.Add(descriptor);
        commands.Add(descriptor.Name, descriptor.Description, callback);
    }

    /// <summary>Convenience for commands with no subcommands (name-only completion).</summary>
    public static void Register(
        this ICommandHelper commands,
        string name,
        string description,
        Action<string, string[]> callback
    )
    {
        commands.Register(
            new CommandDescriptor { Name = name, Description = description },
            callback
        );
    }
}
