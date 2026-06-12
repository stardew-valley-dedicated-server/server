using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.TestRunner.Rendering;

/// <summary>
/// Base class for renderers with common state tracking.
/// </summary>
public abstract class RendererBase : ITestRenderer
{
    private int _passed;
    private int _failed;
    private int _canceled;
    private int _skipped;
    private int _implicitlySkipped;

    /// <summary>
    /// Per-test output accumulated from real-time pipe events.
    /// This is the single source of truth for test output. xUnit's buffered Output is not used.
    /// </summary>
    private readonly ConcurrentDictionary<string, StringBuilder> _testOutputBuffers = new();

    public int PassedCount => _passed;
    public int FailedCount => _failed;
    public int CanceledCount => _canceled;
    public int SkippedCount => _skipped;

    /// <summary>
    /// Count of tests that were never executed and implicitly marked as skipped
    /// when the run finished early (e.g., StopOnFail).
    /// </summary>
    protected int ImplicitlySkippedCount => _implicitlySkipped;

    protected void IncrementImplicitlySkipped() => Interlocked.Increment(ref _implicitlySkipped);

    /// <summary>
    /// Total number of tests discovered via reflection (set by PopulateTests).
    /// </summary>
    protected int TotalDiscovered { get; private set; }

    /// <summary>
    /// Whether verbose output is enabled. Shows detailed setup steps and diagnostics inline.
    /// </summary>
    public bool Verbose { get; }

    protected RendererBase(bool verbose = false)
    {
        Verbose = verbose;
    }

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public abstract void OnDiscoveryComplete(DiscoveryCompleteEvent e);
    public abstract void OnRunStarted(RunStartedEvent e);
    public abstract void OnRunFinished(RunFinishedEvent e);
    public abstract void OnTestStarted(TestStartedEvent e);

    public virtual void OnTestRunning(TestRunningEvent e) { }

    public virtual void OnTestEnrichment(TestEnrichmentEvent e) { }

    public virtual void OnRunMetadata(RunMetadataEvent e) { }

    public virtual void OnFlakyTests(FlakyTestsEvent e) { }

    public virtual void OnTestOutput(TestOutputEvent e)
    {
        _testOutputBuffers.AddOrUpdate(
            e.DisplayName,
            _ => new StringBuilder(e.Line),
            (_, sb) =>
            {
                lock (sb)
                {
                    sb.AppendLine().Append(e.Line);
                }
                return sb;
            }
        );
    }

    public virtual void OnTestAnnotation(TestAnnotationEvent e) { }

    public abstract void OnDiagnostic(DiagnosticEvent e);
    public abstract void OnError(ErrorEvent e);

    // Setup phase events - default implementations do nothing
    public virtual void OnSetupPhaseStarted(SetupPhaseStartedEvent e) { }

    public virtual void OnSetupPhaseCompleted(SetupPhaseCompletedEvent e) { }

    public virtual void OnSetupStep(SetupStepEvent e) { }

    // Screenshot events - default implementation does nothing
    public virtual void OnScreenshotCaptured(ScreenshotCapturedEvent e) { }

    // Recording events - default implementation does nothing
    public virtual void OnRecordingCaptured(RecordingCapturedEvent e) { }

    public virtual void OnRecordingSkipped(RecordingSkippedEvent e) { }

    // VNC URL events - default implementation does nothing
    public virtual void OnVncUrl(VncUrlEvent e) { }

    // Instance lifecycle events - default implementations do nothing
    public virtual void OnInstanceCreated(InstanceCreatedEvent e) { }

    public virtual void OnInstanceLeased(InstanceLeasedEvent e) { }

    public virtual void OnInstanceClientAttached(InstanceClientAttachedEvent e) { }

    public virtual void OnInstanceReturned(InstanceReturnedEvent e) { }

    public virtual void OnInstanceDisposed(InstanceDisposedEvent e) { }

    public virtual void OnInstanceRecording(InstanceRecordingEvent e) { }

    public virtual void OnInstancePoisoned(InstancePoisonedEvent e) { }

    public virtual void OnInstanceConnected(InstanceConnectedEvent e) { }

    public virtual void OnInstanceDisconnected(InstanceDisconnectedEvent e) { }

