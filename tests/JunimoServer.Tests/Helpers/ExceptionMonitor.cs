using DotNet.Testcontainers.Containers;
using JunimoServer.Tests.Clients;
using System.Text.RegularExpressions;

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

    public override string ToString() =>
        $"[{Source}] {ExceptionType ?? "Exception"}: {Message}";
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
    /// If true, monitor server container logs for exceptions.
    /// Default is true.
    /// </summary>
    public bool MonitorServerLogs { get; set; } = true;

    /// <summary>
    /// Regex patterns to ignore in server logs (e.g., expected warnings).
    /// </summary>
    public List<string> IgnorePatterns { get; set; } = new();

    /// <summary>
    /// Default options for standard testing.
    /// </summary>
    public static ExceptionMonitorOptions Default => new();

    /// <summary>
    /// Options with exception monitoring disabled (for testing exception handling).
    /// </summary>
    public static ExceptionMonitorOptions Disabled => new()
    {
        AbortOnException = false,
        MonitorGameClient = false,
        MonitorServerLogs = false
    };
}

/// <summary>
/// Monitors both the server and game client for exceptions.
/// Allows tests to fail fast when unexpected errors occur.
/// </summary>
public class ExceptionMonitor : IDisposable
{
    private readonly GameTestClient _gameClient;
    private readonly IContainer? _serverContainer;
    private readonly Action<string>? _logOutput;
    private readonly ExceptionMonitorOptions _options;

    private readonly List<CapturedException> _capturedExceptions = new();
    private readonly object _lock = new();
    private readonly List<Regex> _ignorePatterns;

    // Track what we've seen from client to avoid duplicates
    private readonly HashSet<string> _seenClientErrorIds = new();

    // For server log streaming
    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;
    private long _lastLogPosition;

    // Regex patterns for detecting exceptions in server logs
    private static readonly Regex ExceptionPattern = new(
        @"(?:Exception|Error|FATAL|CRITICAL).*?:.*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StackTracePattern = new(
        @"^\s+at\s+",
        RegexOptions.Compiled);

    public ExceptionMonitor(
        GameTestClient gameClient,
        IContainer? serverContainer = null,
        ExceptionMonitorOptions? options = null,
        Action<string>? logOutput = null)
    {
        _gameClient = gameClient;
        _serverContainer = serverContainer;
        _options = options ?? ExceptionMonitorOptions.Default;
        _logOutput = logOutput;

        _ignorePatterns = _options.IgnorePatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
    }

    /// <summary>
    /// Starts background monitoring of server logs.
    /// </summary>
    public void StartServerLogMonitoring()
    {
        if (_serverContainer == null || !_options.MonitorServerLogs)
            return;

        _logStreamCts = new CancellationTokenSource();
        _logStreamTask = Task.Run(() => StreamServerLogsAsync(_logStreamCts.Token));
    }

    /// <summary>
    /// Stops server log monitoring.
    /// </summary>
    public async Task StopServerLogMonitoringAsync()
    {
        if (_logStreamCts == null) return;

        _logStreamCts.Cancel();
        if (_logStreamTask != null)
        {
            try
            {
                await _logStreamTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
        _logStreamCts.Dispose();
        _logStreamCts = null;
    }

    private async Task StreamServerLogsAsync(CancellationToken ct)
    {
        if (_serverContainer == null) return;

        var accumulatedStackTrace = new List<string>();
        string? currentExceptionMessage = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var logs = await _serverContainer.GetLogsAsync(
                    timestampsEnabled: false,
                    ct: ct);

                var allLines = logs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // Process new lines
                for (var i = (int)_lastLogPosition; i < allLines.Length; i++)
                {
                    var line = allLines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Output to console with [Server] prefix
                    _logOutput?.Invoke($"[Server] {line}");

                    // Check for exception patterns
                    if (ExceptionPattern.IsMatch(line))
                    {
                        // Start capturing a new exception
                        if (currentExceptionMessage != null)
                        {
                            // Save previous exception
                            CaptureServerException(currentExceptionMessage, accumulatedStackTrace);
                            accumulatedStackTrace.Clear();
                        }
                        currentExceptionMessage = line;
                    }
                    else if (StackTracePattern.IsMatch(line) && currentExceptionMessage != null)
                    {
                        // Accumulate stack trace
                        accumulatedStackTrace.Add(line);
                    }
                    else if (currentExceptionMessage != null)
                    {
                        // End of stack trace
                        CaptureServerException(currentExceptionMessage, accumulatedStackTrace);
                        currentExceptionMessage = null;
                        accumulatedStackTrace.Clear();
                    }
                }

                _lastLogPosition = allLines.Length;

                await Task.Delay(500, ct); // Poll every 500ms
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Ignore errors while streaming logs
                await Task.Delay(1000, ct);
            }
        }

        // Capture any remaining exception
        if (currentExceptionMessage != null)
        {
            CaptureServerException(currentExceptionMessage, accumulatedStackTrace);
        }
    }

