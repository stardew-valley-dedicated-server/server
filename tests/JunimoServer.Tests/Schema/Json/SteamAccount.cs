namespace JunimoServer.Tests.Schema.Json;

/// <summary>
/// One entry of the <c>STEAM_ACCOUNTS</c> env JSON — the canonical field names
/// for every test-side consumer. They mirror the consuming sidecar's
/// deserialization contract (<c>tools/steam-service/Program.cs</c>,
/// <c>SteamAccountConfig</c>): <c>"user"</c> plus either <c>"pass"</c> or
/// <c>"token"</c>. The sidecar binds case-sensitively and ignores unknown keys,
/// so names outside this record never reach Steam — keep in sync.
/// </summary>
public sealed record SteamAccount(string User, string Pass, string Token)
{
    /// <summary>Element-shape hint for <see cref="UserConfigJson.ParseArrayStrict"/> error messages.</summary>
    public const string ShapeHint = "{user, pass[, token]}";

    /// <summary>The entry's non-empty secret values, for masking/redaction consumers.</summary>
    public IEnumerable<string> SecretValues
    {
        get
        {
            foreach (var value in new[] { User, Pass, Token })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>
    /// Parses <c>STEAM_ACCOUNTS</c> JSON into entries; missing fields become
    /// <c>""</c>. Empty for null/whitespace input; throws
    /// <see cref="InvalidOperationException"/> on malformed JSON
    /// (<see cref="UserConfigJson.ParseArrayStrict"/> semantics).
    /// </summary>
    public static IReadOnlyList<SteamAccount> ParseList(string? steamAccountsJson)
    {
        var nodes = UserConfigJson.ParseArrayStrict("STEAM_ACCOUNTS", steamAccountsJson, ShapeHint);
        var accounts = new List<SteamAccount>(nodes.Count);
        foreach (var node in nodes)
        {
            accounts.Add(
                new SteamAccount(
                    node["user"]?.GetValue<string>() ?? "",
                    node["pass"]?.GetValue<string>() ?? "",
                    node["token"]?.GetValue<string>() ?? ""
                )
            );
        }

        return accounts;
    }
}
