using System.Collections.Concurrent;
using JunimoServer.TestRunner.Rendering;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using Xunit.SimpleRunner;

namespace JunimoServer.TestRunner.Sinks;

/// <summary>
/// Bridges xUnit SimpleRunner callbacks to our ITestRenderer.
/// This is the integration point between xUnit's event system and our rendering layer.
/// </summary>
public sealed class RunnerCallbacks
{
    private readonly ITestRenderer _renderer;

    /// <summary>
    /// Per-test dedupe set keyed by <c>TestDisplayName</c>. xUnit's
    /// <c>OnTestFinished</c> callback fires for every test that started
    /// (with a polymorphic <c>TestFinishedInfo</c> whose runtime subtype —
    /// <c>TestPassedInfo</c>/<c>TestFailedInfo</c>/<c>TestSkippedInfo</c>/
    /// <c>TestNotRunInfo</c> — encodes the outcome). Per the xunit docs the
    /// generic handler is invoked AFTER any matching specific handler, so we
    /// track which display names we've already dispatched and skip duplicates.
    /// When the typed callback didn't fire (the missing-callback bug we
    /// defend against), <c>OnTestFinished</c> is the safety net.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _alreadyDispatched = new();

    public RunnerCallbacks(ITestRenderer renderer)
    {
        _renderer = renderer;
    }

    public void OnDiscoveryComplete(DiscoveryCompleteInfo info)
    {
        // xUnit v3 exposes only `TestCasesToRun` (post-filter); pass it as both the discovered and to-run count.
        _renderer.OnDiscoveryComplete(
            new DiscoveryCompleteEvent(info.TestCasesToRun, info.TestCasesToRun)
        );
    }

    public void OnExecutionComplete(ExecutionCompleteInfo info)
    {
        _renderer.OnRunFinished(
            new RunFinishedEvent(
                info.TestsTotal,
                info.TestsPassed,
                info.TestsFailed,
                info.TestsSkipped,
                TimeSpan.FromSeconds((double)info.ExecutionTime)
            )
        );
    }

    public void OnTestStarting(TestStartingInfo info)
    {
        // Parse class and method from display name
        var (className, methodName) = ParseTestDisplayName(info.TestDisplayName);

        _renderer.OnTestStarted(
            new TestStartedEvent(
                info.TestCollectionDisplayName,
                className,
                methodName,
                info.TestDisplayName
            )
        );
    }

    public void OnTestPassed(TestPassedInfo info)
    {
        if (!_alreadyDispatched.TryAdd(info.TestDisplayName, 0))
            return;
        var (className, methodName) = ParseTestDisplayName(info.TestDisplayName);

        _renderer.OnTestPassed(
            new TestPassedEvent(
                info.TestCollectionDisplayName,
                className,
                methodName,
                info.TestDisplayName,
                TimeSpan.FromSeconds((double)info.ExecutionTime)
            )
        );
    }

    public void OnTestFailed(TestFailedInfo info)
    {
        if (!_alreadyDispatched.TryAdd(info.TestDisplayName, 0))
            return;
        var (className, methodName) = ParseTestDisplayName(info.TestDisplayName);

        // Extract exception info from ExceptionInfo structure.
        // For fixture failures, xUnit wraps the real cause in TestPipelineException.
        // Unwrap InnerExceptions recursively to surface the actual root cause
        // (e.g. AggregateException → OperationCanceledException).
        var exception = info.Exception;
        var deepest = UnwrapException(exception);

        var exceptionType = deepest?.FullType ?? exception?.FullType ?? "Unknown";
        var exceptionMessage = BuildExceptionMessage(exception, deepest);
        var stackTrace = deepest?.StackTrace ?? exception?.StackTrace;

        _renderer.OnTestFailed(
            new TestFailedEvent(
                info.TestCollectionDisplayName,
                className,
                methodName,
                info.TestDisplayName,
                TimeSpan.FromSeconds((double)info.ExecutionTime),
                exceptionType,
                exceptionMessage,
                stackTrace
            )
        );
    }

    public void OnTestSkipped(TestSkippedInfo info)
    {
        if (!_alreadyDispatched.TryAdd(info.TestDisplayName, 0))
            return;
        var (className, methodName) = ParseTestDisplayName(info.TestDisplayName);

        _renderer.OnTestSkipped(
            new TestSkippedEvent(
                info.TestCollectionDisplayName,
                className,
                methodName,
                info.TestDisplayName,
                info.SkipReason
            )
        );
    }

