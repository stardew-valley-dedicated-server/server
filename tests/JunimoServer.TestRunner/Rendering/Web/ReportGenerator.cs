using System.Security.Cryptography;
using System.Text.Json;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Generates a self-contained static HTML report by injecting the final test state
/// into the SPA's index.html. Screenshots are base64-inlined for offline viewing.
/// TODO: Should this be renamed to HtmlReportGenerator?
/// </summary>
public sealed class ReportGenerator
{
    private readonly TestRunState _state;
    private readonly string _spaDistPath;
    private readonly string _testResultsPath;

    public ReportGenerator(TestRunState state, string spaDistPath, string testResultsPath)
    {
        _state = state;
        _spaDistPath = spaDistPath;
        _testResultsPath = testResultsPath;
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

        // Copy assets (JS, CSS files)
        CopyAssets(reportDir);

        // Build the report HTML
        var html = File.ReadAllText(indexPath);

        // Get the snapshot and inline screenshots
        var snapshotJson = GetSnapshotWithInlinedScreenshots();

        // Inject state JSON using a safe script tag
        // System.Text.Json's default encoder escapes <, >, & (safe against </script> breakout)
        var dataTag = $"<script type=\"application/json\" id=\"test-report-data\">{snapshotJson}</script>";

        // Insert before closing </head> tag
        if (html.Contains("</head>"))
        {
            html = html.Replace("</head>", $"{dataTag}\n</head>");
        }
        else
        {
            // Fallback: prepend to body
            html = dataTag + "\n" + html;
        }

        // Fix asset paths to be relative (report is served from TestResults/report/)
        // Vite outputs paths like /assets/xxx.js; make them ./assets/xxx.js
        html = html.Replace("src=\"/assets/", "src=\"./assets/");
        html = html.Replace("href=\"/assets/", "href=\"./assets/");

        var reportPath = Path.Combine(reportDir, "index.html");
        File.WriteAllText(reportPath, html);

        Console.Error.WriteLine($"[WebUI] Static report generated: {reportPath}");
    }

    private string GetSnapshotWithInlinedScreenshots()
    {
        var snapshotJson = _state.ToSnapshotJson();
        return InlineScreenshots(snapshotJson, _testResultsPath);
    }

    /// <summary>
    /// Replaces absolute screenshot file paths in snapshot JSON with base64 data URIs,
    /// making the JSON self-contained. Used by report generation for offline viewing.
    /// </summary>
    public static string InlineScreenshots(string snapshotJson, string? testResultsPath = null)
    {
        try
        {
            var paths = CollectArtifactPaths(snapshotJson);

            foreach (var screenshotPath in paths)
            {
                // Only inline image files; videos are too large for base64
                var ext = Path.GetExtension(screenshotPath).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif"))
                    continue;

                var resolvedPath = ResolveArtifactPath(screenshotPath, testResultsPath);
                if (resolvedPath == null || !File.Exists(resolvedPath))
                {
                    Console.Error.WriteLine($"[WebUI] Warning: Screenshot not found: {screenshotPath}");
                    continue;
                }

                var fileInfo = new FileInfo(resolvedPath);
                if (fileInfo.Length > 2 * 1024 * 1024)
                    Console.Error.WriteLine($"[WebUI] Warning: Large screenshot ({fileInfo.Length / 1024}KB): {resolvedPath}");

                var bytes = File.ReadAllBytes(resolvedPath);
                var base64 = Convert.ToBase64String(bytes);
                var mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    _ => "image/png"
                };
                var dataUrl = $"data:{mimeType};base64,{base64}";

                // Replace all occurrences of this path in the JSON with the data URL
                snapshotJson = snapshotJson.Replace(
                    JsonSerializer.Serialize(screenshotPath),
                    JsonSerializer.Serialize(dataUrl));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebUI] Warning: Failed to inline screenshots: {ex.Message}");
        }

        return snapshotJson;
    }

    /// <summary>
    /// Exports artifact files (screenshots and videos) to a directory with content-hashed
    /// filenames and rewrites all paths in the snapshot JSON to relative paths.
    /// Used by mock data export for frontend development.
    /// </summary>
    public static string ExportMockArtifacts(string snapshotJson, string mockArtifactsDir, string? testResultsPath = null)
    {
        try
        {
            if (Directory.Exists(mockArtifactsDir))
                Directory.Delete(mockArtifactsDir, recursive: true);
            Directory.CreateDirectory(mockArtifactsDir);

            var paths = CollectArtifactPaths(snapshotJson);
            var replacements = new Dictionary<string, string>();
            var dirName = Path.GetFileName(mockArtifactsDir); // "mock-artifacts"

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

                File.WriteAllBytes(Path.Combine(mockArtifactsDir, fileName), bytes);
                replacements[originalPath] = $"{dirName}/{fileName}";
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

            Console.Error.WriteLine($"[WebUI] Exported {replacements.Count} artifacts to {mockArtifactsDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebUI] Warning: Failed to export mock artifacts: {ex.Message}");
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
