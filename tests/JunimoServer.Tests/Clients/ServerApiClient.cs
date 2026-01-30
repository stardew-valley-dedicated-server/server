using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Clients;

/// <summary>
/// Server status data returned by the /status endpoint.
/// Mirrors the ServerStatus class from ApiService.
/// </summary>
public class ServerStatus
{
    [JsonPropertyName("playerCount")]
    public int PlayerCount { get; set; }

    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("inviteCode")]
    public string InviteCode { get; set; } = string.Empty;

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = string.Empty;

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }

    [JsonPropertyName("isReady")]
    public bool IsReady { get; set; }

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; set; } = string.Empty;

    // Game state fields
    [JsonPropertyName("farmName")]
    public string FarmName { get; set; } = string.Empty;

    [JsonPropertyName("day")]
    public int Day { get; set; }

    [JsonPropertyName("season")]
    public string Season { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("timeOfDay")]
    public int TimeOfDay { get; set; }
}

/// <summary>
/// Player info returned by the /players endpoint.
/// </summary>
public class PlayerInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }
}

/// <summary>
/// Response from the /players endpoint.
/// </summary>
public class PlayersResponse
{
    [JsonPropertyName("players")]
    public List<PlayerInfo> Players { get; set; } = new();
}

/// <summary>
/// Response from the /invite-code endpoint.
/// </summary>
public class InviteCodeResponse
{
    [JsonPropertyName("inviteCode")]
    public string? InviteCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from the /health endpoint.
/// </summary>
public class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>
/// Response from farmhand operations.
/// </summary>
public class FarmhandOperationResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Information about a farmhand slot.
/// </summary>
public class ServerFarmhandInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isCustomized")]
    public bool IsCustomized { get; set; }
}

/// <summary>
/// Response from /farmhands endpoint.
/// </summary>
public class ServerFarmhandsResponse
{
    [JsonPropertyName("farmhands")]
    public List<ServerFarmhandInfo> Farmhands { get; set; } = new();
}

/// <summary>
/// Response from /rendering GET endpoint.
/// </summary>
public class RenderingStatusResponse
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>
/// Response from /rendering POST endpoint.
/// </summary>
public class RenderingToggleResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /time POST endpoint.
/// </summary>
public class TimeSetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("timeOfDay")]
    public int TimeOfDay { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /settings GET endpoint.
/// </summary>
public class SettingsResponse
{
    [JsonPropertyName("game")]
    public GameSettingsInfo Game { get; set; } = new();

    [JsonPropertyName("server")]
    public ServerRuntimeSettingsInfo Server { get; set; } = new();
}

/// <summary>
/// Game creation settings from server-settings.json.
/// </summary>
public class GameSettingsInfo
{
    [JsonPropertyName("farmName")]
    public string FarmName { get; set; } = string.Empty;

    [JsonPropertyName("farmType")]
    public int FarmType { get; set; }

    [JsonPropertyName("profitMargin")]
    public float ProfitMargin { get; set; }

    [JsonPropertyName("startingCabins")]
    public int StartingCabins { get; set; }

    [JsonPropertyName("spawnMonstersAtNight")]
    public string SpawnMonstersAtNight { get; set; } = string.Empty;
}

/// <summary>
/// Server runtime settings from server-settings.json.
/// </summary>
public class ServerRuntimeSettingsInfo
{
    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("cabinStrategy")]
    public string CabinStrategy { get; set; } = string.Empty;

    [JsonPropertyName("separateWallets")]
    public bool SeparateWallets { get; set; }

    [JsonPropertyName("existingCabinBehavior")]
    public string ExistingCabinBehavior { get; set; } = string.Empty;
}

/// <summary>
/// Response from /cabins GET endpoint.
/// </summary>
public class CabinsResponse
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = string.Empty;

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("assignedCount")]
    public int AssignedCount { get; set; }

    [JsonPropertyName("availableCount")]
    public int AvailableCount { get; set; }

    [JsonPropertyName("cabins")]
    public List<CabinInfoResponse> Cabins { get; set; } = new();
}

/// <summary>
/// Information about a single cabin.
/// </summary>
public class CabinInfoResponse
{
    [JsonPropertyName("tileX")]
    public int TileX { get; set; }

    [JsonPropertyName("tileY")]
    public int TileY { get; set; }

    [JsonPropertyName("isHidden")]
    public bool IsHidden { get; set; }

    [JsonPropertyName("ownerId")]
    public long OwnerId { get; set; }

    [JsonPropertyName("ownerName")]
    public string OwnerName { get; set; } = string.Empty;

    [JsonPropertyName("isAssigned")]
    public bool IsAssigned { get; set; }
}