    /// <summary>
    /// xUnit fires this when a test was discovered but didn't run (e.g.
    /// filter mismatch). Currently routed through <see cref="OnTestSkipped"/>
    /// so downstream counters and Status are consistent — there's no
    /// meaningful runner-side distinction between "skipped by attribute" and
    /// "skipped by filter" for artifact reporting.
    /// </summary>
    public void OnTestNotRun(TestNotRunInfo info)
    {
        if (!_alreadyDispatched.TryAdd(info.TestDisplayName, 0))
            return;
        var (className, methodName) = ParseTestDisplayName(info.TestDisplayName);

        _renderer.OnTestSkipped(
            new TestSkippedEvent(
                info.TestCollectionDisplayName,
                className,
                methodName,
                info.TestDisplayName,
                "Test not run (filter mismatch)"
            )
        );
    }

    /// <summary>
    /// Generic per-test completion callback. xUnit fires this for every test
    /// that started, regardless of outcome (per the xunit.v3.runner.utility
    /// 3.2.2 docs: "the same finished info object is sent in both cases, so
    /// you can alternatively just subscribe to this one handler and
    /// differentiate status based on info class type"). Per docs the generic
    /// handler runs AFTER the specific handler — reaching here without
    /// <see cref="_alreadyDispatched"/> containing the test means xUnit
    /// dropped the typed callback, the bug class this safety net defends
    /// against. We dispatch by runtime subtype so the test still flows
    /// through the appropriate downstream renderer pipeline.
    /// </summary>
    public void OnTestFinished(TestFinishedInfo info)
    {
        if (_alreadyDispatched.ContainsKey(info.TestDisplayName))
            return;

        switch (info)
        {
            case TestPassedInfo p:
                OnTestPassed(p);
                break;
            case TestFailedInfo f:
                OnTestFailed(f);
                break;
            case TestSkippedInfo s:
                OnTestSkipped(s);
                break;
            case TestNotRunInfo nr:
                OnTestNotRun(nr);
                break;
            default:
                // Truly unclassified — TestFinishedInfo with no recognized
                // subtype. Indicates either an xUnit version mismatch (new
                // outcome type added) or a deeper internal gating bug. Surface
                // explicitly so TestRunState can fall back to enrichment via
                // ApplyRunFinished's sweep reading EnrichmentOutcome.
                InfrastructureEventLog.Emit(
                    "test_unclassified_finish",
                    new
                    {
                        testDisplayName = info.TestDisplayName,
                        runtimeType = info.GetType().FullName,
                    }
                );
                // No renderer dispatch — the test stays "running" in
                // TestRunState until ApplyRunFinished's sweep, at which point
                // EnrichmentOutcome (if set) determines the outcome.
                break;
        }
    }

    public void OnDiagnosticMessage(MessageInfo info)
    {
        // Parse source from message if prefixed, otherwise assume framework
        var (source, message) = ParseDiagnosticMessage(info.Message);

        _renderer.OnDiagnostic(new DiagnosticEvent(source, LogLevel.Info, message));
    }

    public void OnErrorMessage(ErrorMessageInfo info)
    {
        var errorType = info.ErrorMessageType.ToString();
        var exceptionMessage = info.Exception?.Message ?? "Unknown error";
        var stackTrace = info.Exception?.StackTrace;

        // Unwrap inner exceptions to get the actual root cause.
        // xUnit wraps fixture failures in TestPipelineException, which hides the real error.
        // ExceptionInfo uses InnerExceptions (List<ExceptionInfo>), not InnerException.
        var innerException = info.Exception?.InnerExceptions?.FirstOrDefault();
        var rootCause =
            innerException != null
                ? $"{exceptionMessage}\n  -> {innerException.FullType}: {innerException.Message}"
                : exceptionMessage;
        var fullStackTrace =
            innerException != null
                ? $"{innerException.StackTrace}\n--- Outer exception ---\n{stackTrace}"
                : stackTrace;

        // Check if this is a fixture initialization error
        if (
            exceptionMessage.Contains("fixture type")
            && exceptionMessage.Contains("InitializeAsync")
        )
        {
            // Extract fixture name from message like "Collection fixture type 'X' threw in InitializeAsync"
            var match = System.Text.RegularExpressions.Regex.Match(
                exceptionMessage,
                @"fixture type '([^']+)'"
            );
            var fixtureName = match.Success ? match.Groups[1].Value : "Unknown Fixture";

            // Short name for display
            var shortName = fixtureName.Contains('.')
                ? fixtureName.Substring(fixtureName.LastIndexOf('.') + 1)
                : fixtureName;

            // Emit setup phase events
            _renderer.OnSetupPhaseStarted(
                new SetupPhaseStartedEvent("Setup", $"Fixture: {shortName}")
            );
            _renderer.OnSetupStep(
                new SetupStepEvent("Setup", "InitializeAsync", SetupStepStatus.Failed, rootCause)
            );
            _renderer.OnSetupPhaseCompleted(
                new SetupPhaseCompletedEvent("Setup", $"Fixture: {shortName}", false, rootCause)
            );
        }

        _renderer.OnError(new ErrorEvent($"{errorType}: {rootCause}", fullStackTrace));
    }

