using System.Text.RegularExpressions;
using JunimoServer.Tests.Clients;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Represents an exception captured from either the server or game client.
/// </summary>
public class CapturedException
{
    public string Source { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = "";
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }

    public override string ToString()
    {
        var result = $"[{Source}] {ExceptionType ?? "Exception"}: {Message}";
        if (!string.IsNullOrEmpty(StackTrace))
        {
            result += $"\n{StackTrace}";
        }

        return result;
    }
}

/// <summary>
/// Configuration for exception monitoring behavior.
/// </summary>
public class ExceptionMonitorOptions
{
    /// <summary>
    /// If true, exceptions will cause tests to fail immediately when checked.
    /// Default is true.
    /// </summary>
    public bool AbortOnException { get; set; } = true;

    /// <summary>
    /// If true, monitor the game client for exceptions.
    /// Default is true.
    /// </summary>
    public bool MonitorGameClient { get; set; } = true;

    /// <summary>
    /// Regex patterns to ignore in server logs (e.g., expected warnings).
    /// </summary>
    public List<string> IgnorePatterns { get; set; } = new();

    /// <summary>
    /// Default options for standard testing.
    /// </summary>
    public static ExceptionMonitorOptions Default => new();
}

/// <summary>
/// Monitors both the server and game client for exceptions.
/// Allows tests to fail fast when unexpected errors occur.
/// </summary>
public class ExceptionMonitor
{
    private readonly GameTestClient _gameClient;
    private Action<string>? _logOutput;
    private readonly ExceptionMonitorOptions _options;

    // Plumbed from TestBase: the monitor performs the server-error precheck
    // and records the failure before throwing. Both fields point at the
    // *current* test's values, not the constructing test's, because
    // PersistentSession reuses one monitor across the class — see
    // SetTestContext.
    private Func<IReadOnlyList<string>>? _serverErrorsGetter;
    private Action<string, string?>? _recordFailure;

    private readonly List<CapturedException> _capturedExceptions = new();
    private readonly object _lock = new();
    private readonly List<Regex> _ignorePatterns;

    // Track what we've seen from client to avoid duplicates
    private readonly HashSet<string> _seenClientErrorIds = new();

    public ExceptionMonitor(
        GameTestClient gameClient,
        ExceptionMonitorOptions? options = null,
        Action<string>? logOutput = null
    )
    {
        _gameClient = gameClient;
        _options = options ?? ExceptionMonitorOptions.Default;
        _logOutput = logOutput;

        _ignorePatterns = _options
            .IgnorePatterns.Select(p => new Regex(
                p,
                RegexOptions.IgnoreCase | RegexOptions.Compiled
            ))
            .ToList();
    }

    /// <summary>
    /// Updates the log output callback. Used when a persistent session is reused
    /// by a new test instance whose ITestOutputHelper differs from the original.
    /// </summary>
    public void SetLogOutput(Action<string>? logOutput) => _logOutput = logOutput;

    /// <summary>
    /// Wires the per-test server-errors source and failure-recording sink into
    /// the monitor. Called once at construction and again on persistent-session
    /// reuse so the monitor reads the current test's lease and records failures
    /// against the current test, not the test that originally built the session.
    /// </summary>
    public void SetTestContext(
        Func<IReadOnlyList<string>>? serverErrorsGetter,
        Action<string, string?>? recordFailure
    )
    {
        _serverErrorsGetter = serverErrorsGetter;
        _recordFailure = recordFailure;
    }

