using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// A server or client container was created and is ready.
/// </summary>
public sealed record InstanceCreatedEvent(
    string InstanceId,
    string InstanceType,
    string ServerKey,
    string? VncUrl,
    string? Label,
    string HostId) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceCreated;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A client instance was leased to a test.
/// </summary>
public sealed record InstanceLeasedEvent(
    string InstanceId,
    string TestName,
    string? ServerInstanceId = null) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceLeased;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A client instance was attached to a server (records history on the server side).
/// On the wire, the server's id is emitted as <c>instanceId</c> and the client's id
/// as <c>clientInstanceId</c>.
/// </summary>
public sealed record InstanceClientAttachedEvent(
    [property: JsonPropertyName("instanceId")] string ServerInstanceId,
    string ClientInstanceId) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceClientAttached;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A client instance was returned to the pool.
/// </summary>
public sealed record InstanceReturnedEvent(string InstanceId) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceReturned;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A container instance was disposed.
/// </summary>
public sealed record InstanceDisposedEvent(string InstanceId) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceDisposed;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A container's full recording is available on the host.
/// </summary>
public sealed record InstanceRecordingEvent(
    string InstanceId,
    string RecordingPath) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceRecording;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A server was poisoned due to health-check failures.
/// </summary>
public sealed record InstancePoisonedEvent(
    string InstanceId,
    string Reason) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstancePoisoned;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A game session was established on an instance.
/// </summary>
public sealed record InstanceConnectedEvent(string InstanceId) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceConnected;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A game session ended on an instance.
/// </summary>
public sealed record InstanceDisconnectedEvent(string InstanceId) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceDisconnected;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Container performance stats for an instance.
/// Carries the full <see cref="InstanceStatsData"/> by reference; the
/// <see cref="InstanceStatsEventConverter"/> flattens its 18 fields onto the
/// wire alongside <c>instanceId</c> and <c>hostId</c>:
/// <c>{ event, timestamp, instanceId, hostId, cpuPercent, memoryMb, … }</c>.
/// </summary>
[JsonConverter(typeof(InstanceStatsEventConverter))]
public sealed record InstanceStatsEvent(
    string InstanceId,
    string HostId,
    InstanceStatsData Data) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.InstanceStats;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Flattens <see cref="InstanceStatsData"/> properties onto the top-level
/// instance_stats event object. Read deserializes the flat shape back into
/// the nested record.
/// </summary>
public sealed class InstanceStatsEventConverter : JsonConverter<InstanceStatsEvent>
{
    public override InstanceStatsEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string instanceId = "", hostId = "";
        var data = new InstanceStatsData();
        DateTime timestamp = DateTime.UtcNow;

        if (root.TryGetProperty("instanceId", out var iid)) instanceId = iid.GetString() ?? "";
        if (root.TryGetProperty("hostId", out var hid)) hostId = hid.GetString() ?? "";
        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String)
            timestamp = ts.GetDateTime();

        // Reconstruct InstanceStatsData by reading each property. Missing fields
        // fall back to record defaults (0 for value types, null for nullables).
        data = data with
        {
            CpuPercent = GetDouble(root, "cpuPercent"),
            MemoryMb = GetDouble(root, "memoryMb"),
            CpuCount = GetInt(root, "cpuCount"),
            TotalMemoryMb = GetDouble(root, "totalMemoryMb"),
            Fps = GetOptDouble(root, "fps"),
            Tps = GetOptDouble(root, "tps"),
            AvgTickMs = GetOptDouble(root, "avgTickMs"),
            GameMemoryMb = GetOptDouble(root, "gameMemoryMb"),
            TargetTps = GetOptInt(root, "targetTps"),
            TargetFps = GetOptInt(root, "targetFps"),
            GcRate = GetOptDouble(root, "gcRate"),
            PendingActions = GetOptInt(root, "pendingActions"),
            GameThreadWaitMs = GetOptDouble(root, "gameThreadWaitMs"),
            NetRxBytesPerSec = GetOptDouble(root, "netRxBytesPerSec"),
            NetTxBytesPerSec = GetOptDouble(root, "netTxBytesPerSec"),
            BlkReadBytesPerSec = GetOptDouble(root, "blkReadBytesPerSec"),
            BlkWriteBytesPerSec = GetOptDouble(root, "blkWriteBytesPerSec"),
            MemoryLimitMb = GetDouble(root, "memoryLimitMb"),
        };

        return new InstanceStatsEvent(instanceId, hostId, data) { Timestamp = timestamp };
    }

    public override void Write(Utf8JsonWriter writer, InstanceStatsEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("event", EventNames.InstanceStats);
        writer.WritePropertyName("timestamp");
        JsonSerializer.Serialize(writer, value.Timestamp, options);
        writer.WriteString("instanceId", value.InstanceId);
        writer.WriteString("hostId", value.HostId);

        var d = value.Data;
        writer.WriteNumber("cpuPercent", d.CpuPercent);
        writer.WriteNumber("memoryMb", d.MemoryMb);
        writer.WriteNumber("cpuCount", d.CpuCount);
        writer.WriteNumber("totalMemoryMb", d.TotalMemoryMb);
        WriteOptDouble(writer, "fps", d.Fps);
        WriteOptDouble(writer, "tps", d.Tps);
        WriteOptDouble(writer, "avgTickMs", d.AvgTickMs);
        WriteOptDouble(writer, "gameMemoryMb", d.GameMemoryMb);
        WriteOptInt(writer, "targetTps", d.TargetTps);
        WriteOptInt(writer, "targetFps", d.TargetFps);
        WriteOptDouble(writer, "gcRate", d.GcRate);
        WriteOptInt(writer, "pendingActions", d.PendingActions);
        WriteOptDouble(writer, "gameThreadWaitMs", d.GameThreadWaitMs);
        WriteOptDouble(writer, "netRxBytesPerSec", d.NetRxBytesPerSec);
        WriteOptDouble(writer, "netTxBytesPerSec", d.NetTxBytesPerSec);
        WriteOptDouble(writer, "blkReadBytesPerSec", d.BlkReadBytesPerSec);
        WriteOptDouble(writer, "blkWriteBytesPerSec", d.BlkWriteBytesPerSec);
        writer.WriteNumber("memoryLimitMb", d.MemoryLimitMb);

        writer.WriteEndObject();
    }

    private static void WriteOptDouble(Utf8JsonWriter w, string name, double? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value);
    }

    private static void WriteOptInt(Utf8JsonWriter w, string name, int? v)
    {
        if (v.HasValue) w.WriteNumber(name, v.Value);
    }

    private static double GetDouble(JsonElement el, string p)
        => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static int GetInt(JsonElement el, string p)
        => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double? GetOptDouble(JsonElement el, string p)
        => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? GetOptInt(JsonElement el, string p)
        => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
