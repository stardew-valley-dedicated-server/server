using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// Source of diagnostic / annotation messages. Used by the diagnostic event
/// today; future annotation events (Plan 01) carry the same enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogSource>))]
public enum LogSource
{
    Runner,
    Framework,
    Fixture,
    Server,
    Game,
    Test,
}

/// <summary>
/// Log severity level.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Per-annotation severity for the human-readable narration plane (Plane C).
/// One value per <c>TestBase.Log*</c> method.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnnotationLevel>))]
public enum AnnotationLevel
{
    Info,
    Success,
    Warning,
    Error,
    Detail,
    Trace,
    Section,
}

/// <summary>
/// Producer of an annotation. Lets renderers tag entries by origin
/// (test body vs broker vs recording vs mod-forwarded vs setup).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnnotationSource>))]
public enum AnnotationSource
{
    Body,
    Broker,
    Recording,
    Mod,
    Setup,
}
