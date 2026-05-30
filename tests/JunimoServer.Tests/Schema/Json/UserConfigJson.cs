using System.Text.Json;
using System.Text.Json.Nodes;

namespace JunimoServer.Tests.Schema.Json;

/// <summary>
/// Parsing for hand-edited environment-variable JSON (STEAM_ACCOUNTS,
/// SDVD_DOCKER_HOSTS, future env-var configs). Tolerates trailing commas and
/// <c>//</c> comments — same defaults as <c>appsettings.json</c>.
///
/// <para>Three modes for three failure semantics:
///   <see cref="ParseArrayStrict"/> throws actionable on malformed (named env var);
///   <see cref="CountArrayTolerant"/> returns 0 on any failure (diagnostic count);
///   <see cref="DeserializeArrayStrict{T}"/> throws actionable typed deserialize.</para>
/// </summary>
public static class UserConfigJson
{
    /// <summary>
    /// Raw options for the rare site that needs its own projection
    /// (DockerImageBuilder's tolerant index-0 user/pass/token read). Prefer
    /// the named methods below; reach for this only when the projection
    /// doesn't fit one of them.
    /// </summary>
    public static readonly JsonDocumentOptions Document = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions Serializer = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parse a JSON-array env var. Returns the array's non-null entries.
    /// Returns empty for null/whitespace. Throws
    /// <see cref="InvalidOperationException"/> with an actionable message
    /// naming <paramref name="envVarName"/> on malformed JSON or non-array root.
    /// </summary>
    /// <param name="expectedShapeHint">
    /// Optional element-shape description appended to the error message — e.g.
    /// <c>"{user, pass[, refreshToken]}"</c>. Helps operators correct malformed
    /// input without consulting docs.
    /// </param>
    public static IReadOnlyList<JsonNode> ParseArrayStrict(
        string envVarName, string? json, string? expectedShapeHint = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<JsonNode>();

        var hint = expectedShapeHint != null
            ? $" Expected a JSON array of {expectedShapeHint} objects."
            : "";

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: Document);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{envVarName} is set but is not valid JSON: {ex.Message}.{hint} " +
                "Trailing commas and // comments are allowed.", ex);
        }

        if (node is not JsonArray arr)
            throw new InvalidOperationException(
                $"{envVarName} is set but is not a JSON array.{hint}");

        var nodes = new List<JsonNode>(arr.Count);
        for (var i = 0; i < arr.Count; i++)
        {
            var entry = arr[i];
            if (entry != null) nodes.Add(entry);
        }
        return nodes;
    }

    /// <summary>
    /// Returns the array length, or 0 on null/empty/non-array/parse-failure.
    /// For diagnostic counts only; do not gate behavior on this value.
    /// </summary>
    public static int CountArrayTolerant(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json, Document);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Typed array deserialize. Throws <see cref="InvalidOperationException"/>
    /// with an actionable message naming <paramref name="envVarName"/> on
    /// parse failure. Returns null for null/whitespace input.
    /// </summary>
    /// <param name="expectedShapeHint">
    /// Optional element-shape description appended to the error message — e.g.
    /// <c>"{id, serverSlots, clientSlots, [endpoint], [sshKey]}"</c>.
    /// </param>
    public static T[]? DeserializeArrayStrict<T>(
        string envVarName, string? json, string? expectedShapeHint = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var hint = expectedShapeHint != null
            ? $" Expected: an array of {expectedShapeHint} objects."
            : "";
        try
        {
            return JsonSerializer.Deserialize<T[]>(json, Serializer);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{envVarName} is not valid JSON: {ex.Message}.{hint}", ex);
        }
    }
}
