using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using JunimoServer.Tests.Helpers;

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
    private readonly IReadOnlyCollection<string> _knownSecrets;

    public ReportGenerator(TestRunState state, string spaDistPath, string testResultsPath,
        IReadOnlyCollection<string> knownSecrets)
    {
        _state = state;
        _spaDistPath = spaDistPath;
        _testResultsPath = testResultsPath;
        _knownSecrets = knownSecrets;
    }

    /// <summary>
    /// Single entrypoint for assembling the offline report bundle, called from
    /// the runner's finally regardless of renderer mode. Resolves the built SPA,
    /// writes the bundle inside <paramref name="runDir"/> (so it rides along in
    /// the uploaded per-run artifact tree), and no-ops with a warning when the
    /// SPA has not been built. <paramref name="knownSecrets"/> are masked out of the
    /// published snapshot (see <see cref="ReportRedactor"/>). Never throws.
    /// </summary>
    public static void TryGenerate(TestRunState state, string runDir, IReadOnlyCollection<string> knownSecrets)
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

            new ReportGenerator(state, spaDistPath, runDir, knownSecrets).Generate();
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

    // Assumes the SPA index.html exists — TryGenerate (the only caller) verifies it.
    private void Generate()
    {
        var indexPath = Path.Combine(_spaDistPath, "index.html");
        var reportDir = Path.Combine(_testResultsPath, "report");
        Directory.CreateDirectory(reportDir);

        // Copy the SPA (JS, CSS) next to the bundle.
        CopyAssets(reportDir);

        // Root-level dist statics (favicon) aren't under dist/assets/, so CopyAssets
        // misses them — copy the ones the report references explicitly.
        CopyRootStatic(reportDir, "logo.svg");

        // Generate the link-preview og:image alongside index.html. Best-effort: a
        // failure leaves the og:image sentinel pointing at a missing file, which
        // degrades the unfurl to text-only rather than breaking the bundle.
        var summary = _state.GetRunSummary();
        try
        {
            File.WriteAllBytes(Path.Combine(reportDir, "og-image.png"), OgImageGenerator.Render(summary));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebUI] Warning: OG image generation failed: {ex.Message}");
        }

        // Copy screenshots + videos into reportDir/artifacts/ and rewrite the
        // snapshot's paths to that relative location, so both kinds of media
        // resolve over file:// once Kestrel is gone. (The SPA's screenshotSrc
        // passes these relative paths through unchanged in report mode.)
        var snapshotJson = CopyArtifacts(
            _state.ToSnapshotJson(),
            Path.Combine(reportDir, BundleArtifactsDir),
            BundleArtifactsDir,
            _testResultsPath);

        // Redact secrets/infra before the snapshot is published to the public report.
        // Runs after the media-path rewrite so the artifacts/<hash> paths are preserved.
        snapshotJson = ReportRedactor.Scrub(snapshotJson, _knownSecrets);

        // Fail closed: if redaction somehow produced invalid JSON, do NOT publish the
        // bundle (an unredacted fallback could leak). Masked output can't break JSON by
        // construction, so this is a backstop that should never trip.
        try { using var _ = JsonDocument.Parse(snapshotJson); }
        catch (JsonException ex)
        {
            Console.Error.WriteLine(
                $"WARNING: Report generation aborted — redacted snapshot is not valid JSON ({ex.Message}). Not publishing.");
            return;
        }

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

        html = ReplaceMetaBlock(html, summary);

        var reportPath = Path.Combine(reportDir, "index.html");
        File.WriteAllText(reportPath, html);

        Console.Error.WriteLine($"[WebUI] Static report generated: {PathDisplay.ScrubMessage(reportPath)}");
    }

    private void CopyRootStatic(string reportDir, string fileName)
    {
        var src = Path.Combine(_spaDistPath, fileName);
        if (File.Exists(src))
            File.Copy(src, Path.Combine(reportDir, fileName), overwrite: true);
    }

    // Replaces the source index.html's <!-- META:BEGIN/END --> block with per-run
    // tags. The absolute __OG_URL__/__OG_IMAGE__ sentinels stay literal here and
    // are filled by the R2 publish step (only it knows the public URL).
    private static string ReplaceMetaBlock(string html, RunSummary summary)
    {
        const string begin = "<!-- META:BEGIN -->";
        const string end = "<!-- META:END -->";
        if (!html.Contains(begin) || !html.Contains(end))
        {
            Console.Error.WriteLine("[WebUI] Warning: META marker block not found — meta tags left as built defaults.");
            return html;
        }
        // MatchEvaluator (not a replacement string) so a `$` in the meta HTML
        // — e.g. from a git branch/sha — isn't parsed as a regex group token.
        var tags = BuildMetaTags(summary);
        return Regex.Replace(html, $"{Regex.Escape(begin)}.*?{Regex.Escape(end)}",
            _ => tags, RegexOptions.Singleline);
    }

    private static string BuildMetaTags(RunSummary s)
    {
        var title = Enc(BuildTitle(s));
        var desc = Enc(BuildDescription(s));
        var themeColor = s.Status == "aborted" ? "#6b7280" : s.Failed > 0 ? "#dc2626" : "#16a34a";
        return $"""
        <!-- META:BEGIN -->
        <title>{title}</title>
        <meta name="description" content="{desc}" />
        <meta name="theme-color" content="{themeColor}" />
        <meta name="robots" content="noindex, nofollow" />
        <link rel="canonical" href="__OG_URL__" />
        <meta property="og:type" content="website" />
        <meta property="og:site_name" content="SDVD E2E Test Report" />
        <meta property="og:locale" content="en_US" />
        <meta property="og:title" content="{title}" />
        <meta property="og:description" content="{desc}" />
        <meta property="og:url" content="__OG_URL__" />
        <meta property="og:image" content="__OG_IMAGE__" />
        <meta property="og:image:width" content="1200" />
        <meta property="og:image:height" content="630" />
        <meta property="og:image:alt" content="{desc}" />
        <meta name="twitter:card" content="summary_large_image" />
        <meta name="twitter:title" content="{title}" />
        <meta name="twitter:description" content="{desc}" />
        <meta name="twitter:image" content="__OG_IMAGE__" />
        <meta name="twitter:image:alt" content="{desc}" />
        <!-- META:END -->
        """;
    }

    private static string BuildTitle(RunSummary s)
    {
        var git = GitSuffix(s);
        if (s.Status == "aborted") return $"⚪ Run aborted{git}";
        var icon = s.Failed > 0 ? "❌" : "✅";
        return s.Failed > 0
            ? $"{icon} {s.Failed} failed of {s.TotalTests}{git}"
            : $"{icon} {s.Passed} passed · 0 failed{git}";
    }

    private static string BuildDescription(RunSummary s)
    {
        var parts = new List<string> { $"{s.Passed} passed", $"{s.Failed} failed" };
        if (s.Skipped > 0) parts.Add($"{s.Skipped} skipped");
        if (s.Canceled > 0) parts.Add($"{s.Canceled} canceled");
        return $"{string.Join(" · ", parts)} of {s.TotalTests} tests{GitSuffix(s)}";
    }

    private static string GitSuffix(RunSummary s)
    {
        if (s.GitBranch == null && s.GitSha == null) return "";
        var sha = s.GitSha is { Length: >= 7 } ? s.GitSha[..7] : s.GitSha;
        var branch = s.GitBranch ?? "?";
        return sha != null ? $" — {branch} @ {sha}" : $" — {branch}";
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

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
                    Console.Error.WriteLine($"[WebUI] Warning: Artifact not found: {PathDisplay.ScrubMessage(originalPath)}");
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

            Console.Error.WriteLine($"[WebUI] Exported {replacements.Count} artifacts to {PathDisplay.ScrubMessage(targetDir)}");
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