    public virtual void OnInstanceStats(InstanceStatsEvent e) { }

    public virtual void OnTestPassed(TestPassedEvent e)
    {
        Interlocked.Increment(ref _passed);
        OnTestPassedCore(e);
        _testOutputBuffers.TryRemove(e.DisplayName, out _);
    }

    /// <summary>
    /// Tracks displayNames that <see cref="OnTestFailed"/> classified as canceled
    /// (body-OCE/TCE without further context). Drains in
    /// <see cref="ReclassifyCanceledAsFailed"/> when a later <c>test_enrichment</c>
    /// event tells us the OCE was actually a per-test timeout, not a StopOnFail
    /// cascade. The set guarantees idempotence: a non-OCE failure
    /// (e.g. AssertException) classified directly as failed never lands here, so
    /// a stray enrichment override can't decrement _canceled below zero.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _classifiedAsCanceled = new();

    public virtual void OnTestFailed(TestFailedEvent e)
    {
        var isCanceled =
            e.ExceptionType
            is "System.OperationCanceledException"
                or "System.Threading.Tasks.TaskCanceledException";
        if (isCanceled)
        {
            Interlocked.Increment(ref _canceled);
            _classifiedAsCanceled[e.DisplayName] = 0;
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }
        OnTestFailedCore(e);
        _testOutputBuffers.TryRemove(e.DisplayName, out _);
    }

    /// <summary>
    /// Post-hoc counter sync: when the test process emits a <c>test_enrichment</c>
    /// IPC event reclassifying an OCE-as-canceled into a per-test-timeout
    /// failure, decrement <c>canceled</c> and increment <c>failed</c>. Idempotent
    /// and gated by the <see cref="_classifiedAsCanceled"/> set, so calling for a
    /// test that was never classified as canceled is a no-op (defends CIRenderer's
    /// case where there is no <see cref="Web.TestRunState"/> to consult). Returns
    /// true if the swap happened.
    /// </summary>
    protected bool ReclassifyCanceledAsFailed(string displayName)
    {
        if (!_classifiedAsCanceled.TryRemove(displayName, out _))
        {
            return false;
        }

        Interlocked.Decrement(ref _canceled);
        Interlocked.Increment(ref _failed);
        return true;
    }

    public virtual void OnTestSkipped(TestSkippedEvent e)
    {
        Interlocked.Increment(ref _skipped);
        OnTestSkippedCore(e);
        _testOutputBuffers.TryRemove(e.DisplayName, out _);
    }

    /// <summary>
    /// Retrieve and remove accumulated pipe output for a test.
    /// Must be called within OnTest*Core before the base auto-cleanup.
    /// </summary>
    protected string? GetAccumulatedOutput(string displayName)
    {
        return _testOutputBuffers.TryRemove(displayName, out var sb) ? sb.ToString() : null;
    }

    protected abstract void OnTestPassedCore(TestPassedEvent e);
    protected abstract void OnTestFailedCore(TestFailedEvent e);
    protected abstract void OnTestSkippedCore(TestSkippedEvent e);

    /// <summary>
    /// Populate the test tree with discovered tests. Base implementation tracks TotalDiscovered.
    /// </summary>
    public virtual void PopulateTests(
        IReadOnlyList<(
            string Collection,
            string ClassName,
            string MethodName,
            string DisplayName
        )> tests
    )
    {
        TotalDiscovered = tests.Count;
    }

    /// <summary>
    /// Transforms a gerund step name to past tense for completed steps.
    /// Delegates to <see cref="SetupEventBus.ToPastTense"/> (single source of truth).
    /// </summary>
    protected static string ToPastTense(string stepName) => SetupEventBus.ToPastTense(stepName);

    /// <summary>
    /// Format a duration for display.
    /// Delegates to <see cref="SetupEventBus.FormatDuration"/> (single source of truth).
    /// </summary>
    protected static string FormatDuration(TimeSpan duration) =>
        SetupEventBus.FormatDuration(duration);

