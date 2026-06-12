using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Fixtures;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Tracks test results across runs for flakiness detection.
/// Appends to <c>TestResults/flakiness.jsonl</c> (root, not per-run).
/// </summary>
public static class FlakinessTracker
{
    /// <summary>
    /// Append target — the cross-run root file <c>TestResults/flakiness.jsonl</c>.
    /// </summary>
    private static string FilePath =>
        Path.Combine(TestArtifacts.OutputDir, RunArtifactNames.FlakinessJsonl);

    // camelCase policy omitted: current entry fields are already lowercase by
    // source spelling, so adding PropertyNamingPolicy = CamelCase would be a
    // no-op for them — but folding into Schema/Json/DiagnosticEmitJson would
    // silently rename future PascalCase record fields and break the on-disk
    // schema that ComputeFlakiness reads back.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Appends one line per test from the current run.
    /// Called from <see cref="TestSummaryFixture.DisposeAsync"/>.
    /// </summary>
    public static void RecordRun(TestSummaryFixture fixture)
    {
        var runId = RunMetadata.RunId;
        if (runId == null)
            return;

        var results = fixture.GetAllTestResults();
        if (results.Count == 0)
            return;

        try
        {
            Directory.CreateDirectory(TestArtifacts.OutputDir);
            var ts = DateTime.UtcNow.ToString("o");

            using var writer = new StreamWriter(FilePath, append: true);
            foreach (var r in results)
            {
                var entry = new
                {
                    ts,
                    runId,
                    test = r.TestName,
                    // Lowercase enum name: "passed" | "failed" | "canceled" (no notDispatched —
                    // never-dispatched tests have no record).
                    result = r.Outcome.ToString().ToLowerInvariant(),
                    // Only set for failed entries. ComputeFlakiness excludes
                    // "infrastructure" failures from the fail rate.
                    failureCategory = r.FailureCategory,
                    durationMs = r.DurationMs,
                    // Breakdown is null for tests that didn't go through TestBase (e.g.
                    // DownloadValidationFixture). Serializer drops nulls via JsonOptions.
                    testBodyMs = r.Breakdown?.TestBodyMs,
                    artifactsMs = r.Breakdown?.ArtifactsMs,
                    cleanupMs = r.Breakdown?.CleanupMs,
                    lastKeepDisposeMs = r.Breakdown?.LastKeepDisposeMs,
                    leaseReleaseMs = r.Breakdown?.LeaseReleaseMs,
                };
                writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FlakinessTracker] Failed to write: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads historical data and returns flaky tests (0% &lt; failRate &lt; 100%)
    /// from the last 20 runs.
    /// </summary>
    public static List<object> ComputeFlakiness()
    {
        if (!File.Exists(FilePath))
            return [];

        try
        {
            var lines = File.ReadAllLines(FilePath);

            var entries =
                new List<(
                    string runId,
                    string test,
                    string result,
                    string? failureCategory,
                    DateTime ts
                )>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var rid = root.GetProperty("runId").GetString() ?? "";
                    var test = root.GetProperty("test").GetString() ?? "";
                    var result = root.GetProperty("result").GetString() ?? "";
                    // Absent on passed/canceled entries and on lines written
                    // before the field existed.
                    var category = root.TryGetProperty("failureCategory", out var cat)
                        ? cat.GetString()
                        : null;
                    var ts = root.GetProperty("ts").GetDateTime();
                    entries.Add((rid, test, result, category, ts));
                }
                catch
                { /* skip malformed lines */
                }
            }

            var recentRuns = entries
                .GroupBy(e => e.runId)
                .OrderByDescending(g => g.Max(e => e.ts))
                .Take(20)
                .Select(g => g.Key)
                .ToHashSet();
            var recentEntries = entries.Where(e => recentRuns.Contains(e.runId)).ToList();

            // Compute per-test failure rates. Only `passed` and `failed` entries
            // count toward the rate; `canceled` is not flakiness evidence, and
            // neither are infrastructure-classified failures (host-disconnect
            // cascade victims would otherwise read as flaky tests).
            var testStats = recentEntries
                .GroupBy(e => e.test)
                .Select(g =>
                {
                    var dispatched = g.Where(e =>
                            e.result == "passed"
                            || (e.result == "failed" && e.failureCategory != "infrastructure")
                        )
                        .ToList();
                    if (dispatched.Count == 0)
                        return null;
                    var failures = dispatched.Count(e => e.result == "failed");
                    var failRate = (double)failures / dispatched.Count;
                    return new
                    {
                        Test = g.Key,
                        FailRate = failRate,
                        RecentRuns = dispatched.Count,
                    };
                })
                .Where(s => s != null && s.FailRate > 0 && s.FailRate < 1.0)
                .Select(s => s!)
                .OrderByDescending(s => s.FailRate)
                .ToList();

            return testStats
                .Select(s =>
                    (object)
                        new Dictionary<string, object>
                        {
                            ["test"] = s.Test,
                            ["failRate"] = Math.Round(s.FailRate, 2),
                            ["recentRuns"] = s.RecentRuns,
                        }
                )
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[FlakinessTracker] Failed to compute flakiness: {ex.Message}"
            );
            return [];
        }
    }
}
