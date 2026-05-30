using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.TestRunner.Rendering;

/// <summary>
/// Streaming renderer for CI environments with vitest/jest-inspired output.
///
/// Output structure:
///   Header → Setup phases (streamed with spinners) → Test classes (streamed) → Failure details → Summary
///
/// Setup steps and tests show a braille spinner animation on TTY terminals,
/// replaced by ✓/✗ when complete. Non-TTY output uses a static ◌ indicator.
/// On GitHub Actions: groups and ::error:: annotations emitted.
/// </summary>
public sealed class CIRenderer : RendererBase
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly bool _isGitHubActions;
    private readonly bool _useColor;
    private readonly bool _isTTY;

    // Test class tracking
    private string? _currentClassName;
    private bool _currentClassHasGitHubGroup;

    // Failure collection for bottom summary (event + accumulated pipe output)
    private readonly List<(TestFailedEvent Failure, string? Output)> _failures = new();

    // Spinner animation (TTY only)
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private readonly object _writeLock = new();
    private Timer? _spinnerTimer;
    private int _spinnerFrame;
    private string? _spinnerLabel;
    private string _spinnerIndent = "   ";
    private bool _spinnerIsSetup; // true = setup step spinner, false = test spinner
    private int _terminalWidth;

    public CIRenderer(bool verbose = false) : this(Console.Out, Console.Error, verbose) { }

    public CIRenderer(TextWriter stdout, TextWriter stderr, bool verbose = false) : base(verbose)
    {
        _out = stdout;
        _err = stderr;
        _isGitHubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        _useColor = !Console.IsOutputRedirected &&
                     Environment.GetEnvironmentVariable("NO_COLOR") == null;
        _isTTY = !Console.IsOutputRedirected;
        _terminalWidth = _isTTY ? GetTerminalWidth() : 0;
    }

    // ── ANSI helpers ──

    private string Green(string text) => _useColor ? $"\x1b[32m{text}\x1b[0m" : text;
    private string Red(string text) => _useColor ? $"\x1b[31m{text}\x1b[0m" : text;
    private string RedBold(string text) => _useColor ? $"\x1b[1;31m{text}\x1b[0m" : text;
    private string Yellow(string text) => _useColor ? $"\x1b[33m{text}\x1b[0m" : text;
    private string Cyan(string text) => _useColor ? $"\x1b[36m{text}\x1b[0m" : text;
    private string Bold(string text) => _useColor ? $"\x1b[1m{text}\x1b[0m" : text;
    private string Dim(string text) => _useColor ? $"\x1b[2m{text}\x1b[0m" : text;
    private string RedBoldInverse(string text) => _useColor ? $"\x1b[1;7;31m{text}\x1b[0m" : text;

    // ── Run lifecycle ──

    public override void OnRunStarted(RunStartedEvent e)
    {
        _out.WriteLine();
        _out.WriteLine($" {Bold("JunimoServer.Tests")}");
        _out.Flush();
    }

    public override void OnDiscoveryComplete(DiscoveryCompleteEvent e)
    {
        // Suppressed; the header is enough
    }

    public override void OnRunFinished(RunFinishedEvent e)
    {
        // Close the last class group
        EndClassGroup();

        _out.WriteLine();

        // Print failure detail section
        if (_failures.Count > 0)
        {
            var rule = Dim(new string('\u2500', 50));
            _out.WriteLine($" {Dim("\u2500\u2500")} {Bold("Failures")} {rule}");
            _out.WriteLine();

            foreach (var (f, fOutput) in _failures)
            {
                var shortClass = GetShortClassName(f.TestClass);
                var shortTest = GetShortTestName(f.DisplayName);

                _out.WriteLine($" {RedBoldInverse(" FAIL ")} {shortClass} > {shortTest}  {Dim(FormatDuration(f.Duration))}");
                _out.WriteLine();

                // Exception message
                _out.WriteLine($"   {Red($"{f.ExceptionType}: {FirstLine(f.Message)}")}");

                // Additional message lines (e.g. inner exception details)
                var messageLines = f.Message.Split('\n');
                for (var i = 1; i < messageLines.Length && i < 5; i++)
                    _out.WriteLine($"   {Red(messageLines[i].TrimEnd())}");

                _out.WriteLine();

                // Stack trace
                if (!string.IsNullOrEmpty(f.StackTrace))
                {
                    var sanitized = SanitizeStackTrace(f.StackTrace);
                    foreach (var line in sanitized.Split('\n').Take(10))
                        _out.WriteLine($"   {Dim(line.TrimEnd())}");
                    _out.WriteLine();
                }

                // Screenshot path
                if (!string.IsNullOrEmpty(f.ScreenshotPath))
                    _out.WriteLine($"   Screenshot: {f.ScreenshotPath}");

                // Test output (ConnectionHelper logs, etc.), only for failures
                if (!string.IsNullOrWhiteSpace(fOutput))
                {
                    _out.WriteLine($"   {Dim("\u2500\u2500 Output \u2500\u2500")}");
                    foreach (var line in fOutput.Split('\n').Take(20))
                        _out.WriteLine($"   {Dim(line.TrimEnd())}");
                    _out.WriteLine();
                }
            }

            _out.WriteLine($" {rule}");
            _out.WriteLine();
        }

        // Summary footer: include implicit skips (tests that never executed)
        var executedTotal = e.Passed + e.Failed + e.Skipped;
        var implicitSkipped = Math.Max(0, TotalDiscovered - executedTotal);
        var totalSkipped = e.Skipped + implicitSkipped;
        var totalTests = e.Passed + e.Failed + totalSkipped;
        var parts = new List<string>();

        if (e.Failed > 0)
            parts.Add(RedBold($"{e.Failed} failed"));
        if (totalSkipped > 0)
            parts.Add(Yellow($"{totalSkipped} skipped"));
        parts.Add(Green($"{e.Passed} passed"));

        var countsStr = string.Join($" {Dim("|")} ", parts);
        _out.WriteLine($" {Bold("Tests")}   {countsStr} {Dim($"({totalTests} total)")}");
        _out.WriteLine($" {Bold("Duration")}  {FormatDuration(e.Duration)}");
        _out.WriteLine();
        _out.Flush();
    }

    // ── Setup phases ──

    public override void OnSetupPhaseStarted(SetupPhaseStartedEvent e)
    {
        lock (_writeLock)
        {
            // If a test spinner is active, clear its line before writing the phase header
            if (_isTTY && _spinnerLabel != null && !_spinnerIsSetup)
                _out.Write("\r\x1b[2K");

            _out.WriteLine();
            _out.WriteLine($" {Cyan("\u25C8")} {Bold(e.PhaseName)}");

            if (_isGitHubActions)
                _out.WriteLine($"::group::Setup: {e.PhaseName}");

            // Redraw the test spinner if it was active
            if (_isTTY && _spinnerLabel != null && !_spinnerIsSetup)
                _out.Write(BuildSpinnerLine(_spinnerFrame));

            _out.Flush();
        }
    }

    public override void OnSetupStep(SetupStepEvent e)
    {
        var details = !string.IsNullOrEmpty(e.Details) ? $"  {Dim($"({e.Details})")}" : "";

        switch (e.Status)
        {
            case SetupStepStatus.Started:
                // Don't replace an active test spinner with a setup spinner.
                // The check is intentionally outside the lock. StartSpinner acquires
                // it internally, and the worst case is a benign no-op race.
                if (_spinnerLabel == null || _spinnerIsSetup)
                    StartSpinner($"{e.StepName}{details}", "   ", isSetup: true);
                break;

            case SetupStepStatus.InProgress:
                // Update the spinner label with new details (e.g. progress info)
                // Only if the active spinner is a setup spinner; don't overwrite test spinners
                if (_isTTY)
                {
                    lock (_writeLock)
                    {
                        if (_spinnerIsSetup)
                            _spinnerLabel = $"{e.StepName}{details}";
                    }
                }
                break;

            case SetupStepStatus.Completed:
            {
                var pastTense = ToPastTense(e.StepName);
                lock (_writeLock)
                {
                    WriteSetupResultLocked($"   {Green("\u2713")} {pastTense}{details}");
                }
                break;
            }

            case SetupStepStatus.Failed:
            {
                lock (_writeLock)
                {
                    WriteSetupResultLocked($"   {RedBold("\u2717")} {e.StepName}{details}");
                }
                break;
            }

            case SetupStepStatus.Warning:
            {
                lock (_writeLock)
                {
                    WriteSetupResultLocked($"   {Yellow("!")} {e.StepName}{details}");
                }
                break;
            }
        }
    }

    public override void OnSetupPhaseCompleted(SetupPhaseCompletedEvent e)
    {
        // Stop any lingering step spinner from this phase, but not test spinners
        lock (_writeLock)
        {
            StopSetupSpinnerLocked();
        }

        if (_isGitHubActions)
            _out.WriteLine("::endgroup::");
    }

    // ── Tests ──

    public override void OnTestStarted(TestStartedEvent e)
    {
        var shortClass = GetShortClassName(e.TestClass);
        EmitClassHeaderIfNew(shortClass);
        var shortTest = GetShortTestName(e.DisplayName);
        StartSpinner(shortTest);
    }

    private void StartSpinner(string label, string indent = "   ", bool isSetup = false)
    {
        if (_isTTY)
        {
            lock (_writeLock)
            {
                // Stop any existing spinner first
                _spinnerTimer?.Dispose();
                _spinnerLabel = label;
                _spinnerIndent = indent;
                _spinnerIsSetup = isSetup;
                _spinnerFrame = 0;
                _out.Write(BuildSpinnerLine(0));
                _out.Flush();
                _spinnerTimer = new Timer(SpinnerTick, null, 80, 80);
            }
        }
        else
        {
            _out.WriteLine($"{indent}{Dim("\u25CC")} {label}");
            _out.Flush();
        }
    }

    private void SpinnerTick(object? state)
    {
        lock (_writeLock)
        {
            if (_spinnerLabel == null) return;
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            _out.Write(BuildSpinnerLine(_spinnerFrame));
            _out.Flush();
        }
    }

    /// <summary>
    /// Build a spinner line truncated to terminal width to prevent wrapping.
    /// When spinner text wraps to a second line, \r\x1b[2K only clears the
    /// wrapped portion, leaving the first line as a ghost artifact.
    /// </summary>
    private string BuildSpinnerLine(int frame)
    {
        // "{indent}{spinner} {label}": indent + spinner char (1) + space (1) + label
        var prefixLen = _spinnerIndent.Length + 2; // spinner char + space
        var maxLabelLen = _terminalWidth > 0 ? _terminalWidth - prefixLen : 0;

        var label = _spinnerLabel ?? "";
        if (maxLabelLen > 4 && label.Length > maxLabelLen)
            label = string.Concat(label.AsSpan(0, maxLabelLen - 1), "\u2026"); // ellipsis

        return $"\r\x1b[2K{_spinnerIndent}{Yellow(SpinnerFrames[frame])} {label}";
    }

    private static int GetTerminalWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 120; }
    }

    /// <summary>
    /// Stop the spinner. Must be called while holding _writeLock
    /// to prevent a timer callback from writing between stop and result output.
    /// </summary>
    private void StopSpinnerLocked()
    {
        _spinnerTimer?.Dispose();
        _spinnerTimer = null;
        _spinnerLabel = null;
    }

    /// <summary>
    /// Stop the spinner only if it belongs to a setup step (not a test).
    /// Setup events arriving via the named pipe can race with test spinners;
    /// this prevents a late setup completion from killing the active test spinner.
    /// Must be called while holding _writeLock.
    /// </summary>
    private bool StopSetupSpinnerLocked()
    {
        if (_spinnerLabel == null || !_spinnerIsSetup)
            return false;

        StopSpinnerLocked();
        return true;
    }

    /// <summary>
    /// Write a setup result line. If a test spinner is active, temporarily clears it,
    /// writes the result line, then redraws the test spinner on the next line.
    /// Must be called while holding _writeLock.
    /// </summary>
    private void WriteSetupResultLocked(string line)
    {
        if (StopSetupSpinnerLocked())
        {
            // Was a setup spinner; clear its line and write the result
            if (_isTTY)
                _out.Write("\r\x1b[2K");
            _out.WriteLine(line);
        }
        else if (_spinnerLabel != null)
        {
            // A test spinner is active; write result above it then redraw
            if (_isTTY)
                _out.Write("\r\x1b[2K");
            _out.WriteLine(line);
            _out.Write(BuildSpinnerLine(_spinnerFrame));
        }
        else
        {
            // No spinner active
            _out.WriteLine(line);
        }
        _out.Flush();
    }

    public override void OnTestEnrichment(TestEnrichmentEvent e)
    {
        // CIRenderer has no TestRunState; we rely on RendererBase's gate
        // (_classifiedAsCanceled set, populated by OnTestFailed) so this is a
        // no-op when OnTestFailed never classified this test as canceled.
        // When the test process tells us a body-OCE was actually a
        // per-test timeout (Outcome == "failed"), sync the counter so the
        // stdout summary tally agrees with summary.json.
        if (e.Outcome == "failed")
            ReclassifyCanceledAsFailed(e.DisplayName);
    }

    protected override void OnTestPassedCore(TestPassedEvent e)
    {
        var shortTest = GetShortTestName(e.DisplayName);
        var line = $"   {Green("\u2713")} {shortTest}  {Dim(FormatDuration(e.Duration))}";
        var output = GetAccumulatedOutput(e.DisplayName);

        lock (_writeLock)
        {
            StopSpinnerLocked();
            if (_isTTY)
                _out.Write("\r\x1b[2K");

            _out.WriteLine(line);

            // In verbose mode, show test output inline
            if (Verbose && !string.IsNullOrWhiteSpace(output))
            {
                foreach (var ol in output.Split('\n'))
                    _out.WriteLine($"     {Dim(ol.TrimEnd())}");
            }

            _out.Flush();
        }
    }

    protected override void OnTestFailedCore(TestFailedEvent e)
    {
        var shortTest = GetShortTestName(e.DisplayName);
        var output = GetAccumulatedOutput(e.DisplayName);

        lock (_writeLock)
        {
            StopSpinnerLocked();
            if (_isTTY)
                _out.Write("\r\x1b[2K");

            _out.WriteLine($"   {RedBold("\u2717")} {shortTest}  {Dim(FormatDuration(e.Duration))}");

            // Short inline error (first line only)
            _out.WriteLine($"     {Red($"\u2192 {FirstLine(e.Message)}")}");

            _failures.Add((e, output));

            // GitHub Actions error annotation
            if (_isGitHubActions)
            {
                var escapedMsg = e.Message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
                _err.WriteLine($"::error title={e.TestClass}.{e.TestMethod}::{escapedMsg}");
            }

            // In verbose mode, show test output inline
            if (Verbose && !string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split('\n'))
                    _out.WriteLine($"     {Dim(line.TrimEnd())}");
            }

            _out.Flush();
        }
    }

    protected override void OnTestSkippedCore(TestSkippedEvent e)
    {
        var shortTest = GetShortTestName(e.DisplayName);

        lock (_writeLock)
        {
            StopSpinnerLocked();
            if (_isTTY)
                _out.Write("\r\x1b[2K");

            _out.WriteLine($"   {Yellow("\u25CB")} {shortTest}  {Dim(e.Reason)}");
            _out.Flush();
        }
    }

    // ── Annotations (Plane C) ──

    public override void OnTestAnnotation(TestAnnotationEvent e)
    {
        // Trace level only renders when verbose.
        if (e.Level == AnnotationLevel.Trace && !Verbose)
            return;

        var (icon, colorize) = e.Level switch
        {
            AnnotationLevel.Success => ("✓", (Func<string, string>)Green),
            AnnotationLevel.Error   => ("✗", RedBold),
            AnnotationLevel.Warning => ("!",      Yellow),
            AnnotationLevel.Detail  => ("→", Dim),
            AnnotationLevel.Trace   => ("·", Dim),
            AnnotationLevel.Section => ("",       Bold),
            _                        => ("",       static s => s),
        };

        var sourceTag = $"[{e.Source.ToString().ToLowerInvariant()}]";
        var prefix = string.IsNullOrEmpty(icon) ? sourceTag : $"{sourceTag} {icon}";
        var line = $"   {colorize($"{prefix} {e.Message}")}";

        lock (_writeLock)
        {
            if (_isTTY && _spinnerLabel != null)
                _err.Write("\r\x1b[2K");

            _err.WriteLine(line);

            if (_isTTY && _spinnerLabel != null)
                _out.Write(BuildSpinnerLine(_spinnerFrame));

            _err.Flush();
        }
    }

    // ── Diagnostics ──

    public override void OnDiagnostic(DiagnosticEvent e)
    {
        // In non-verbose mode: suppress everything except errors
        if (!Verbose && e.Level < LogLevel.Error)
            return;

        // In verbose mode: show Info+ (same as old behavior)
        if (Verbose && e.Level < LogLevel.Info)
            return;

        var prefix = e.Source switch
        {
            LogSource.Server => "server",
            LogSource.Game => "game",
            LogSource.Fixture => "setup",
            LogSource.Test => "test",
            _ => "info"
        };

        var line = $"   [{prefix}] {e.Message}";

        lock (_writeLock)
        {
            // If a spinner is active on TTY, clear its line before writing the diagnostic
            if (_isTTY && _spinnerLabel != null)
                _out.Write("\r\x1b[2K");

            if (e.Level >= LogLevel.Error)
                _err.WriteLine(Red(line));
            else if (e.Level >= LogLevel.Warning)
                _out.WriteLine(Yellow(line));
            else
                _out.WriteLine(Dim(line));

            // Redraw the spinner line if it was active
            if (_isTTY && _spinnerLabel != null)
            {
                _out.Write(BuildSpinnerLine(_spinnerFrame));
            }

            _out.Flush();
        }
    }

    public override void OnError(ErrorEvent e)
    {
        _err.WriteLine(RedBold($" ERROR: {e.Message}"));
        if (!string.IsNullOrEmpty(e.StackTrace))
        {
            var sanitized = SanitizeStackTrace(e.StackTrace);
            foreach (var line in sanitized.Split('\n').Take(10))
                _err.WriteLine(Dim($"   {line.TrimEnd()}"));
        }

        if (_isGitHubActions)
        {
            var escapedMsg = e.Message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
            _err.WriteLine($"::error::{escapedMsg}");
        }

        _err.Flush();
    }

    // ── Dispose ──

    public override ValueTask DisposeAsync()
    {
        lock (_writeLock)
        {
            StopSpinnerLocked();
        }
        // Close any open GitHub Actions group
        EndClassGroup();
        return ValueTask.CompletedTask;
    }

    // ── Class grouping helpers ──

    private void EmitClassHeaderIfNew(string className)
    {
        if (_currentClassName == className)
            return;

        // Close previous class group
        EndClassGroup();

        _currentClassName = className;
        _out.WriteLine();
        _out.WriteLine($" {Bold(className)}");

        if (_isGitHubActions)
        {
            _out.WriteLine($"::group::{className}");
            _currentClassHasGitHubGroup = true;
        }
    }

    private void EndClassGroup()
    {
        if (_currentClassHasGitHubGroup)
        {
            _out.WriteLine("::endgroup::");
            _currentClassHasGitHubGroup = false;
        }
    }

    // ── Utilities ──

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var idx = text.IndexOfAny(['\r', '\n']);
        return idx >= 0 ? text[..idx] : text;
    }

}
