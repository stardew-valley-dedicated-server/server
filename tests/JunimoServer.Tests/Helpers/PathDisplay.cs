namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Rewrites absolute repo-root paths in human-facing log lines to a relative
/// form (<c>.\TestResults\…</c> / <c>./TestResults/…</c>). No-op for paths
/// outside the repo root, and idempotent on already-relativized strings.
///
/// Free text only — structured output (the <c>--llm</c> JSONL stream, event
/// path fields the web UI turns into <c>/artifacts/</c> URLs) must stay
/// absolute and is never routed through here.
/// </summary>
public static class PathDisplay
{
    // OrdinalIgnoreCase on Windows so drive-letter / path-segment case drift
    // still matches; Ordinal on POSIX where the filesystem is case-sensitive.
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly string Replacement = "." + Path.DirectorySeparatorChar;

    // Null if the repo root can't be located: ProjectRoot.Path throws on a missing
    // marker, and a logging path must not throw — null degrades to no-op.
    private static readonly string? _prefix = ResolvePrefix();

    private static string? ResolvePrefix()
    {
        try
        {
            return ProjectRoot.Path + Path.DirectorySeparatorChar;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Single native-separator prefix-replace: repo-root paths in these messages
    /// come from <c>Path.Combine</c> (via <see cref="TestArtifacts"/>), so they
    /// only ever use the native separator — no forward-slash form to also catch.
    /// </summary>
    public static string ScrubMessage(string message)
    {
        if (string.IsNullOrEmpty(message) || _prefix is null)
            return message;

        return message.Replace(_prefix, Replacement, Comparison);
    }
}
