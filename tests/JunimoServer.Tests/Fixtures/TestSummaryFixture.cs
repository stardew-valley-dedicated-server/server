using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Helpers;
using Spectre.Console;
using Xunit;
using Xunit.Extensions.AssemblyFixture;

[assembly: TestFramework(AssemblyFixtureFramework.TypeName, AssemblyFixtureFramework.AssemblyName)]
[assembly: TestCollectionOrderer(
    "JunimoServer.Tests.Fixtures.TestCollectionOrderer",
    "JunimoServer.Tests")]

namespace JunimoServer.Tests.Fixtures;

/// <summary>
/// Assembly-level fixture that tracks test execution across ALL collections
/// and prints a unified summary when all tests complete.
///
/// Uses a static singleton pattern so collection fixtures can register tests.
/// The assembly fixture lifecycle guarantees:
/// - Instance is created before any collection fixtures
/// - Instance is disposed after all collection fixtures are disposed
/// </summary>
public class TestSummaryFixture : IAsyncLifetime
{
    // Static singleton for collection fixtures to access
    private static TestSummaryFixture? _instance;
    private static readonly object _instanceLock = new();

    /// <summary>
    /// Gets the current instance. Returns null if assembly fixture not yet initialized.
    /// Collection fixtures should handle null gracefully.
    /// </summary>
    public static TestSummaryFixture? Instance
    {
        get { lock (_instanceLock) { return _instance; } }
    }

    // Test output formatting
    private static readonly bool UseIcons = !string.Equals(
        Environment.GetEnvironmentVariable("SDVD_TEST_ICONS"), "false", StringComparison.OrdinalIgnoreCase);
    private static readonly string IconSuccess = UseIcons ? "✓" : "[OK]";
    private static readonly string IconError = UseIcons ? "✗" : "[ERROR]";
    private const string BlankLine = "\u200B";

    // Test run timing
    private DateTime _testRunStartTime;

    // Test tracking: Collection -> Class -> Test records
    private readonly Dictionary<string, Dictionary<string, List<TestRecord>>> _testsByCollection = new();
    private readonly object _testLock = new();

    /// <summary>
    /// Tracks a single test's name, duration, and failure info.
    /// </summary>
    private class TestRecord
    {
        public string Name { get; }
        public TimeSpan? Duration { get; set; }
        public bool Failed { get; set; }
        public string? Error { get; set; }
        public string? Phase { get; set; }
        public string? ScreenshotPath { get; set; }

        public TestRecord(string name) => Name = name;
    }

    // Abort state (aggregated from all fixtures)
    private volatile bool _testRunAborted;
    private string? _abortReason;
    private readonly object _abortLock = new();

    /// <summary>
    /// Returns true if any collection was aborted.
    /// </summary>
    public bool IsTestRunAborted => _testRunAborted;

    /// <summary>
    /// Gets the abort reason if any.
    /// </summary>
    public string? AbortReason => _abortReason;

    /// <summary>
    /// Marks the test run as aborted with the given reason.
    /// Only the first abort reason is recorded.
    /// </summary>
    public void SetAborted(string reason)
    {
        lock (_abortLock)
        {
            if (_testRunAborted) return;
            _testRunAborted = true;
            _abortReason = reason;
        }
    }

    /// <summary>
    /// Registers a test execution, grouped by collection and class name.
    /// </summary>
    public void RegisterTest(string collectionName, string className, string? testName = null)
    {
        lock (_testLock)
        {
            if (!_testsByCollection.TryGetValue(collectionName, out var testsByClass))
            {
                testsByClass = new Dictionary<string, List<TestRecord>>();
                _testsByCollection[collectionName] = testsByClass;
            }

            if (!testsByClass.TryGetValue(className, out var tests))
            {
                tests = new List<TestRecord>();
                testsByClass[className] = tests;
            }

            tests.Add(new TestRecord(testName ?? "(unknown)"));
        }
    }

