using System.Text.Json;

namespace Diagnostics;

/// <summary>
/// Probes the steam-auth sidecar's /health from inside the container (same URL the server uses).
/// Reports reachability and a COUNT of configured / logged-in accounts — never usernames or
/// steam_ids, which are sensitive and must not reach the report. Never throws.
/// </summary>
internal static class SteamAuthProbe
{
    public static async Task<string> ProbeAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"{Config.SteamAuthUrl}/health");
            if (!response.IsSuccessStatusCode)
            {
                return $"reachable, but /health returned HTTP {(int)response.StatusCode}";
            }
            var (total, loggedIn) = CountAccounts(await response.Content.ReadAsStringAsync());
            // Derive from the count, not the sidecar's top-level `logged_in`: that flag is
            // All(accounts, …), vacuously true with zero accounts — it would read as "logged in"
            // on a server that has no Steam accounts configured at all.
            return total == 0
                ? "reachable, 0 Steam accounts configured"
                : $"reachable, {loggedIn}/{total} Steam account(s) logged in";
        }
        catch (Exception ex)
        {
            return $"UNREACHABLE ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Reads ONLY the `accounts` array length and each entry's `logged_in` bool — deliberately never
    /// `username` or `steam_id`, so no account identity can leak. Returns (0, 0) on any parse issue.
    /// </summary>
    private static (int total, int loggedIn) CountAccounts(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (
                !doc.RootElement.TryGetProperty("accounts", out var accounts)
                || accounts.ValueKind != JsonValueKind.Array
            )
            {
                return (0, 0);
            }
            var loggedIn = accounts
                .EnumerateArray()
                .Count(a =>
                    a.TryGetProperty("logged_in", out var flag)
                    && flag.ValueKind == JsonValueKind.True
                );
            return (accounts.GetArrayLength(), loggedIn);
        }
        catch
        {
            return (0, 0);
        }
    }
}