    /// <summary>
    /// Checks the game client for any captured errors and adds them to our list.
    /// </summary>
    public async Task CheckGameClientErrorsAsync(CancellationToken ct = default)
    {
        if (!_options.MonitorGameClient)
        {
            return;
        }

        try
        {
            var errors = await _gameClient.GetErrors(ct: ct);
            if (errors?.Errors == null || errors.Errors.Count == 0)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var error in errors.Errors)
                {
                    // Skip if we've already seen this error
                    if (_seenClientErrorIds.Contains(error.Id))
                    {
                        continue;
                    }

                    _seenClientErrorIds.Add(error.Id);

                    // UnobservedTaskException with "Operation canceled" is benign. It comes
                    // from async teardown in Galaxy SDK / Steam networking when tasks are
                    // abandoned during disconnect. ErrorCapture on the client side filters
                    // these too, but older images may not have that filter yet.
                    if (
                        string.Equals(
                            error.Source,
                            "UnobservedTaskException",
                            StringComparison.Ordinal
                        )
                        && error.Message.Contains(
                            "Operation canceled",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        _logOutput?.Invoke(
                            $"[Game] (ignored) UnobservedTaskException: {error.Message}"
                        );
                        continue;
                    }

                    // Log to output
                    _logOutput?.Invoke($"[Game] EXCEPTION: {error}");

                    _capturedExceptions.Add(
                        new CapturedException
                        {
                            Source = "GameClient",
                            Timestamp = error.Timestamp,
                            Message = error.Message,
                            ExceptionType = error.ExceptionType,
                            StackTrace = error.StackTrace,
                        }
                    );
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logOutput?.Invoke(
                $"[ExceptionMonitor] Failed to check game client errors: {ex.Message}"
            );
        }
    }

    /// <summary>
    /// Gets all captured exceptions.
    /// </summary>
    public IReadOnlyList<CapturedException> GetExceptions()
    {
        lock (_lock)
        {
            return _capturedExceptions.ToList();
        }
    }

    /// <summary>
    /// Returns true if any exceptions have been captured.
    /// </summary>
    public bool HasExceptions
    {
        get
        {
            lock (_lock)
            {
                return _capturedExceptions.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the count of captured exceptions.
    /// </summary>
    public int ExceptionCount
    {
        get
        {
            lock (_lock)
            {
                return _capturedExceptions.Count;
            }
        }
    }

    /// <summary>
    /// Clears all captured exceptions.
    /// Call this at the start of a test if you want to ignore previous errors.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _capturedExceptions.Clear();
            _seenClientErrorIds.Clear();
        }
    }

    /// <summary>
    /// Checks for exceptions and throws if AbortOnException is enabled and exceptions exist.
    /// Call this at key points during a test to fail fast on errors.
    /// Server errors (from the lease's server-errors getter, if wired) are checked
    /// first and produce an <see cref="ExceptionMonitorException"/> with no captured
    /// exceptions attached, matching the historical wrapper's contract.
    /// </summary>
    /// <param name="context">Optional context message for the assertion.</param>
    public async Task AssertNoExceptionsAsync(string? context = null)
    {
        // Server-error precheck. Throws before touching the game client so a
        // dead server short-circuits the slower game-client query.
        var serverErrors = _serverErrorsGetter?.Invoke();
        if (serverErrors is { Count: > 0 })
        {
            var errorList = string.Join("\n", serverErrors);
            var message =
                context != null
                    ? $"Server error during: {context}\n\n{errorList}"
                    : $"Server error detected\n\n{errorList}";
            _recordFailure?.Invoke(message, context);
            throw new ExceptionMonitorException(message, Array.Empty<CapturedException>());
        }

        // First check game client for new errors
        await CheckGameClientErrorsAsync();

        if (!_options.AbortOnException)
        {
            return;
        }

        ExceptionMonitorException? toThrow = null;
        lock (_lock)
        {
            if (_capturedExceptions.Count > 0)
            {
                var exceptions = string.Join("\n\n", _capturedExceptions.Select(e => e.ToString()));
                var message =
                    context != null
                        ? $"Exceptions detected during: {context}\n\n{exceptions}"
                        : $"Exceptions detected:\n\n{exceptions}";

                toThrow = new ExceptionMonitorException(message, _capturedExceptions.ToList());
            }
        }

        if (toThrow != null)
        {
            _recordFailure?.Invoke(toThrow.Message, context);
            throw toThrow;
        }
    }
}

/// <summary>
/// Exception thrown when the ExceptionMonitor detects errors.
/// </summary>
public class ExceptionMonitorException : Exception
{
    public IReadOnlyList<CapturedException> CapturedExceptions { get; }

    public ExceptionMonitorException(string message, IReadOnlyList<CapturedException> exceptions)
        : base(message)
    {
        CapturedExceptions = exceptions;
    }
}