    /// <summary>
    /// Parse class and method names from test display name.
    /// Format: "Namespace.ClassName.MethodName" or just "MethodName"
    /// </summary>
    private static (string ClassName, string MethodName) ParseTestDisplayName(string displayName)
    {
        // For Theory tests, parameters appear after '(' and may contain dots
        // (e.g. "Namespace.Class.Method(settingPath: \"Game.FarmName\")").
        // Only search for dots before the opening parenthesis to avoid splitting
        // inside parameter values.
        var parenIdx = displayName.IndexOf('(');
        var searchUpTo = parenIdx >= 0 ? parenIdx : displayName.Length;

        var lastDot = displayName.LastIndexOf('.', searchUpTo - 1, searchUpTo);
        if (lastDot < 0)
            return ("Unknown", displayName);

        var methodName = displayName[(lastDot + 1)..];
        var classPath = displayName[..lastDot];

        var secondLastDot = classPath.LastIndexOf('.');
        var className = secondLastDot >= 0 ? classPath[(secondLastDot + 1)..] : classPath;

        return (className, methodName);
    }

    /// <summary>
    /// Recursively unwrap InnerExceptions to find the deepest root cause.
    /// Returns null if the exception has no inner exceptions (i.e. it IS the root).
    /// </summary>
    private static Xunit.SimpleRunner.ExceptionInfo? UnwrapException(
        Xunit.SimpleRunner.ExceptionInfo? exception
    )
    {
        if (exception?.InnerExceptions == null || exception.InnerExceptions.Count == 0)
            return null;

        var inner = exception.InnerExceptions[0];
        // Recurse: if the inner exception also has inners, keep unwrapping
        return UnwrapException(inner) ?? inner;
    }

    /// <summary>
    /// Build a readable error message showing the chain from outer to root cause.
    /// </summary>
    private static string BuildExceptionMessage(
        Xunit.SimpleRunner.ExceptionInfo? outer,
        Xunit.SimpleRunner.ExceptionInfo? root
    )
    {
        if (outer == null)
            return "Unknown error";
        if (root == null)
            return outer.Message ?? "Unknown error";

        // Walk the chain to show each level
        var parts = new System.Collections.Generic.List<string>();
        parts.Add(outer.Message ?? outer.FullType ?? "Unknown");

        var current = outer;
        while (current?.InnerExceptions is { Count: > 0 })
        {
            current = current.InnerExceptions[0];
            var type = current.FullType ?? "Exception";
            var msg = current.Message;
            parts.Add(msg != null ? $"{type}: {msg}" : type);
        }

        return string.Join("\n  -> ", parts);
    }

    /// <summary>
    /// Parse diagnostic messages that may have a source prefix.
    /// Format: "[Source] message" or just "message"
    /// </summary>
    private static (LogSource Source, string Message) ParseDiagnosticMessage(string message)
    {
        if (message.StartsWith("[Server]", StringComparison.OrdinalIgnoreCase))
            return (LogSource.Server, message[8..].TrimStart());

        if (message.StartsWith("[Game]", StringComparison.OrdinalIgnoreCase))
            return (LogSource.Game, message[6..].TrimStart());

        if (message.StartsWith("[Setup]", StringComparison.OrdinalIgnoreCase))
            return (LogSource.Fixture, message[7..].TrimStart());

        if (message.StartsWith("[Test]", StringComparison.OrdinalIgnoreCase))
            return (LogSource.Test, message[6..].TrimStart());

        if (message.StartsWith("[Fixture]", StringComparison.OrdinalIgnoreCase))
            return (LogSource.Fixture, message[9..].TrimStart());

        return (LogSource.Framework, message);
    }
}
