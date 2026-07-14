using System.Net.Http.Headers;

namespace Diagnostics;

/// <summary>
/// Collects raw JSON from the server's local HTTP API. Records the responses plus why any read
/// failed, so <see cref="DeriveState"/> can tell "listener not up yet" (every read refused) apart
/// from an HTTP error status. Reads are resilient: a failure yields a null body, never an exception.
/// </summary>
internal sealed class ServerClient
{
    private readonly Dictionary<string, string?> _responses = new();
    private readonly List<string> _failed = new();
    private int _attempted;
    private int _connectionFailures;

    public IReadOnlyList<string> FailedReads => _failed;

    public string? Get(string path) => _responses.GetValueOrDefault(path);

    /// <summary>GETs every configured endpoint, reporting progress via <paramref name="onEndpoint"/>.</summary>
    public async Task CollectAsync(Action<string> onEndpoint)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrEmpty(Config.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Config.ApiKey
            );
        }

        foreach (var path in Config.Endpoints)
        {
            onEndpoint(path);
            _responses[path] = await TryGetAsync(client, path);
        }
    }

    /// <summary>
    /// NotAccepting when every read hit a connection failure (the listener isn't up), NoWorldLoaded
    /// when the listener answered but /status reports no loaded save (isOnline is false while booting
    /// and through runtime day/farm-map transitions), else Reachable.
    /// </summary>
    public ServerState DeriveState()
    {
        if (_attempted > 0 && _connectionFailures == _attempted)
        {
            return ServerState.NotAccepting;
        }
        if (Get("/status") is { } status && !Json.Bool(status, "isOnline"))
        {
            return ServerState.NoWorldLoaded;
        }
        return ServerState.Reachable;
    }

    private async Task<string?> TryGetAsync(HttpClient client, string path)
    {
        _attempted++;
        try
        {
            var response = await client.GetAsync(Config.BaseUrl + path);
            if (!response.IsSuccessStatusCode)
            {
                // An HTTP error status means the listener is up — not a connection failure.
                _failed.Add($"{path} (HTTP {(int)response.StatusCode})");
                return null;
            }
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            // Couldn't reach the listener at all (refused / timed out) — the "still starting" signal.
            _connectionFailures++;
            _failed.Add($"{path} ({ex.GetType().Name})");
            return null;
        }
    }
}
