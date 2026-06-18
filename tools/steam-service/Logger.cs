using System.Collections.Concurrent;
using System.Text.Json;

namespace SteamService;

/// <summary>
/// Thread-safe logger with per-account elapsed time tracking.
/// Multiple SteamAuthService instances may log concurrently.
/// </summary>
public static class Logger
{
    private static DateTime _startTime = DateTime.Now;
    private static readonly ConcurrentDictionary<string, DateTime> _lastLogTimes = new();
    private static readonly object _lock = new();

    public static void Log(string message)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var key = ExtractPrefix(message);
            var last = _lastLogTimes.GetOrAdd(key, _startTime);
            var elapsed = now - last;
            _lastLogTimes[key] = now;

            Console.Write(message);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($" +{elapsed.TotalSeconds:F1}s");
            Console.ResetColor();
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _startTime = DateTime.Now;
            _lastLogTimes.Clear();
        }
    }

    private static readonly JsonSerializerOptions _eventJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Exception types already reported on stderr. Used to emit
    /// the first failure of each type and suppress the rest.</summary>
    private static readonly ConcurrentDictionary<Type, byte> _reportedEventFailures = new();

    /// <summary>Mirrors the mod's <c>Env.IsTest</c>: <c>SDVD_ENV=test</c>
    /// (case-insensitive, matching <c>Env.SdvdEnv</c>'s <c>ToLowerInvariant</c>).
    /// Only the E2E harness consumes <c>SDVD_EVENT </c> lines, so prod skips them.</summary>
    private static readonly bool _isTest = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_ENV"),
        "test",
        StringComparison.OrdinalIgnoreCase
    );

    /// <summary>
    /// Emits a structured event line. Interleaves with the free-text log
    /// stream on stdout; the host-side streamer filters by the
    /// <c>SDVD_EVENT </c> prefix and forwards to <c>infrastructure.jsonl</c>.
    ///
    /// Envelope schema: see docs/developers/events-schema.md
    ///
    /// <para>
    /// Only emits when <c>SDVD_ENV=test</c>: the lines are consumed solely by
    /// the E2E harness, so prod builds skip the serialize + stdout write.
    /// </para>
    ///
    /// <para>
    /// Never throws: a dropped event is preferable to a crashed sidecar.
    /// The first failure of each exception type is reported on
    /// <c>Console.Error</c>; subsequent failures of the same type are
    /// silent. The error path does not re-enter the emitter.
    /// </para>
    /// </summary>
    public static void LogEvent(string name, object? data = null)
    {
        if (!_isTest)
        {
            return;
        }

        try
        {
            var entry = new
            {
                ts = DateTime.UtcNow,
                requestId = SidecarRequestContext.Current,
                service = "steam-auth",
                @event = name,
                data,
            };
            var json = JsonSerializer.Serialize(entry, _eventJsonOptions);
            lock (_lock)
            {
                Console.Out.WriteLine("SDVD_EVENT " + json);
            }
        }
        catch (Exception ex)
        {
            ReportEventFailure(name, ex);
        }
    }

    /// <summary>
    /// Reports an emission failure once per exception type on <c>Console.Error</c>.
    /// Must not re-enter <see cref="LogEvent"/> — a broken sink would loop.
    /// </summary>
    private static void ReportEventFailure(string name, Exception ex)
    {
        try
        {
            if (_reportedEventFailures.TryAdd(ex.GetType(), 0))
            {
                Console.Error.WriteLine(
                    $"[Logger.LogEvent] emit failed ({ex.GetType().Name}: {ex.Message}) "
                        + $"while emitting '{name}'. Further '{ex.GetType().Name}' failures will be silent."
                );
            }
        }
        catch
        {
            // stderr unavailable; drop the report.
        }
    }

    public static void LogTotal(string prefix = "[Steam] Total time:")
    {
        lock (_lock)
        {
            var total = DateTime.Now - _startTime;
            Console.Write(prefix);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($" {total.TotalSeconds:F1}s");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Extracts the bracketed prefix from a log message (e.g. "[SteamAuth:A0]")
    /// to use as the per-account timing key. Falls back to "" for unprefixed messages.
    /// </summary>
    private static string ExtractPrefix(string message)
    {
        if (message.Length > 1 && message[0] == '[')
        {
            var end = message.IndexOf(']');
            if (end > 0)
            {
                return message[..(end + 1)];
            }
        }
        return "";
    }
}
