using System.Net.Sockets;
using StardewModdingAPI;

namespace JunimoTestClient.Diagnostics;

/// <summary>
/// Captures and exposes errors/exceptions that occur in the game.
/// </summary>
public class ErrorCapture
{
    private readonly IMonitor _monitor;
    private readonly List<CapturedError> _errors = new();
    private readonly object _lock = new();

    private const int MaxErrors = 100;

    public ErrorCapture(IMonitor monitor)
    {
        _monitor = monitor;
    }

    public void Start()
    {
        // Hook into unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _monitor.Log("Error capture started", LogLevel.Trace);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        CaptureError(
            "UnhandledException",
            ex?.Message ?? "Unknown error",
            ex?.StackTrace,
            ex?.GetType().Name
        );
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved(); // Prevent crash

        // Filter benign async teardown exceptions that surface during network disconnect.
        // These are fire-and-forget socket operations in .NET's networking stack (Galaxy SDK,
        // game multiplayer). The tasks are abandoned when the connection drops.
        var innerExceptions = e.Exception.Flatten().InnerExceptions;
        if (innerExceptions.All(IsBenignTeardownException))
            return;

        // Something slipped past the cancellation filter; log the full exception tree
        // so we can diagnose what's actually in the AggregateException.
        _monitor.Log(
            $"UnobservedTaskException captured ({innerExceptions.Count} inner):",
            LogLevel.Warn
        );
        foreach (var inner in innerExceptions)
        {
            var chain = (inner.GetType().FullName ?? inner.GetType().Name) + ": " + inner.Message;
            for (var cause = inner.InnerException; cause != null; cause = cause.InnerException)
                chain +=
                    $"\n    → {(cause.GetType().FullName ?? cause.GetType().Name)}: {cause.Message}";
            _monitor.Log($"  - {chain}", LogLevel.Warn);
            if (inner.StackTrace != null)
                _monitor.Log($"    StackTrace: {inner.StackTrace}", LogLevel.Debug);
        }

        CaptureError(
            "UnobservedTaskException",
            e.Exception.Message,
            e.Exception.StackTrace,
            e.Exception.GetType().Name
        );
    }

    /// <summary>
    /// Returns true for exceptions that are normal during async network teardown
    /// and should not be surfaced to the test harness. Only called in the
    /// UnobservedTaskException context (fire-and-forget tasks abandoned on disconnect).
    /// </summary>
    private static bool IsBenignTeardownException(Exception ex)
    {
        // CancellationToken-triggered cancellation (includes TaskCanceledException)
        if (ex is OperationCanceledException)
            return true;

        // Async socket I/O canceled on disconnect. .NET throws SocketException
        // from Socket.AwaitableSocketAsyncEventArgs instead of OperationCanceledException.
        // OperationAborted = WSA 995 on Windows / ECANCELED 125 on Linux.
        if (ex is SocketException { SocketErrorCode: SocketError.OperationAborted })
            return true;

        return false;
    }

    /// <summary>
    /// Manually capture an error (can be called from catch blocks).
    /// </summary>
    public void CaptureError(
        string source,
        string message,
        string? stackTrace = null,
        string? exceptionType = null
    )
    {
        lock (_lock)
        {
            var error = new CapturedError
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Timestamp = DateTime.UtcNow,
                Source = source,
                Message = message,
                StackTrace = stackTrace,
                ExceptionType = exceptionType,
            };

            _errors.Add(error);

            // Trim old errors
            while (_errors.Count > MaxErrors)
            {
                _errors.RemoveAt(0);
            }

            _monitor.Log($"Captured error [{error.Id}]: {source} - {message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Capture an exception.
    /// </summary>
    public void CaptureException(string source, Exception ex)
    {
        CaptureError(source, ex.Message, ex.StackTrace, ex.GetType().FullName);
    }

    /// <summary>
    /// Get all captured errors.
    /// </summary>
    public ErrorsResponse GetErrors(int? limit = null, bool? clear = null)
    {
        lock (_lock)
        {
            var errors = limit.HasValue ? _errors.TakeLast(limit.Value).ToList() : _errors.ToList();

            var response = new ErrorsResponse { TotalCount = _errors.Count, Errors = errors };

            if (clear == true)
            {
                _errors.Clear();
            }

            return response;
        }
    }

    /// <summary>
    /// Get a specific error by ID.
    /// </summary>
    public CapturedError? GetError(string id)
    {
        lock (_lock)
        {
            return _errors.FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>
    /// Clear all captured errors.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _errors.Clear();
        }
    }

    /// <summary>
    /// Get error count.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _errors.Count;
        }
    }

    public void Stop()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }
}

public class CapturedError
{
    public string Id { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string? StackTrace { get; set; }
    public string? ExceptionType { get; set; }
}

public class ErrorsResponse
{
    public int TotalCount { get; set; }
    public List<CapturedError> Errors { get; set; } = new();
}
