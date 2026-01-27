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

public class JoinInviteParams
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
        return _client.PostAsync<NavigationResult>("/coop/invite-code/submit", new JoinInviteParams { InviteCode = inviteCode });
    }

    /// <summary>
    /// Join a server using an invite code.
    /// </summary>
    /// <remarks>
    /// Deprecated: Use OpenInviteCodeMenu → wait for text input → SubmitInviteCode instead.
    /// </remarks>
    [Obsolete("Use OpenInviteCodeMenu + SubmitInviteCode instead.")]
    public Task<NavigationResult?> JoinInvite(string inviteCode)
    {
        return _client.PostAsync<NavigationResult>("/coop/join-invite", new JoinInviteParams { InviteCode = inviteCode });
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

public class WaitClient
{
    private readonly GameTestClient _client;
    private readonly int _defaultTimeout;

    public WaitClient(GameTestClient client, int defaultTimeout = 30000)
    {
        _client = client;
        _defaultTimeout = defaultTimeout;
    }

    /// <summary>
    /// Wait for a specific menu type.
    /// GET /wait/menu?type=X
    /// </summary>
    public Task<WaitResult?> ForMenu(string menuType, int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/menu?type={Uri.EscapeDataString(menuType)}&timeout={t}");
    }

    /// <summary>
    /// Wait for farmhand selection screen.
    /// GET /wait/farmhands
    /// </summary>
    public Task<WaitResult?> ForFarmhands(int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/farmhands?timeout={t}");
    }

    /// <summary>
    /// Wait for character customization menu.
    /// GET /wait/character
    /// </summary>
    public Task<WaitResult?> ForCharacter(int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/character?timeout={t}");
    }

    /// <summary>
    /// Wait for world to be ready (in-game).
    /// GET /wait/world-ready
    /// </summary>
    public Task<WaitResult?> ForWorldReady(int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/world-ready?timeout={t}");
    }

    /// <summary>
    /// Wait for title screen.
    /// GET /wait/title
    /// </summary>
    public Task<WaitResult?> ForTitle(int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/title?timeout={t}");
    }

    /// <summary>
    /// Wait for text input menu (invite code / LAN input dialog).
    /// GET /wait/text-input
    /// </summary>
    public Task<WaitResult?> ForTextInput(int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/text-input?timeout={t}");
    }

    /// <summary>
    /// Wait for connection to server.
    /// GET /wait/connected
    /// </summary>
    public Task<WaitResult?> ForConnected(int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/connected?timeout={t}");
    }

    /// <summary>
    /// Wait for full disconnection (no active connection).
    /// GET /wait/disconnected
    /// </summary>
    public Task<WaitResult?> ForDisconnected(int? timeout = null)
    {
        var t = timeout ?? _defaultTimeout;
        return _client.GetAsync<WaitResult>($"/wait/disconnected?timeout={t}");
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

    public CoopClient Coop { get; }
    public FarmhandClient Farmhands { get; }
    public CharacterClient Character { get; }
    public ActionsClient Actions { get; }
    public WaitClient Wait { get; }

    public GameTestClient(string baseUrl = "http://localhost:5123", int defaultWaitTimeout = 30000)
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

    #region HTTP Helpers

    public async Task<T?> GetAsync<T>(string path) where T : class
    {
        var response = await _httpClient.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<T?> PostAsync<T>(string path, object body) where T : class
    {
        var response = await _httpClient.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    // Keep old methods for backward compatibility with existing tests
    public Task<HttpResponseMessage> Navigate(NavigateParams navigateParams)
    {
        return _httpClient.PostAsJsonAsync("/navigate", navigateParams);
    }

    public Task<HttpResponseMessage> Post(string path, object body)
    {
        return _httpClient.PostAsJsonAsync(path, body);
    }

    #endregion

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