/// <summary>
/// HTTP client for the JunimoServer API.
/// Default port is 8080 (configurable via API_PORT env var in the server).
/// </summary>
public class ServerApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public ServerApiClient(string baseUrl = "http://localhost:8080")
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };
    }

    /// <summary>
    /// Gets the server status including player count, invite code, game state, etc.
    /// GET /status
    /// </summary>
    public async Task<ServerStatus?> GetStatus()
    {
        var response = await _httpClient.GetAsync("/status");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerStatus>();
    }

    /// <summary>
    /// Gets the list of connected players.
    /// GET /players
    /// </summary>
    public async Task<PlayersResponse?> GetPlayers()
    {
        var response = await _httpClient.GetAsync("/players");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlayersResponse>();
    }

    /// <summary>
    /// Gets the current invite code.
    /// GET /invite-code
    /// </summary>
    public async Task<InviteCodeResponse?> GetInviteCode()
    {
        var response = await _httpClient.GetAsync("/invite-code");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InviteCodeResponse>();
    }

    /// <summary>
    /// Health check endpoint.
    /// GET /health
    /// </summary>
    public async Task<HealthResponse?> GetHealth()
    {
        var response = await _httpClient.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HealthResponse>();
    }

    /// <summary>
    /// Gets the OpenAPI specification.
    /// GET /swagger/v1/swagger.json
    /// </summary>
    public async Task<string> GetOpenApiSpec()
    {
        var response = await _httpClient.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Gets the current server settings.
    /// GET /settings
    /// </summary>
    public async Task<SettingsResponse?> GetSettings()
    {
        var response = await _httpClient.GetAsync("/settings");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SettingsResponse>();
    }

    /// <summary>
    /// Gets the current cabin state and positions.
    /// GET /cabins
    /// </summary>
    public async Task<CabinsResponse?> GetCabins()
    {
        var response = await _httpClient.GetAsync("/cabins");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CabinsResponse>();
    }

    /// <summary>
    /// Gets all farmhand slots.
    /// GET /farmhands
    /// </summary>
    public async Task<ServerFarmhandsResponse?> GetFarmhands()
    {
        var response = await _httpClient.GetAsync("/farmhands");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerFarmhandsResponse>();
    }

    /// <summary>
    /// Deletes a farmhand by name.
    /// DELETE /farmhands?name=X
    /// </summary>
    public async Task<FarmhandOperationResponse?> DeleteFarmhand(string name)
    {
        var response = await _httpClient.DeleteAsync($"/farmhands?name={Uri.EscapeDataString(name)}");
        return await response.Content.ReadFromJsonAsync<FarmhandOperationResponse>();
    }

    /// <summary>
    /// Gets the current rendering status.
    /// GET /rendering
    /// </summary>
    public async Task<RenderingStatusResponse?> GetRendering()
    {
        var response = await _httpClient.GetAsync("/rendering");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RenderingStatusResponse>();
    }

    /// <summary>
    /// Sets the rendering state.
    /// POST /rendering?enabled=true|false
    /// </summary>
    public async Task<RenderingToggleResponse?> SetRendering(bool enabled)
    {
        var response = await _httpClient.PostAsync($"/rendering?enabled={enabled.ToString().ToLowerInvariant()}", null);
        return await response.Content.ReadFromJsonAsync<RenderingToggleResponse>();
    }

    /// <summary>
    /// Sets the game time of day.
    /// POST /time?value=XXXX
    /// </summary>
    public async Task<TimeSetResponse?> SetTime(int value)
    {
        var response = await _httpClient.PostAsync($"/time?value={value}", null);
        return await response.Content.ReadFromJsonAsync<TimeSetResponse>();
    }

    /// <summary>
    /// Posts to the rendering endpoint without the enabled parameter (for error testing).
    /// POST /rendering
    /// </summary>
    public async Task<RenderingToggleResponse?> PostRenderingRaw()
    {
        var response = await _httpClient.PostAsync("/rendering", null);
        return await response.Content.ReadFromJsonAsync<RenderingToggleResponse>();
    }

    /// <summary>
    /// Waits for the server to come online with a valid invite code.
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">Time between status checks</param>
    /// <returns>The server status once online, or null if timeout</returns>
    public async Task<ServerStatus?> WaitForServerOnline(TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;
        string lastReason = "no attempts made";

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            try
            {
                var status = await GetStatus();
                if (status?.IsOnline == true && status.IsReady && !string.IsNullOrEmpty(status.InviteCode))
                {
                    Console.WriteLine($"[Setup] Server online and ready after {attempt} attempts");
                    return status;
                }

                lastReason = $"IsOnline={status?.IsOnline}, IsReady={status?.IsReady}, InviteCode='{status?.InviteCode ?? "(null)"}'";
            }
            catch (Exception ex)
            {
                lastReason = $"{ex.GetType().Name}: {ex.Message}";
            }

            // Log progress every 10 attempts
            if (attempt % 10 == 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                Console.WriteLine($"[Setup] Waiting for server: attempt {attempt}, {remaining.TotalSeconds:0}s remaining - {lastReason}");
            }

            await Task.Delay(interval);
        }

        Console.WriteLine($"[Setup] Server wait timed out after {attempt} attempts. Last: {lastReason}");
        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
