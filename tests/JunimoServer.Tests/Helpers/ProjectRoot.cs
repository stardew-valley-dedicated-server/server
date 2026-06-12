namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Locates the project root directory by walking upward from
/// <see cref="AppContext.BaseDirectory"/> looking for a stable marker
/// (<c>Directory.Build.props</c>, checked into git at the repo root).
///
/// Used as the anchor for resolving project-relative paths (e.g., the
/// <c>sshKey</c> field in <c>SDVD_DOCKER_HOSTS</c>, <c>.env.test</c>) so
/// callers don't depend on <see cref="Directory.GetCurrentDirectory"/> —
/// which moves with how the user invoked the binary (<c>dotnet run</c> can
/// set CWD to the bin output directory).
/// </summary>
public static class ProjectRoot
{
    private const string Marker = "Directory.Build.props";

    private static readonly Lazy<string> _path = new(Find);

    /// <summary>The absolute path to the project root directory.</summary>
    public static string Path => _path.Value;

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against the project root.
    /// Absolute paths are returned unchanged. Tilde-prefixed paths
    /// (<c>~/foo</c>) are expanded against <c>USERPROFILE</c>/<c>HOME</c>
    /// before resolution.
    /// </summary>
    public static string Resolve(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            throw new ArgumentException("Path cannot be null or empty.", nameof(relativePath));

        var expanded = Environment.ExpandEnvironmentVariables(relativePath);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded == "~")
        {
            var home =
                Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME")
                ?? throw new InvalidOperationException(
                    $"Cannot expand '~' in path '{relativePath}': neither USERPROFILE nor HOME is set."
                );
            expanded = expanded.Length == 1 ? home : System.IO.Path.Combine(home, expanded[2..]);
        }

        return System.IO.Path.IsPathRooted(expanded)
            ? System.IO.Path.GetFullPath(expanded)
            : System.IO.Path.GetFullPath(expanded, Path);
    }

    private static string Find()
    {
        // AppContext.BaseDirectory is deterministic regardless of how the
        // process was launched (unlike Directory.GetCurrentDirectory, which
        // depends on the invocation). For tests it's the test-bin dir; for
        // the runner it's the runner-bin dir; both walk up to the same root.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(d.FullName, Marker)))
                return d.FullName;
        }

        throw new InvalidOperationException(
            $"Could not locate project root: walked up from '{AppContext.BaseDirectory}' "
                + $"without finding '{Marker}'. The marker file should exist at the repo root."
        );
    }
}
