using System.Net.Http.Headers;
using System.Text.Json;

namespace Diagnostics;

/// <summary>
/// A snapshot of the server's local HTTP API: <see cref="CollectAsync"/> reads every endpoint once,
/// then each property hands back that endpoint's <see cref="ApiRead"/> — the parsed body, or why it
/// couldn't be read. Bodies are parsed here rather than per field, and a failure becomes a failed
/// <see cref="ApiRead"/> rather than an exception, so no section can throw or re-parse.
/// </summary>
internal sealed class ServerClient
{
    private const string HealthPath = "/health";
    private const string StatusPath = "/status";
    private const string StatsPath = "/stats";
    private const string DiagnosticsStatePath = "/diagnostics/state";
    private const string SettingsPath = "/settings";
    private const string PlayersPath = "/players";
    private const string FarmhandsPath = "/farmhands";
    private const string CabinsPath = "/cabins";

    /// <summary>
    /// Collection order. /health goes first: it answers without the game thread, so it is the
    /// one read that survives a frozen server.
    /// </summary>
    private static readonly string[] Paths =
    {
        HealthPath,
        StatusPath,
        StatsPath,
        DiagnosticsStatePath,
        SettingsPath,
        PlayersPath,
        FarmhandsPath,
        CabinsPath,
    };

    private readonly Dictionary<string, ApiRead> _reads = new();

    public ApiRead Health => Read(HealthPath);
    public ApiRead Status => Read(StatusPath);
    public ApiRead Stats => Read(StatsPath);
    public ApiRead DiagnosticsState => Read(DiagnosticsStatePath);
    public ApiRead Settings => Read(SettingsPath);
    public ApiRead Players => Read(PlayersPath);
    public ApiRead Farmhands => Read(FarmhandsPath);
    public ApiRead Cabins => Read(CabinsPath);

    /// <summary>The endpoints that didn't return a body, with each one's reason.</summary>
    public IReadOnlyList<(string Path, ApiRead Read)> Failures =>
        _reads.Where(kv => !kv.Value.Ok).Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>GETs every endpoint, reporting progress via <paramref name="onEndpoint"/>.</summary>
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

        foreach (var path in Paths)
        {
            onEndpoint(path);
            _reads[path] = await ReadAsync(client, path);
        }
    }

    /// <summary>
    /// NotAccepting when every attempted read was unreachable (the listener isn't up), NoWorldLoaded
    /// when it answered but /status reports no loaded save (isOnline is false while booting and
    /// through runtime day/farm-map transitions), else Reachable.
    /// </summary>
    public ServerState DeriveState()
    {
        if (_reads.Count > 0 && _reads.Values.All(r => r.Status == ReadStatus.Unreachable))
        {
            return ServerState.NotAccepting;
        }
        if (Status.Ok && !Json.FieldBool(Status.Json, "isOnline"))
        {
            return ServerState.NoWorldLoaded;
        }
        return ServerState.Reachable;
    }

    private ApiRead Read(string path) => _reads.GetValueOrDefault(path) ?? ApiRead.NotAttempted;

    private static async Task<ApiRead> ReadAsync(HttpClient client, string path)
    {
        try
        {
            var response = await client.GetAsync(Config.BaseUrl + path);
            if (!response.IsSuccessStatusCode)
            {
                return ApiRead.HttpError((int)response.StatusCode);
            }
            var body = await response.Content.ReadAsStringAsync();
            // Clone detaches the root from the document being disposed, so it outlives this scope.
            using var doc = JsonDocument.Parse(body);
            return ApiRead.Parsed(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return ApiRead.Malformed;
        }
        catch (Exception ex)
        {
            return ApiRead.Unreachable(ex);
        }
    }
}
