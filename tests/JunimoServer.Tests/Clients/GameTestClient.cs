using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Clients;

#region Request DTOs

public class NavigateParams
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

public class TabParams
{
    [JsonPropertyName("tab")]
    public int Tab { get; set; }
}

public class InviteCodeParams
{
    [JsonPropertyName("inviteCode")]
    public string InviteCode { get; set; } = string.Empty;
}

public class SelectFarmhandParams
{
    [JsonPropertyName("slotIndex")]
    public int SlotIndex { get; set; }
}

public class CustomizeCharacterParams
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("favoriteThing")]
    public string FavoriteThing { get; set; } = string.Empty;
}

#endregion

#region Response DTOs

public class CapturedError
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; set; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] {Source}: {ExceptionType ?? "Error"} - {Message}";
}

public class ErrorsResponse
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("errors")]
    public List<CapturedError> Errors { get; set; } = new();
}

public class NavigationResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class WaitResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("waitedMs")]
    public int WaitedMs { get; set; }
}

public class MenuInfo
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("subMenu")]
    public MenuInfo? SubMenu { get; set; }
}

public class FarmhandSlot
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isCustomized")]
    public bool IsCustomized { get; set; }

    [JsonPropertyName("isEmpty")]
    public bool IsEmpty { get; set; }
}

