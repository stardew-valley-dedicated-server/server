using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Json;

/// <summary>
/// Serialization for persisted run artifacts — <c>summary.json</c>,
/// <c>ctrf-report.json</c>, <c>run-metadata.json</c>, <c>snapshot.json</c>,
/// <c>failure.json</c>. Pretty-printed for human + LLM readers; camelCase +
/// omit-nulls match the diagnostic-emit shape so operators can copy field
/// names between artifacts and live logs.
/// </summary>
public static class ArtifactPrettyJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    public static Task WriteAsync(string path, object? value) =>
        File.WriteAllTextAsync(path, Serialize(value));

    public static void Write(string path, object? value) =>
        File.WriteAllText(path, Serialize(value));
}
