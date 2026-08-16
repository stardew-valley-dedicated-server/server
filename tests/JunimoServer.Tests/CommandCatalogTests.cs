using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Verifies the console-command catalog the mod writes to /tmp/server-commands at startup
/// (CommandCatalogFile.cs) — the data source for the attach-cli's TAB completion
/// (server-completion.zsh):
/// <list type="bullet">
/// <item>the descriptor merge: our commands appear as "ours" with their subcommand/flag lines,</item>
/// <item>the SMAPI reflection enumeration: built-ins like `help` appear as "smapi",</item>
/// <item>descriptor coverage: no line carries the mod's own display name as its source — a
/// console command we register WITHOUT a CommandDescriptor falls through to the reflection
/// path and shows up as "JunimoServer", so that source appearing means a descriptor was
/// forgotten,</item>
/// <item>the completion script's parse helpers work against the real catalog in the real
/// image (sourced headlessly — no TTY needed for the pure functions).</item>
/// </list>
///
/// API-only. Never calls GetClientAsync().
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Clients = 0, Artifacts = false)]
public class CommandCatalogTests : TestBase
{
    /// <summary>
    /// Whole scenario in ONE exec (docker exec degrades badly under parallel load; see
    /// .claude/rules/minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md). The catalog
    /// is written during mod Entry, long before the server is leasable, so the wait loop is a
    /// formality. Verdicts ride sentinel-prefixed stdout lines and the script always exits 0.
    /// </summary>
    private const string InContainerScenario = """
        set -u
        i=0
        while [ $i -lt 100 ] && [ ! -f /tmp/server-commands ]; do
            sleep 0.1
            i=$((i + 1))
        done
        if [ ! -f /tmp/server-commands ]; then
            echo "VERDICT:NO_CATALOG"
            exit 0
        fi
        echo "CATALOG_BEGIN"
        cat /tmp/server-commands
        echo "CATALOG_END"
        zsh -c '
            source /opt/base/bin/server-completion.zsh
            echo "NAMES:$(_collect_candidates 0 "" | tr "\n" " ")"
            echo "SUBS:$(_collect_candidates 1 settings "" | tr "\n" " ")"
            echo "FLAGS:$(_collect_candidates 4 saves import x --reload "" | tr "\n" " ")"
        '
        exit 0
        """;

    public CommandCatalogTests() { }

    [Fact]
    public async Task Catalog_ContainsDescriptorsAndSmapiCommands_AndCompletionParsesIt()
    {
        // .WaitAsync because Testcontainers' ExecAsync does not poll the CT mid-exec.
        var result = await Server
            .Container.ExecAsync(new[] { "sh", "-c", InContainerScenario }, TestCt)
            .WaitAsync(TestCt);

        Log($"command-catalog scenario output:\n{result.Stdout}");
        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            Log($"command-catalog scenario stderr:\n{result.Stderr}");
        }

        Assert.True(
            result.ExitCode == DockerExitCodes.Success,
            $"Scenario script must exit 0 (verdicts ride stdout sentinels); got {result.ExitCode}: {result.Stderr}"
        );

        var catalog = ExtractCatalog(result.Stdout);
        Assert.True(
            catalog != null,
            "Catalog file /tmp/server-commands must exist (the mod writes it at Entry, after "
                + "command registration); it was absent"
        );

        // Descriptor-driven entries: header + subcommand/flag lines exactly as declared in
        // each command's CommandDescriptor. A drifted descriptor (or a subcommand added to a
        // dispatch switch without updating its descriptor and this list) fails here.
        string[] expectedLines =
        [
            "settings\tours",
            "settings show",
            "settings newgame\t--confirm",
            "settings validate",
            "settings verbose",
            "saves\tours",
            "saves info",
            "saves import\t--swap-host-to --reload --force-reload",
            "saves reload\t--force",
            "cabins\tours",
            "cabins add",
            "rendering\tours",
            "rendering status",
            "farmhand\tours",
            "farmhand release",
            "farmhand rebind",
            "invitecode\tours",
            "info\tours",
            "host-auto\tours",
            "host-visibility\tours",
        ];
        foreach (var expected in expectedLines)
        {
            Assert.True(
                catalog!.Contains(expected + "\n"),
                $"Catalog must contain the descriptor line '{expected.Replace("\t", "<TAB>")}'; "
                    + $"catalog was:\n{catalog}"
            );
        }

        Assert.True(
            catalog!.Contains("help\tsmapi\n"),
            $"Catalog must list the SMAPI built-in 'help' with source 'smapi' (the reflection "
                + $"enumeration over SCore.CommandManager); catalog was:\n{catalog}"
        );

        // Parity gate: a console command registered by our mod WITHOUT a descriptor is not
        // deduplicated by the descriptor merge, so the reflection path emits it with our mod's
        // display name as its source. Any such line means a CommandDescriptor was forgotten.
        foreach (var line in catalog!.Split('\n'))
        {
            Assert.True(
                !line.EndsWith("\tJunimoServer", StringComparison.Ordinal),
                $"Catalog line '{line.Replace("\t", "<TAB>")}' carries the mod's display name "
                    + "as its source — a console command was registered without a "
                    + "CommandDescriptor (add one beside its ConsoleCommands.Add call)"
            );
        }

        // The completion script's parse helpers, run against the real catalog in the image.
        var names = ExtractLine(result.Stdout, "NAMES:");
        Assert.True(
            names != null
                && names.Contains("settings")
                && names.Contains("help")
                && names.Contains("cli"),
            $"Word-0 candidates must include 'settings', 'help', and the 'cli' pseudo-command; got '{names}'"
        );

        var subs = ExtractLine(result.Stdout, "SUBS:");
        Assert.True(
            subs == "show newgame validate verbose",
            $"Word-1 candidates for 'settings' must be its four subcommands in catalog order; got '{subs}'"
        );

        var flags = ExtractLine(result.Stdout, "FLAGS:");
        Assert.True(
            flags == "--swap-host-to --force-reload",
            $"Flag candidates for 'saves import … --reload' must omit the already-typed "
                + $"--reload; got '{flags}'"
        );
    }

    /// <summary>Returns the value after the first line starting with <paramref name="prefix"/>.</summary>
    private static string? ExtractLine(string output, string prefix)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }
        return null;
    }

    /// <summary>Returns the catalog text between the CATALOG_BEGIN/CATALOG_END sentinels.</summary>
    private static string? ExtractCatalog(string output)
    {
        var start = output.IndexOf("CATALOG_BEGIN\n", StringComparison.Ordinal);
        var end = output.IndexOf("CATALOG_END", StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return null;
        }
        start += "CATALOG_BEGIN\n".Length;
        return output[start..end].Replace("\r\n", "\n");
    }
}
