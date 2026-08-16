using JunimoServer.Tests.Containers;
using Xunit;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Rewrites values inside the in-container server-settings.json. Changes are applied by the
/// next POST /reload (which re-reads the file) — mirroring the real operator flow: edit
/// settings, reload — with no test-only API added. Uses sed (jq is not in the server image);
/// the settings writer emits one line per key, so a keyed substitution is unambiguous.
/// The key is matched case-insensitively via a capture group: the container seeds the file
/// with camelCase keys, but the mod's own writes (game creation, migration commit) rewrite
/// it with PascalCase keys — a fixed-casing pattern would silently no-op (sed exits 0 on
/// zero matches) once the mod has touched the file.
/// </summary>
public static class ServerSettingsFileHelper
{
    public static async Task SwitchCabinStrategyAsync(
        ServerContainer server,
        string strategy,
        CancellationToken ct
    )
    {
        var script =
            $"sed -i 's/\"\\([cC]abinStrategy\\)\": \"[^\"]*\"/\"\\1\": \"{strategy}\"/' {ServerContainer.SettingsPath} "
            + $"&& grep -q '\"{strategy}\"' {ServerContainer.SettingsPath}";
        var result = await server.Container.ExecAsync(new[] { "sh", "-c", script }, ct);
        Assert.True(
            result.ExitCode == DockerExitCodes.Success,
            $"Failed to rewrite settings cabinStrategy to {strategy}: {result.Stderr}"
        );
    }
}
