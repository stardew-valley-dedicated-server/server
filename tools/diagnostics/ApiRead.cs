using System.Text.Json;

namespace Diagnostics;

/// <summary>How one endpoint read ended.</summary>
internal enum ReadStatus
{
    /// <summary>Never requested (the API is disabled, so no collection ran).</summary>
    NotAttempted,

    /// <summary>Body returned and parsed.</summary>
    Ok,

    /// <summary>Listener answered with an error status — it is up, so this is not a connection failure.</summary>
    HttpError,

    /// <summary>Couldn't reach the listener at all: refused or timed out.</summary>
    Unreachable,

    /// <summary>Answered, but the body wasn't valid JSON.</summary>
    Malformed,
}

/// <summary>
/// One endpoint's result: the parsed body when it succeeded, otherwise why it didn't. Rendering a
/// section and explaining an empty one draw on the same value, so status never lives apart from the
/// data. <see cref="Json"/> is meaningful only when <see cref="Ok"/>; on failure it's an Undefined
/// element, which every reader in <see cref="Diagnostics.Json"/> treats as empty.
/// </summary>
internal sealed record ApiRead(ReadStatus Status, JsonElement Json, string? Reason)
{
    public static readonly ApiRead NotAttempted = new(ReadStatus.NotAttempted, default, null);

    public static readonly ApiRead Malformed = new(ReadStatus.Malformed, default, "invalid JSON");

    public bool Ok => Status == ReadStatus.Ok;

    /// <summary>The reason as a parenthesized suffix for the report, empty when there isn't one.</summary>
    public string Detail => Reason is null ? "" : $" ({Reason})";

    public static ApiRead Parsed(JsonElement json) => new(ReadStatus.Ok, json, null);

    public static ApiRead HttpError(int statusCode) =>
        new(ReadStatus.HttpError, default, $"HTTP {statusCode}");

    public static ApiRead Unreachable(Exception ex) =>
        new(ReadStatus.Unreachable, default, ex.GetType().Name);
}