    private void CaptureServerException(string message, List<string> stackTrace)
    {
        // Check ignore patterns
        if (_ignorePatterns.Any(p => p.IsMatch(message)))
            return;

        lock (_lock)
        {
            _capturedExceptions.Add(new CapturedException
            {
                Source = "Server",
                Timestamp = DateTime.UtcNow,
                Message = message,
                StackTrace = stackTrace.Count > 0 ? string.Join("\n", stackTrace) : null
            });
        }
    }

    /// <summary>
    /// Checks the game client for any captured errors and adds them to our list.
    /// </summary>
    public async Task CheckGameClientErrorsAsync()
    {
        if (!_options.MonitorGameClient) return;

        try
        {
            var errors = await _gameClient.GetErrors();
            if (errors?.Errors == null || errors.Errors.Count == 0) return;

            lock (_lock)
            {
                foreach (var error in errors.Errors)
                {
                    // Skip if we've already seen this error
                    if (_seenClientErrorIds.Contains(error.Id))
                        continue;

                    _seenClientErrorIds.Add(error.Id);

                    // Log to output
                    _logOutput?.Invoke($"[Game] EXCEPTION: {error}");

                    _capturedExceptions.Add(new CapturedException
                    {
                        Source = "GameClient",
                        Timestamp = error.Timestamp,
                        Message = error.Message,
                        ExceptionType = error.ExceptionType,
                        StackTrace = error.StackTrace
                    });
                }
            }
        }
        catch (Exception)
        {
            // Ignore errors while checking for errors
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
    /// </summary>
    /// <param name="context">Optional context message for the assertion.</param>
    public async Task AssertNoExceptionsAsync(string? context = null)
    {
        // First check game client for new errors
        await CheckGameClientErrorsAsync();

        if (!_options.AbortOnException) return;

        lock (_lock)
        {
            if (_capturedExceptions.Count > 0)
            {
                var exceptions = string.Join("\n\n", _capturedExceptions.Select(e => e.ToString()));
                var message = context != null
                    ? $"Exceptions detected during: {context}\n\n{exceptions}"
                    : $"Exceptions detected:\n\n{exceptions}";

                throw new ExceptionMonitorException(message, _capturedExceptions.ToList());
            }
        }
    }

    /// <summary>
    /// Creates a scope that will check for exceptions when disposed.
    /// Usage: using (await monitor.CheckpointAsync("joining server")) { ... }
    /// </summary>
    public async Task<ExceptionCheckpoint> CheckpointAsync(string context)
    {
        // Check for exceptions at start of checkpoint
        await AssertNoExceptionsAsync($"before {context}");
        return new ExceptionCheckpoint(this, context);
    }

    /// <summary>
    /// Temporarily disables abort-on-exception for a scope.
    /// Usage: using (monitor.SuppressAbort()) { ... code that may throw expected exceptions ... }
    /// </summary>
    public ExceptionSuppressionScope SuppressAbort()
    {
        return new ExceptionSuppressionScope(this);
    }

    public void Dispose()
    {
        _logStreamCts?.Cancel();
        _logStreamCts?.Dispose();
    }

    /// <summary>
    /// Represents a checkpoint scope that checks for exceptions when disposed.
    /// </summary>
    public class ExceptionCheckpoint : IAsyncDisposable
    {
        private readonly ExceptionMonitor _monitor;
        private readonly string _context;

        public ExceptionCheckpoint(ExceptionMonitor monitor, string context)
        {
            _monitor = monitor;
            _context = context;
        }

        public async ValueTask DisposeAsync()
        {
            await _monitor.AssertNoExceptionsAsync($"during {_context}");
        }
    }

    /// <summary>
    /// Scope that suppresses exception abort behavior.
    /// </summary>
    public class ExceptionSuppressionScope : IDisposable
    {
        private readonly ExceptionMonitor _monitor;
        private readonly bool _previousAbortOnException;

        public ExceptionSuppressionScope(ExceptionMonitor monitor)
        {
            _monitor = monitor;
            _previousAbortOnException = monitor._options.AbortOnException;
            monitor._options.AbortOnException = false;
        }

        public void Dispose()
        {
            _monitor._options.AbortOnException = _previousAbortOnException;
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