    /// <summary>
    /// Extract short class name from full type name.
    /// </summary>
    protected static string GetShortClassName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
    }

    /// <summary>
    /// Extract short test name from display name.
    /// e.g., "JunimoServer.Tests.PasswordTests.Login_Works" -> "Login_Works"
    /// e.g., "Namespace.Class.Method(settingPath: "Game.FarmName")" -> "Method(settingPath: "Game.FarmName")"
    /// Also sanitizes newlines from xUnit Theory serialized parameters.
    /// </summary>
    protected static string GetShortTestName(string displayName)
    {
        var sanitized = SanitizeDisplayName(displayName);

        // For Theory tests, parameters appear after '(' and may contain dots.
        // Split on the last dot *before* the opening parenthesis to avoid
        // splitting inside parameter values like "Game.FarmName".
        var parenIdx = sanitized.IndexOf('(');
        var searchUpTo = parenIdx >= 0 ? parenIdx : sanitized.Length;
        var lastDot = sanitized.LastIndexOf('.', searchUpTo - 1, searchUpTo);

        return lastDot >= 0 ? sanitized[(lastDot + 1)..] : sanitized;
    }

    private static readonly Regex MultipleSpaces = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Sanitize display names by replacing newlines with spaces and collapsing whitespace.
    /// xUnit Theory tests serialize parameters with newlines that break single-line output.
    /// </summary>
    protected static string SanitizeDisplayName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var result = name.Replace("\r\n", " ").Replace("\n", " ");
        return MultipleSpaces.Replace(result, " ");
    }

    // Namespaces to drop entirely from stack traces (test harness noise)
    private static readonly string[] HiddenFrameNamespaces = ["Xunit.", "xunit."];

    // Regex to detect project source paths (frames we want to keep file info for)
    private static readonly Regex ProjectFramePattern = new(
        @"(tests[/\\]|mod[/\\])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Stack frame pattern: "   at Namespace.Class.Method(...) in path/file.cs:line N"
    private static readonly Regex FramePattern = new(
        @"^(?<indent>\s*)at (?<method>.+?)(?<source> in (?<path>.+))?$",
        RegexOptions.Compiled
    );

    private static readonly Regex FilePathPattern = new(@"[^\n]+?\.cs:", RegexOptions.Compiled);

    private static readonly string? s_projectDir = DetectProjectDir();

    /// <summary>
    /// Clean up stack trace: drop harness frames, strip file paths from 3rd party
    /// frames, clean up project paths and normalize delimiters to forward slashes.
    /// </summary>
    public static string SanitizeStackTrace(string stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return stackTrace;
        }

        var lines = stackTrace.Split('\n');
        var filtered = new List<string>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Keep "--- End of stack trace ---" markers
            if (line.TrimStart().StartsWith("---"))
            {
                filtered.Add(line);
                continue;
            }

            var match = FramePattern.Match(line);
            if (!match.Success)
            {
                filtered.Add(line);
                continue;
            }

            var method = match.Groups["method"].Value;

            // Drop frames from hidden namespaces entirely
            if (IsHiddenFrame(method))
            {
                continue;
            }

            var hasSource = match.Groups["source"].Success;
            if (!hasSource)
            {
                filtered.Add(line);
                continue;
            }

            var path = match.Groups["path"].Value;
            if (ProjectFramePattern.IsMatch(path))
            {
                filtered.Add(
                    $"{match.Groups["indent"].Value}at {method} in {CleanProjectPath(path)}"
                );
            }
            else
            {
                filtered.Add($"{match.Groups["indent"].Value}at {method}");
            }
        }

        return string.Join('\n', filtered);
    }

    private static bool IsHiddenFrame(string method)
    {
        foreach (var ns in HiddenFrameNamespaces)
        {
            if (method.StartsWith(ns, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string CleanProjectPath(string path)
    {
        var result = path;

        if (!string.IsNullOrEmpty(s_projectDir))
        {
            var backDir = s_projectDir.Replace('/', '\\').TrimEnd('\\') + "\\";
            var fwdDir = s_projectDir.Replace('\\', '/').TrimEnd('/') + "/";
            result = result.Replace(backDir, "");
            result = result.Replace(fwdDir, "");
        }

        result = FilePathPattern.Replace(result, m => m.Value.Replace('\\', '/'));
        return result;
    }

    private static string? DetectProjectDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && dir != null; i++)
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir;
    }
}
