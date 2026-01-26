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

        _monitor.Log("Error capture started", LogLevel.Debug);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        CaptureError("UnhandledException", ex?.Message ?? "Unknown error", ex?.StackTrace, ex?.GetType().Name);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CaptureError("UnobservedTaskException", e.Exception.Message, e.Exception.StackTrace, e.Exception.GetType().Name);
        e.SetObserved(); // Prevent crash
    }

    /// <summary>
    /// Manually capture an error (can be called from catch blocks).
    /// </summary>
    public void CaptureError(string source, string message, string? stackTrace = null, string? exceptionType = null)
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
                ExceptionType = exceptionType
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
            var errors = limit.HasValue
                ? _errors.TakeLast(limit.Value).ToList()
                : _errors.ToList();

            var response = new ErrorsResponse
            {
                TotalCount = _errors.Count,
                Errors = errors
            };

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
            lock (_lock) return _errors.Count;
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
