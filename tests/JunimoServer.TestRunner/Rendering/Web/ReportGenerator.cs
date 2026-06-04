using System.Security.Cryptography;
using System.Text.Json;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Assembles the self-contained offline bundle of the test-UI SPA: copies the
/// built SPA next to the run's media and injects the final run snapshot as
/// <c>&lt;script id="test-report-data"&gt;</c>, which the SPA bootstraps from in
/// report mode (no WebSocket). Screenshots and videos are copied to a sibling
/// <c>artifacts/</c> directory with relative paths so they resolve over
/// <c>file://</c>.
/// </summary>
public sealed class ReportGenerator
{
    /// <summary>Sibling directory (next to index.html) holding copied media.</summary>
    private const string BundleArtifactsDir = "artifacts";

    private readonly TestRunState _state;
    private readonly string _spaDistPath;
    private readonly string _testResultsPath;

    public ReportGenerator(TestRunState state, string spaDistPath, string testResultsPath)
    {
        _state = state;
        _spaDistPath = spaDistPath;
        _testResultsPath = testResultsPath;
    }

    /// <summary>
    /// Single entrypoint for assembling the offline report bundle, called from
    /// the runner's finally regardless of renderer mode. Resolves the built SPA,
    /// writes the bundle inside <paramref name="runDir"/> (so it rides along in
    /// the uploaded per-run artifact tree), and no-ops with a warning when the
    /// SPA has not been built. Never throws.
    /// </summary>
    public static void TryGenerate(TestRunState state, string runDir)
    {
        try
        {
            var spaDistPath = FindSpaProjectPath() is { } proj ? Path.Combine(proj, "dist") : null;
            if (spaDistPath == null || !File.Exists(Path.Combine(spaDistPath, "index.html")))
            {
                Console.Error.WriteLine(
                    "WARNING: Report generation skipped. SPA dist not found. Run 'make build-test-ui' first.");
                return;
            }

            new ReportGenerator(state, spaDistPath, runDir).Generate();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: Report generation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates the test-UI SPA project dir by walking up from the runner's base
    /// directory (then the cwd) for <c>tests/test-ui/src</c>. Shared with
    /// mock-data export.
    /// </summary>
    public static string? FindSpaProjectPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "test-ui");
            if (Directory.Exists(Path.Combine(candidate, "src")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        var cwdCandidate = Path.GetFullPath("tests/test-ui");
        if (Directory.Exists(Path.Combine(cwdCandidate, "src")))
            return cwdCandidate;

        return null;
    }

    public void Generate()
    {
        var indexPath = Path.Combine(_spaDistPath, "index.html");
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine("WARNING: Report generation skipped. index.html not found in SPA dist.");
            return;
        }

        var reportDir = Path.Combine(_testResultsPath, "report");
        Directory.CreateDirectory(reportDir);

        // Copy the SPA (JS, CSS) next to the bundle.
        CopyAssets(reportDir);

        // Copy screenshots + videos into reportDir/artifacts/ and rewrite the
        // snapshot's paths to that relative location, so both kinds of media
        // resolve over file:// once Kestrel is gone. (The SPA's screenshotSrc
        // passes these relative paths through unchanged in report mode.)
        var snapshotJson = CopyArtifacts(
            _state.ToSnapshotJson(),
            Path.Combine(reportDir, BundleArtifactsDir),
            BundleArtifactsDir,
            _testResultsPath);

        // Inject state JSON using a safe script tag
        // System.Text.Json's default encoder escapes <, >, & (safe against </script> breakout)
        var dataTag = $"<script type=\"application/json\" id=\"test-report-data\">{snapshotJson}</script>";

        var html = File.ReadAllText(indexPath);
        if (html.Contains("</head>"))
        {
            html = html.Replace("</head>", $"{dataTag}\n</head>");
        }
        else
        {
            // Fallback: prepend to body
            html = dataTag + "\n" + html;
        }

        // Fix asset paths to be relative (the bundle is opened over file://).
        // Vite outputs paths like /assets/xxx.js; make them ./assets/xxx.js
        html = html.Replace("src=\"/assets/", "src=\"./assets/");
        html = html.Replace("href=\"/assets/", "href=\"./assets/");

        var reportPath = Path.Combine(reportDir, "index.html");
        File.WriteAllText(reportPath, html);

        Console.Error.WriteLine($"[WebUI] Static report generated: {reportPath}");
    }

    /// <summary>
    /// Exports artifact files (screenshots and videos) to a directory with content-hashed
    /// filenames and rewrites all paths in the snapshot JSON to relative paths.
    /// Used by mock data export for frontend development. Replaces the directory's
    /// contents (mock data is regenerated wholesale each run).
    /// </summary>
    public static string ExportMockArtifacts(string snapshotJson, string mockArtifactsDir, string? testResultsPath = null)
    {
        if (Directory.Exists(mockArtifactsDir))
            Directory.Delete(mockArtifactsDir, recursive: true);

        return CopyArtifacts(
            snapshotJson,
            mockArtifactsDir,
            Path.GetFileName(mockArtifactsDir), // "mock-artifacts"
            testResultsPath);
    }

    /// <summary>
    /// Copies every screenshot/video referenced in the snapshot into
    /// <paramref name="targetDir"/> with a content-hashed filename, then rewrites
    /// each path in the snapshot JSON to <paramref name="relativePrefix"/>/&lt;hash&gt;.&lt;ext&gt;.
    /// The shared mechanism behind both the offline report bundle and mock-data
    /// export — content hashing dedupes identical media across tests.
    /// </summary>
    private static string CopyArtifacts(
        string snapshotJson, string targetDir, string relativePrefix, string? testResultsPath)
    {
        try
        {
            Directory.CreateDirectory(targetDir);

            var paths = CollectArtifactPaths(snapshotJson);
            var replacements = new Dictionary<string, string>();

            foreach (var originalPath in paths)
            {
                var resolvedPath = ResolveArtifactPath(originalPath, testResultsPath);
                if (resolvedPath == null || !File.Exists(resolvedPath))
                {
                    Console.Error.WriteLine($"[WebUI] Warning: Artifact not found: {originalPath}");
                    continue;
                }

                var bytes = File.ReadAllBytes(resolvedPath);
                var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
                var ext = Path.GetExtension(resolvedPath).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var fileName = $"{hash}{ext}";

                File.WriteAllBytes(Path.Combine(targetDir, fileName), bytes);
                replacements[originalPath] = $"{relativePrefix}/{fileName}";
            }

            foreach (var (originalPath, newPath) in replacements)
            {
                // Replace full JSON string values (e.g. "screenshotPath": "D:\\path")
                snapshotJson = snapshotJson.Replace(
                    JsonSerializer.Serialize(originalPath),
                    JsonSerializer.Serialize(newPath));

                // Also replace unquoted occurrences within larger JSON strings
                // (paths may appear as substrings in other serialized values)
                var escapedOld = JsonSerializer.Serialize(originalPath)[1..^1];
                var escapedNew = JsonSerializer.Serialize(newPath)[1..^1];
                snapshotJson = snapshotJson.Replace(escapedOld, escapedNew);
            }

            Console.Error.WriteLine($"[WebUI] Exported {replacements.Count} artifacts to {targetDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebUI] Warning: Failed to copy artifacts: {ex.Message}");
        }

        return snapshotJson;
    }

    /// <summary>
    /// Walks the snapshot JSON and collects all unique artifact paths: screenshot fields,
    /// recording paths, and screenshot entries in typed output arrays.
    /// </summary>
    private static HashSet<string> CollectArtifactPaths(string snapshotJson)
    {
        var paths = new HashSet<string>();

        using var doc = JsonDocument.Parse(snapshotJson);
        var root = doc.RootElement;

        // Collect from test objects in collections
        if (root.TryGetProperty("collections", out var collections))
        {
            foreach (var collection in collections.EnumerateArray())
            {
                if (!collection.TryGetProperty("classes", out var classes)) continue;
                foreach (var cls in classes.EnumerateArray())
                {
                    if (!cls.TryGetProperty("tests", out var tests)) continue;
                    foreach (var test in tests.EnumerateArray())
                    {
                        CollectStringProp(test, "screenshotPath", paths);
                        CollectOutputArtifacts(test, paths);
                        CollectRecordings(test, paths);
                    }
                }
            }
        }

        // Collect from instance objects
        if (root.TryGetProperty("instances", out var instances))
        {
            foreach (var instance in instances.EnumerateArray())
                CollectStringProp(instance, "recordingPath", paths);
        }

        return paths;
    }

    private static void CollectStringProp(JsonElement element, string propertyName, HashSet<string> paths)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            if (!string.IsNullOrEmpty(value))
                paths.Add(value);
        }
    }

    /// <summary>
    /// Extracts screenshot paths from the typed output array (entries with type=screenshot).
    /// </summary>
    private static void CollectOutputArtifacts(JsonElement test, HashSet<string> paths)
    {
        if (!test.TryGetProperty("output", out var outputProp) || outputProp.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in outputProp.EnumerateArray())
        {
            if (entry.TryGetProperty("type", out var typeProp)
                && typeProp.GetString() == "screenshot"
                && entry.TryGetProperty("path", out var pathProp)
                && pathProp.ValueKind == JsonValueKind.String)
            {
                var path = pathProp.GetString();
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
        }
    }

    /// <summary>
    /// Extracts recording paths from the recordings array (objects with path field).
    /// </summary>
    private static void CollectRecordings(JsonElement test, HashSet<string> paths)
    {
        if (!test.TryGetProperty("recordings", out var recProp) || recProp.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in recProp.EnumerateArray())
        {
            if (entry.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
            {
                var path = pathProp.GetString();
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
        }
    }

    private static string? ResolveArtifactPath(string artifactPath, string? testResultsPath)
    {
        // Try as-is (absolute paths)
        if (File.Exists(artifactPath))
            return artifactPath;

        if (testResultsPath == null)
            return null;

        // Try relative to TestResults
        var relative = Path.Combine(testResultsPath, artifactPath);
        if (File.Exists(relative))
            return relative;

        return null;
    }

    private void CopyAssets(string reportDir)
    {
        var assetsSource = Path.Combine(_spaDistPath, "assets");
        if (!Directory.Exists(assetsSource))
            return;

        var assetsDest = Path.Combine(reportDir, "assets");
        Directory.CreateDirectory(assetsDest);

        foreach (var file in Directory.GetFiles(assetsSource, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(assetsSource, file);
            var destPath = Path.Combine(assetsDest, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null)
                Directory.CreateDirectory(destDir);
            File.Copy(file, destPath, overwrite: true);
        }
    }
}