public class FarmhandsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("slots")]
    public List<FarmhandSlot> Slots { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class CharacterInfo
{
    [JsonPropertyName("inCharacterMenu")]
    public bool InCharacterMenu { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("favoriteThing")]
    public string? FavoriteThing { get; set; }

    [JsonPropertyName("canConfirm")]
    public bool CanConfirm { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class CustomizeResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class SleepResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class ChatResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class ChatMessage
{
    /// <summary>
    /// The text content of the message.
    /// Note: Named "text" in JSON to match test-client's ChatMessageInfo.Text property.
    /// </summary>
    [JsonPropertyName("text")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The color of the message as a hex string (e.g., "#FFFFFF").
    /// </summary>
    [JsonPropertyName("colorHex")]
    public string ColorHex { get; set; } = string.Empty;

    /// <summary>
    /// The alpha/opacity of the message.
    /// </summary>
    [JsonPropertyName("alpha")]
    public float Alpha { get; set; }
}

public class ChatHistoryResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class GameStateResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("isInGame")]
    public bool IsInGame { get; set; }

    [JsonPropertyName("playerName")]
    public string? PlayerName { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /status endpoint containing menu, connection, and farmer info.
/// </summary>
public class StatusResponse
{
    [JsonPropertyName("menu")]
    public MenuInfo? Menu { get; set; }

    [JsonPropertyName("connection")]
    public ConnectionStatusInfo? Connection { get; set; }

    [JsonPropertyName("farmer")]
    public FarmerInfoResponse? Farmer { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

public class ConnectionStatusInfo
{
    [JsonPropertyName("isMultiplayer")]
    public bool IsMultiplayer { get; set; }

    [JsonPropertyName("isClient")]
    public bool IsClient { get; set; }

    [JsonPropertyName("isServer")]
    public bool IsServer { get; set; }

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("worldReady")]
    public bool WorldReady { get; set; }

    [JsonPropertyName("hasLoadedGame")]
    public bool HasLoadedGame { get; set; }
}

public class FarmerInfoResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("farmName")]
    public string FarmName { get; set; } = string.Empty;

    [JsonPropertyName("currentLocation")]
    public string? CurrentLocation { get; set; }

    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;
}

#endregion

#region Sub-Clients

public class CoopClient
{
    private readonly GameTestClient _client;

    public CoopClient(GameTestClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Switch to a specific tab in the coop menu.
    /// POST /coop/tab
    /// </summary>
    public Task<NavigationResult?> Tab(int tab)
    {
        return _client.PostAsync<NavigationResult>("/coop/tab", new TabParams { Tab = tab });
    }

    /// <summary>
    /// Open the invite code input dialog.
    /// POST /coop/invite-code/open
    /// </summary>
    public Task<NavigationResult?> OpenInviteCodeMenu()
    {
        return _client.PostAsync<NavigationResult>("/coop/invite-code/open", new { });
    }

    /// <summary>
    /// Submit invite code in the text input menu.
    /// POST /coop/invite-code/submit
    /// </summary>
    public Task<NavigationResult?> SubmitInviteCode(string inviteCode)
    {
        return _client.PostAsync<NavigationResult>("/coop/invite-code/submit", new InviteCodeParams { InviteCode = inviteCode });
    }

    /// <summary>
    /// Join a server via LAN/IP address.
    /// POST /coop/join-lan?address=X
    /// </summary>
    /// <param name="address">Server address in "host:port" or "host" format.</param>
    public Task<NavigationResult?> JoinLan(string address = "localhost")
    {
        return _client.PostAsync<NavigationResult>($"/coop/join-lan?address={Uri.EscapeDataString(address)}", new { });
    }
}

public class FarmhandClient
{
    private readonly GameTestClient _client;

    public FarmhandClient(GameTestClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Get available farmhand slots.
    /// GET /farmhands
    /// </summary>
    public Task<FarmhandsResponse?> GetSlots()
    {
        return _client.GetAsync<FarmhandsResponse>("/farmhands");
    }

    /// <summary>
    /// Select a farmhand slot by index.
    /// POST /farmhands/select
    /// </summary>
    public Task<NavigationResult?> Select(int slotIndex)
    {
        return _client.PostAsync<NavigationResult>("/farmhands/select", new SelectFarmhandParams { SlotIndex = slotIndex });
    }
}

public class CharacterClient
{
    private readonly GameTestClient _client;

    public CharacterClient(GameTestClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Get current character customization state.
    /// GET /character
    /// </summary>
    public Task<CharacterInfo?> GetInfo()
    {
        return _client.GetAsync<CharacterInfo>("/character");
    }

    /// <summary>
    /// Set character name and favorite thing.
    /// POST /character/customize
    /// </summary>
    public Task<CustomizeResult?> Customize(string name, string favoriteThing)
    {
        return _client.PostAsync<CustomizeResult>("/character/customize",
            new CustomizeCharacterParams { Name = name, FavoriteThing = favoriteThing });
    }

    /// <summary>
    /// Confirm character creation by clicking OK.
    /// POST /character/confirm
    /// </summary>
    public Task<CustomizeResult?> Confirm()
    {
        return _client.PostAsync<CustomizeResult>("/character/confirm", new { });
    }
}

public class ActionsClient
{
    private readonly GameTestClient _client;

    public ActionsClient(GameTestClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Make the player go to sleep.
    /// POST /actions/sleep
    /// </summary>
    public Task<SleepResult?> Sleep()
    {
        return _client.PostAsync<SleepResult>("/actions/sleep", new { });
    }
}

public class ChatClient
{
    private readonly GameTestClient _client;

    public ChatClient(GameTestClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Send a chat message to all players.
    /// POST /chat/send
    /// </summary>
    public Task<ChatResult?> Send(string message)
    {
        return _client.PostAsync<ChatResult>("/chat/send", new { message });
    }

    /// <summary>
    /// Get recent chat messages.
    /// GET /chat/history
    /// </summary>
    public Task<ChatHistoryResult?> GetHistory(int count = 10)
    {
        return _client.GetAsync<ChatHistoryResult>($"/chat/history?count={count}");
    }

    /// <summary>
    /// Wait for a chat message containing any of the specified keywords (case-insensitive).
    /// Polls until a matching message is found or timeout expires.
    /// </summary>
    /// <param name="keywords">Keywords to search for (any match succeeds)</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="historySize">Number of recent messages to check</param>
    /// <returns>The chat history containing the matching message, or null if timed out</returns>
    public async Task<ChatHistoryResult?> WaitForMessageContainingAsync(
        string[] keywords,
        TimeSpan? timeout = null,
        int historySize = 10)
    {
        var effectiveTimeout = timeout ?? Helpers.TestTimings.ChatCommandTimeout;
        ChatHistoryResult? result = null;

        var found = await Helpers.PollingHelper.WaitUntilAsync(async () =>
        {
            result = await GetHistory(historySize);
            return result?.Messages?.Any(m =>
                keywords.Any(k => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))) == true;
        }, effectiveTimeout);

        return found ? result : null;
    }

    /// <summary>
    /// Wait for a chat message containing the specified keyword (case-insensitive).
    /// Polls until a matching message is found or timeout expires.
    /// </summary>
    public Task<ChatHistoryResult?> WaitForMessageContainingAsync(
        string keyword,
        TimeSpan? timeout = null,
        int historySize = 10)
    {
        return WaitForMessageContainingAsync(new[] { keyword }, timeout, historySize);
    }
}

public class WaitClient
{
    private readonly GameTestClient _client;
    private readonly TimeSpan _defaultTimeout;

    public WaitClient(GameTestClient client, TimeSpan? defaultTimeout = null)
    {
        _client = client;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    private int ToMs(TimeSpan? timeout) => (int)(timeout ?? _defaultTimeout).TotalMilliseconds;

    /// <summary>
    /// Wait for a specific menu type.
    /// GET /wait/menu?type=X
    /// </summary>
    public Task<WaitResult?> ForMenu(string menuType, TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/menu?type={Uri.EscapeDataString(menuType)}&timeout={ToMs(timeout)}");
    }

    /// <summary>
    /// Wait for farmhand selection screen.
    /// GET /wait/farmhands
    /// </summary>
    public Task<WaitResult?> ForFarmhands(TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/farmhands?timeout={ToMs(timeout)}");
    }

    /// <summary>
    /// Wait for character customization menu.
    /// GET /wait/character
    /// </summary>
    public Task<WaitResult?> ForCharacter(TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/character?timeout={ToMs(timeout)}");
    }

    /// <summary>
    /// Wait for world to be ready (in-game).
    /// GET /wait/world-ready
    /// </summary>
    public Task<WaitResult?> ForWorldReady(TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/world-ready?timeout={ToMs(timeout)}");
    }

    /// <summary>
    /// Wait for title screen.
    /// GET /wait/title
    /// </summary>
    public Task<WaitResult?> ForTitle(TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/title?timeout={ToMs(timeout)}");
    }

    /// <summary>
    /// Wait for text input menu (invite code / LAN input dialog).
    /// GET /wait/text-input
    /// </summary>
    public Task<WaitResult?> ForTextInput(TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/text-input?timeout={ToMs(timeout)}");
    }

    /// <summary>
    /// Wait for connection to server.
    /// GET /wait/connected
    /// </summary>
    public Task<WaitResult?> ForConnected(TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/connected?timeout={ToMs(timeout)}");
    }

    /// <summary>
    /// Wait for full disconnection (no active connection).
    /// GET /wait/disconnected
    /// </summary>
    public Task<WaitResult?> ForDisconnected(TimeSpan? timeout = null)
    {
        return _client.GetAsync<WaitResult>($"/wait/disconnected?timeout={ToMs(timeout)}");
    }
}

#endregion

/// <summary>
/// HTTP client for game UI automation/testing.
/// This client controls the game's test interface (navigate menus, join servers, etc.).
/// Default port is 5123.
/// </summary>
public class GameTestClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private CancellationToken _errorCancellationToken = CancellationToken.None;

    public CoopClient Coop { get; }
    public FarmhandClient Farmhands { get; }
    public CharacterClient Character { get; }
    public ActionsClient Actions { get; }
    public WaitClient Wait { get; }
    public ChatClient Chat { get; }

    /// <summary>
    /// Sets the cancellation token that will abort HTTP calls when a server error is detected.
    /// This token is provided by IntegrationTestFixture.GetErrorCancellationToken().
    /// </summary>
    public void SetErrorCancellationToken(CancellationToken token)
    {
        _errorCancellationToken = token;
    }

    public GameTestClient(string baseUrl = "http://localhost:5123", TimeSpan? defaultWaitTimeout = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(2) // Long timeout for wait endpoints
        };
        Coop = new CoopClient(this);
        Farmhands = new FarmhandClient(this);
        Character = new CharacterClient(this);
        Actions = new ActionsClient(this);
        Wait = new WaitClient(this, defaultWaitTimeout);
        Chat = new ChatClient(this);
    }

    /// <summary>
    /// Navigate to a specific menu/screen.
    /// POST /navigate
    /// </summary>
    public Task<NavigationResult?> Navigate(string target)
    {
        return PostAsync<NavigationResult>("/navigate", new NavigateParams { Target = target });
    }

    /// <summary>
    /// Exit to title screen.
    /// POST /exit
    /// </summary>
    public Task<NavigationResult?> Exit()
    {
        return PostAsync<NavigationResult>("/exit", new { });
    }

    /// <summary>
    /// Get current menu info.
    /// GET /menu
    /// </summary>
    public Task<MenuInfo?> GetMenu()
    {
        return GetAsync<MenuInfo>("/menu");
    }

    /// <summary>
    /// Get captured errors from the game client.
    /// GET /errors
    /// </summary>
    /// <param name="limit">Optional limit on number of errors returned.</param>
    /// <param name="clear">If true, clears errors after retrieval.</param>
    public Task<ErrorsResponse?> GetErrors(int? limit = null, bool clear = false)
    {
        var query = new List<string>();
        if (limit.HasValue) query.Add($"limit={limit.Value}");
        if (clear) query.Add("clear=true");

        var path = query.Count > 0 ? $"/errors?{string.Join("&", query)}" : "/errors";
        return GetAsync<ErrorsResponse>(path);
    }

    /// <summary>
    /// Send a chat message to all players.
    /// POST /chat/send
    /// </summary>
    public Task<ChatResult?> SendChat(string message)
    {
        return PostAsync<ChatResult>("/chat/send", new { message });
    }

    /// <summary>
    /// Get recent chat messages.
    /// GET /chat/history
    /// </summary>
    public Task<ChatHistoryResult?> GetChatHistory(int count = 10)
    {
        return GetAsync<ChatHistoryResult>($"/chat/history?count={count}");
    }

    /// <summary>
    /// Get current game state (location, connection status, etc.).
    /// Uses GET /status and transforms the response.
    /// </summary>
    public async Task<GameStateResult?> GetState()
    {
        var status = await GetAsync<StatusResponse>("/status");
        if (status == null) return null;

        return new GameStateResult
        {
            Success = true,
            Location = status.Farmer?.CurrentLocation ?? string.Empty,
            IsConnected = status.Connection?.IsConnected ?? false,
            IsInGame = status.Connection?.WorldReady ?? false,
            PlayerName = status.Farmer?.Name
        };
    }

    /// <summary>
    /// Clear all captured errors.
    /// DELETE /errors
    /// </summary>
    public Task ClearErrors()
    {
        return DeleteAsync("/errors");
    }

    #region HTTP Helpers

    public async Task<T?> GetAsync<T>(string path) where T : class
    {
        var response = await _httpClient.GetAsync(path, _errorCancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(_errorCancellationToken);
    }

    public async Task<T?> PostAsync<T>(string path, object body) where T : class
    {
        var response = await _httpClient.PostAsJsonAsync(path, body, _errorCancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(_errorCancellationToken);
    }

    public async Task DeleteAsync(string path)
    {
        var response = await _httpClient.DeleteAsync(path, _errorCancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Keep old methods for backward compatibility with existing tests
    public Task<HttpResponseMessage> Navigate(NavigateParams navigateParams)
    {
        return _httpClient.PostAsJsonAsync("/navigate", navigateParams, _errorCancellationToken);
    }

    public Task<HttpResponseMessage> Post(string path, object body)
    {
        return _httpClient.PostAsJsonAsync(path, body, _errorCancellationToken);
    }

    #endregion

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
