using System;
using System.Collections.Concurrent;
using System.Text.Json;

namespace JunimoServer.Services.Diagnostics;

/// <summary>
/// Structured event emitter for the server mod.
///
/// Envelope schema: see docs/developers/events-schema.md
///
/// <para>
/// Transport: each event is one <c>Console.Out.WriteLine</c> prefixed
/// with <c>SDVD_EVENT </c>. The host-side <c>SimpleContainerLogStreamer</c>
/// parses the prefix and forwards the payload to <c>infrastructure.jsonl</c>,
/// adding <c>forwardedVia</c>.
/// </para>
///
/// <para>
/// <see cref="Emit"/> never throws: a crashed game is worse than a
/// dropped event. The first failure of each exception type is reported
/// on <c>Console.Error</c>; subsequent failures of the same type are
/// silent. The error path does not re-enter the emitter.
/// </para>
/// </summary>
public static class ModEventLog
{
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Exception types already reported on stderr. Used to emit
    /// the first failure of each type and suppress the rest.</summary>
    private static readonly ConcurrentDictionary<Type, byte> _reportedFailures = new();

    /// <summary>Emits a structured event. Thread-safe, never throws.</summary>
    /// <param name="eventType">Event name; see the catalog on <c>InfrastructureEventLog</c>.</param>
    /// <param name="data">Payload object. May be null.</param>
    /// <param name="tickMs">Optional <c>Game1.ticks</c> value. Caller-supplied
    /// because reading <c>Game1.ticks</c> off the game thread is unsafe.</param>
    public static void Emit(string eventType, object? data = null, int? tickMs = null)
    {
        try
        {
            var entry = new
            {
                ts = DateTime.UtcNow,
                requestId = ModRequestContext.RequestId,
                service = "server",
                tickMs,
                @event = eventType,
                data,
            };
            var json = JsonSerializer.Serialize(entry, _jsonOptions);

            lock (_lock)
            {
                Console.Out.WriteLine("SDVD_EVENT " + json);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(eventType, ex);
        }
    }

    /// <summary>
    /// Reports an emission failure once per exception type on <c>Console.Error</c>.
    /// Must not re-enter <see cref="Emit"/> — a broken sink would loop.
    /// </summary>
    private static void ReportFailure(string eventType, Exception ex)
    {
        try
        {
            if (_reportedFailures.TryAdd(ex.GetType(), 0))
            {
                Console.Error.WriteLine(
                    $"[ModEventLog] emit failed ({ex.GetType().Name}: {ex.Message}) "
                        + $"while emitting '{eventType}'. Further '{ex.GetType().Name}' failures will be silent."
                );
            }
        }
        catch
        {
            // stderr unavailable; drop the report.
        }
    }
}
