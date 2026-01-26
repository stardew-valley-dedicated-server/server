using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Log entry for structured JSONL output.
/// Designed for AI agent parsing and debugging.
/// </summary>
public record TestLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    [JsonPropertyName("testName")]
    public string TestName { get; init; } = "";

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "";

    [JsonPropertyName("level")]
    public string Level { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Data { get; init; }

    [JsonPropertyName("screenshotPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScreenshotPath { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }
}

/// <summary>
/// JSONL reporter for machine-parseable test output.
/// Writes structured log entries to TestResults/Logs/tests.jsonl.
/// Enable via SDVD_REPORTER_JSONL=true environment variable.
/// </summary>
public class JsonlReporter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentQueue<TestLogEntry> _buffer = new();
    private readonly string _testName;
    private readonly DateTime _testStartTime;
    private readonly string? _logFilePath;
    private readonly object _fileLock = new();
    private string _currentPhase = "setup";
    private bool _disposed;

    /// <summary>
    /// Returns true if JSONL reporter is enabled via SDVD_REPORTER_JSONL environment variable.
    /// </summary>
    public static bool IsEnabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_REPORTER_JSONL"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new JSONL reporter for a test.
    /// </summary>
    /// <param name="testName">Full test name (e.g., "PasswordProtectionTests.Login_WithCorrectPassword")</param>
    public JsonlReporter(string testName)
    {
        _testName = testName;
        _testStartTime = DateTime.UtcNow;

        if (IsEnabled)
        {
            var logsDir = Path.Combine(TestArtifacts.OutputDir, "Logs");
            Directory.CreateDirectory(logsDir);
            _logFilePath = Path.Combine(logsDir, "tests.jsonl");
        }
    }

    /// <summary>
    /// Sets the current test phase for subsequent log entries.
    /// </summary>
    public void SetPhase(string phase) => _currentPhase = phase;

    /// <summary>
    /// Log an info-level message.
    /// </summary>
    public void Info(string message, object? data = null, string? screenshotPath = null)
        => Log("info", message, data, screenshotPath);

    /// <summary>
    /// Log a success-level message.
    /// </summary>
    public void Success(string message, object? data = null, string? screenshotPath = null)
        => Log("success", message, data, screenshotPath);

    /// <summary>
    /// Log a warning-level message.
    /// </summary>
    public void Warning(string message, object? data = null, string? screenshotPath = null)
        => Log("warning", message, data, screenshotPath);

    /// <summary>
    /// Log an error-level message.
    /// </summary>
    public void Error(string message, object? data = null, string? screenshotPath = null)
        => Log("error", message, data, screenshotPath);

    private void Log(string level, string message, object? data, string? screenshotPath)
    {
        if (!IsEnabled) return;

        var entry = new TestLogEntry
        {
            Timestamp = DateTime.UtcNow,
            TestName = _testName,
            Phase = _currentPhase,
            Level = level,
            Message = message,
            Data = ConvertToDict(data),
            ScreenshotPath = screenshotPath,
            ElapsedMs = (long)(DateTime.UtcNow - _testStartTime).TotalMilliseconds
        };

        _buffer.Enqueue(entry);
        WriteEntry(entry);
    }

    private static Dictionary<string, object>? ConvertToDict(object? data)
    {
        if (data == null) return null;

        // If already a dictionary, return as-is
        if (data is Dictionary<string, object> dict)
            return dict;

        // Convert anonymous object to dictionary using reflection
        var result = new Dictionary<string, object>();
        foreach (var prop in data.GetType().GetProperties())
        {
            var value = prop.GetValue(data);
            if (value != null)
                result[prop.Name] = value;
        }

        return result.Count > 0 ? result : null;
    }

    private void WriteEntry(TestLogEntry entry)
    {
        if (_logFilePath == null) return;

        var json = JsonSerializer.Serialize(entry, JsonOptions);

        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(_logFilePath, json + Environment.NewLine);
            }
            catch
            {
                // Ignore write errors - don't fail tests due to logging
            }
        }
    }

    /// <summary>
    /// Gets all buffered log entries for this test.
    /// </summary>
    public IReadOnlyList<TestLogEntry> GetEntries() => _buffer.ToArray();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