    /// <summary>
    /// Records the duration for a completed test.
    /// </summary>
    public void CompleteTest(string collectionName, string className, string? testName, TimeSpan duration)
    {
        lock (_testLock)
        {
            if (_testsByCollection.TryGetValue(collectionName, out var testsByClass) &&
                testsByClass.TryGetValue(className, out var tests))
            {
                // Find the matching test record (last one with this name, in case of duplicates)
                var name = testName ?? "(unknown)";
                for (var i = tests.Count - 1; i >= 0; i--)
                {
                    if (tests[i].Name == name && tests[i].Duration == null)
                    {
                        tests[i].Duration = duration;
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Records a test failure with optional details.
    /// </summary>
    public void RecordFailure(string collectionName, string className, string? testName,
        string error, string? phase = null, string? screenshotPath = null)
    {
        lock (_testLock)
        {
            if (_testsByCollection.TryGetValue(collectionName, out var testsByClass) &&
                testsByClass.TryGetValue(className, out var tests))
            {
                var name = testName ?? "(unknown)";
                for (var i = tests.Count - 1; i >= 0; i--)
                {
                    if (tests[i].Name == name)
                    {
                        tests[i].Failed = true;
                        tests[i].Error = error;
                        tests[i].Phase = phase;
                        tests[i].ScreenshotPath = screenshotPath;
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets the total test count across all collections.
    /// </summary>
    public int TotalTestCount
    {
        get
        {
            lock (_testLock)
            {
                return _testsByCollection.Values
                    .SelectMany(c => c.Values)
                    .Sum(tests => tests.Count);
            }
        }
    }

    public Task InitializeAsync()
    {
        lock (_instanceLock) { _instance = this; }
        _testRunStartTime = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        WriteCtrfReport();
        PrintUnifiedSummary();
        lock (_instanceLock) { _instance = null; }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes test results in CTRF format (https://ctrf.io).
    /// </summary>
    private void WriteCtrfReport()
    {
        var stopTime = DateTime.UtcNow;
        var tests = new List<object>();
        int passed = 0, failed = 0;

        lock (_testLock)
        {
            foreach (var (_, testsByClass) in _testsByCollection)
            {
                foreach (var (className, classTests) in testsByClass)
                {
                    foreach (var test in classTests)
                    {
                        if (test.Failed) failed++; else passed++;

                        var testObj = new Dictionary<string, object?>
                        {
                            ["name"] = test.Name,
                            ["status"] = test.Failed ? "failed" : "passed",
                            ["duration"] = (long)(test.Duration?.TotalMilliseconds ?? 0),
                            ["suite"] = className
                        };

                        if (test.Failed && test.Error != null)
                            testObj["message"] = test.Error;

                        if (test.Phase != null)
                            testObj["extra"] = new Dictionary<string, object> { ["phase"] = test.Phase };

                        if (test.ScreenshotPath != null)
                            testObj["attachments"] = new[] { new { name = "screenshot", path = test.ScreenshotPath, contentType = "image/png" } };

                        tests.Add(testObj);
                    }
                }
            }
        }

        var report = new Dictionary<string, object?>
        {
            ["specVersion"] = "0.0.0",
            ["reportFormat"] = "CTRF",
            ["timestamp"] = _testRunStartTime.ToString("o"),
            ["results"] = new Dictionary<string, object?>
            {
                ["tool"] = new { name = "xunit" },
                ["summary"] = new
                {
                    tests = passed + failed,
                    passed,
                    failed,
                    pending = 0,
                    skipped = 0,
                    other = 0,
                    start = new DateTimeOffset(_testRunStartTime).ToUnixTimeMilliseconds(),
                    stop = new DateTimeOffset(stopTime).ToUnixTimeMilliseconds()
                },
                ["tests"] = tests,
                ["extra"] = _testRunAborted ? new { aborted = true, abortReason = _abortReason } : null
            }
        };

        try
        {
            Directory.CreateDirectory(TestArtifacts.OutputDir);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(Path.Combine(TestArtifacts.OutputDir, "ctrf-report.json"), json);
        }
        catch { /* Don't fail tests due to report generation */ }
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration == null) return "";
        return duration.Value.TotalSeconds >= 60
            ? $"{duration.Value.TotalMinutes:F1}m"
            : $"{duration.Value.TotalSeconds:F1}s";
    }

    private static string DurationColor(TimeSpan? duration)
    {
        if (duration == null) return "grey";
        return duration.Value.TotalSeconds switch
        {
            < 5 => "green",
            < 15 => "yellow",
            _ => "red"
        };
    }

    private void PrintUnifiedSummary()
    {
        var totalDuration = DateTime.UtcNow - _testRunStartTime;

        Console.Out.WriteLine(BlankLine);
        Console.Out.WriteLine(BlankLine);

        var statusIcon = _testRunAborted ? IconError : IconSuccess;
        var statusColor = _testRunAborted ? "red" : "green";
        var statusText = _testRunAborted ? "Aborted" : "Passed";

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(_testRunAborted ? Color.Red : Color.Green)
            .Title($"[bold {statusColor}]{statusIcon} Test Run {statusText}[/]")
            .AddColumn(new TableColumn("[white bold]Test[/]").LeftAligned())
            .AddColumn(new TableColumn("[white bold]Count[/]").RightAligned())
            .AddColumn(new TableColumn("[white bold]Duration[/]").RightAligned())
            .Expand();

        lock (_testLock)
        {
            foreach (var (collectionName, testsByClass) in _testsByCollection.OrderBy(x => x.Key))
            {
                var collectionTotal = testsByClass.Values.Sum(t => t.Count);
                var collectionDuration = testsByClass.Values
                    .SelectMany(t => t)
                    .Where(t => t.Duration != null)
                    .Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration!.Value);

                // Collection header
                table.AddRow(
                    new Markup($"[bold cyan]{Markup.Escape(collectionName)}[/]"),
                    new Markup($"[bold cyan]{collectionTotal}[/]"),
                    new Markup($"[bold cyan]{FormatDuration(collectionDuration)}[/]"));

                // Classes within collection
                foreach (var (className, tests) in testsByClass.OrderBy(x => x.Key))
                {
                    var displayName = className.EndsWith("Tests")
                        ? className[..^5]
                        : className;
                    var classDuration = tests
                        .Where(t => t.Duration != null)
                        .Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration!.Value);

                    table.AddRow(
                        new Markup($"  [white]{Markup.Escape(displayName)}[/]"),
                        new Markup($"[grey]{tests.Count}[/]"),
                        new Markup($"[grey]{FormatDuration(classDuration)}[/]"));

                    // Individual tests
                    foreach (var test in tests)
                    {
                        var methodName = test.Name.Contains('.')
                            ? test.Name[(test.Name.LastIndexOf('.') + 1)..]
                            : test.Name;
                        var color = DurationColor(test.Duration);
                        var durationStr = FormatDuration(test.Duration);
                        table.AddRow(
                            new Markup($"    [dim]{Markup.Escape(methodName)}[/]"),
                            new Markup(""),
                            new Markup(durationStr.Length > 0 ? $"[{color}]{durationStr}[/]" : ""));
                    }
                }

                table.AddEmptyRow();
            }
        }

        // Totals
        table.AddRow(
            new Markup("[white bold]Total[/]"),
            new Markup($"[bold cyan]{TotalTestCount}[/]"),
            new Markup($"[bold cyan]{FormatDuration(totalDuration)}[/]"));

        if (_testRunAborted && !string.IsNullOrEmpty(_abortReason))
        {
            var reason = _abortReason.Length > 60
                ? _abortReason[..57] + "..."
                : _abortReason;
            table.AddRow(
                new Markup("[white]Abort Reason[/]"),
                new Markup($"[red]{Markup.Escape(reason)}[/]"),
                new Markup(""));
        }

        AnsiConsole.Write(table);
        Console.Out.WriteLine(BlankLine);
        Console.Out.Flush();
    }
}
