using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

public class ScreenshotResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("base64Png")]
    public string? Base64Png { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
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

    [JsonPropertyName("farmhands")]
    public List<FarmhandSlot> Farmhands { get; set; } = new();

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

public class WarpResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }
}

public class LeaveFestivalResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class EngageToNpcResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("spouse")]
    public string? Spouse { get; set; }

    [JsonPropertyName("isEngaged")]
    public bool IsEngaged { get; set; }
}

public class PlacePotResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }

    [JsonPropertyName("tileX")]
    public int? TileX { get; set; }

    [JsonPropertyName("tileY")]
    public int? TileY { get; set; }
}

public class ClearAreaResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class FarmBuildingInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("tileX")]
    public int TileX { get; set; }

    [JsonPropertyName("tileY")]
    public int TileY { get; set; }

    /// <summary>True if this building has an interior the player can enter (door is live).</summary>
    [JsonPropertyName("hasInterior")]
    public bool HasInterior { get; set; }
}

public class FarmBuildingsResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("cabins")]
    public List<FarmBuildingInfo> Cabins { get; set; } = new();
}

public class PlantCropResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }

    [JsonPropertyName("tileX")]
    public int? TileX { get; set; }

    [JsonPropertyName("tileY")]
    public int? TileY { get; set; }

    [JsonPropertyName("seedItemId")]
    public string? SeedItemId { get; set; }
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

    /// <summary>
    /// Monotonic sequence number assigned by the test-client's Harmony patch.
    /// Used for deduplication: messages with Seq > snapshot are guaranteed to be new.
    /// </summary>
    [JsonPropertyName("seq")]
    public long Seq { get; set; }
}

public class ChatHistoryResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Total number of chat messages received since game start.
    /// Used as a sequence cursor for deduplication.
    /// </summary>
    [JsonPropertyName("totalReceived")]
    public long TotalReceived { get; set; }
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

    [JsonPropertyName("uniqueId")]
    public string? UniqueId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Wedding ceremonies this client has rendered today (one per gate). See
    /// <see cref="StatusResponse.WeddingsRendered"/>.</summary>
    [JsonPropertyName("weddingsRendered")]
    public List<RenderedCeremonyInfo> WeddingsRendered { get; set; } = new();
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

    /// <summary>
    /// Wedding ceremonies this client has played through (rendered) today, one per gate. Empty until a
    /// ceremony actually plays on this client. The E2E wedding test asserts each client rendered BOTH
    /// same-day ceremonies — the host-side spouse-warp can't prove a client rendered anything.
    /// </summary>
    [JsonPropertyName("weddingsRendered")]
    public List<RenderedCeremonyInfo> WeddingsRendered { get; set; } = new();

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>One wedding ceremony a client played through and rendered (mirrors the test-client's
/// <c>RenderedCeremony</c>). Used by the E2E wedding test's per-client render proof.</summary>
public class RenderedCeremonyInfo
{
    [JsonPropertyName("gate")]
    public string Gate { get; set; } = string.Empty;

    [JsonPropertyName("groomId")]
    public long GroomId { get; set; }

    [JsonPropertyName("groomName")]
    public string GroomName { get; set; } = string.Empty;

    [JsonPropertyName("spouse")]
    public string Spouse { get; set; } = string.Empty;

    [JsonPropertyName("isLocalPlayer")]
    public bool IsLocalPlayer { get; set; }
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

    [JsonPropertyName("weatherIcon")]
    public int WeatherIcon { get; set; }

    [JsonPropertyName("whereIsTodaysFest")]
    public string? WhereIsTodaysFest { get; set; }

    [JsonPropertyName("isFestival")]
    public bool IsFestival { get; set; }

    [JsonPropertyName("festivalStartReady")]
    public int FestivalStartReady { get; set; }

    [JsonPropertyName("festivalStartRequired")]
    public int FestivalStartRequired { get; set; }

    [JsonPropertyName("timeOfDay")]
    public int TimeOfDay { get; set; }
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
        return _client.PostAsync<NavigationResult>(
            "/coop/invite-code/submit",
            new InviteCodeParams { InviteCode = inviteCode }
        );
    }

    /// <summary>
    /// Join a server via LAN/IP address.
    /// POST /coop/join-lan
    /// </summary>
    /// <param name="address">Server address in "host:port" or "host" format.</param>
    public Task<NavigationResult?> JoinLan(string address = "localhost")
    {
        return _client.PostAsync<NavigationResult>("/coop/join-lan", new { Address = address });
    }

    /// <summary>
    /// Connect to a server via LAN directly (no menu navigation required).
    /// POST /connect/lan
    /// </summary>
    /// <param name="address">Server address in "host:port" or "host" format.</param>
    public Task<NavigationResult?> ConnectLanDirect(string address)
    {
        return _client.PostAsync<NavigationResult>("/connect/lan", new { Address = address });
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
        return _client.PostAsync<NavigationResult>(
            "/farmhands/select",
            new SelectFarmhandParams { SlotIndex = slotIndex }
        );
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
        return _client.PostAsync<CustomizeResult>(
            "/character/customize",
            new CustomizeCharacterParams { Name = name, FavoriteThing = favoriteThing }
        );
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

    /// <summary>
    /// Vote to leave the current festival (the real-player exit path). Returns
    /// Success=false if the client isn't at a festival. The festival only ends
    /// once the host is also ready, so callers should then poll
    /// <see cref="ServerApiClient.GetFestivalState"/> for IsFestivalActive=false.
    /// POST /actions/leave_festival
    /// </summary>
    public Task<LeaveFestivalResult?> LeaveFestival()
    {
        return _client.PostAsync<LeaveFestivalResult>("/actions/leave_festival", new { });
    }

    /// <summary>
    /// Engage this client's player to an NPC (client-authoritative) so the next day-transition queues
    /// their wedding. Must be authored on the client — the farmhand's Farmer root is client-owned, so
    /// a host-side spouse write is overwritten by the client's nightly full-root resend before the
    /// wedding fires. POST /actions/engage_to_npc
    /// </summary>
    public Task<EngageToNpcResult?> EngageToNpc(string npc, int daysUntilWedding = 1)
    {
        return _client.PostAsync<EngageToNpcResult>(
            "/actions/engage_to_npc",
            new { npc, daysUntilWedding }
        );
    }

    /// <summary>
    /// Queue a warp to the given location/tile. Async — caller must poll
    /// <see cref="GameTestClient.WaitForLocationAsync"/> to confirm arrival
    /// before issuing follow-up actions that depend on currentLocation.
    /// POST /actions/warp
    /// </summary>
    public Task<WarpResult?> Warp(string locationName, int tileX, int tileY) =>
        _client.PostAsync<WarpResult>(
            "/actions/warp",
            new
            {
                locationName,
                tileX,
                tileY,
            }
        );

    /// <summary>
    /// Place a Garden Pot at the given tile on the player's current location.
    /// POST /actions/place_pot
    /// </summary>
    public Task<PlacePotResult?> PlacePot(
        string locationName,
        int tileX,
        int tileY,
        bool clearObstacles = false
    ) =>
        _client.PostAsync<PlacePotResult>(
            "/actions/place_pot",
            new
            {
                locationName,
                tileX,
                tileY,
                clearObstacles,
            }
        );

    /// <summary>
    /// Clear debris (objects, terrain features, bushes, resource clumps) from a tile
    /// area on the player's current location. Use to prep a building footprint.
    /// POST /actions/clear_area
    /// </summary>
    public Task<ClearAreaResult?> ClearArea(
        string locationName,
        int tileX,
        int tileY,
        int width,
        int height
    ) =>
        _client.PostAsync<ClearAreaResult>(
            "/actions/clear_area",
            new
            {
                locationName,
                tileX,
                tileY,
                width,
                height,
            }
        );

    /// <summary>
    /// Plant a seed in a HoeDirt or IndoorPot at the given tile.
    /// POST /actions/plant_crop
    /// </summary>
    public Task<PlantCropResult?> PlantCrop(
        string itemId,
        string locationName,
        int tileX,
        int tileY
    ) =>
        _client.PostAsync<PlantCropResult>(
            "/actions/plant_crop",
            new
            {
                itemId,
                locationName,
                tileX,
                tileY,
            }
        );

    /// <summary>
    /// List this client's view of the farm's cabins (name + tile). The server rewrites each
    /// peer's locationIntroduction, so the client's farm can differ from master state — this
    /// is how a test observes per-peer cabin mutations that /cabins (master) can't see.
    /// GET /actions/farm_buildings
    /// </summary>
    public Task<FarmBuildingsResult?> GetFarmBuildings(CancellationToken ct = default) =>
        _client.GetAsync<FarmBuildingsResult>("/actions/farm_buildings", ct);
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
        int historySize = 10
    )
    {
        var effectiveTimeout = timeout ?? Helpers.TestTimings.ChatCommandTimeout;
        ChatHistoryResult? result = null;

        var found = await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_GameTestClient_WaitForChatHistoryKeyword,
            async () =>
            {
                result = await GetHistory(historySize);
                return result?.Messages?.Any(m =>
                        keywords.Any(k => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))
                    ) == true;
            },
            effectiveTimeout
        );

        return found ? result : null;
    }

    /// <summary>
    /// Wait for a chat message containing the specified keyword (case-insensitive).
    /// Polls until a matching message is found or timeout expires.
    /// </summary>
    public Task<ChatHistoryResult?> WaitForMessageContainingAsync(
        string keyword,
        TimeSpan? timeout = null,
        int historySize = 10
    )
    {
        return WaitForMessageContainingAsync(new[] { keyword }, timeout, historySize);
    }

    /// <summary>
    /// Sends a chat message and waits for a new response matching the given keywords.
    /// Uses sequence-based deduplication: snapshots the TotalReceived counter before sending,
    /// then only checks messages with Seq > snapshot. This avoids the string-comparison
    /// deduplication bug where identical response texts from different commands are filtered out.
    /// </summary>
    /// <param name="message">The chat message to send</param>
    /// <param name="responseKeywords">Keywords to look for in the response</param>
    /// <param name="matchAll">If true, ALL keywords must match a single message. If false, ANY keyword suffices.</param>
    /// <param name="timeout">Maximum time to wait for the response</param>
    /// <param name="historySize">Number of recent messages to check</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<bool> SendAndWaitForResponseAsync(
        string message,
        string[] responseKeywords,
        bool matchAll = false,
        TimeSpan? timeout = null,
        int historySize = 20,
        CancellationToken ct = default
    )
    {
        var chatBefore = await GetHistory(historySize);
        var seqBefore = chatBefore?.TotalReceived ?? 0;

        await Send(message);

        return await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_GameTestClient_SendAndExpectChatResponse,
            async () =>
            {
                var chat = await GetHistory(historySize);
                if (chat?.Messages == null)
                {
                    return false;
                }

                // Only check messages that arrived after our snapshot
                var newMessages = chat.Messages.Where(m => m.Seq > seqBefore).ToList();
                return matchAll
                    ? keywords_AllPresent(newMessages, responseKeywords)
                    : keywords_AnyPresent(newMessages, responseKeywords);
            },
            timeout ?? Helpers.TestTimings.ChatCommandTimeout,
            cancellationToken: ct
        );
    }

    private static bool keywords_AllPresent(List<ChatMessage> messages, string[] keywords)
    {
        return keywords.All(k =>
            messages.Any(m => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))
        );
    }

    private static bool keywords_AnyPresent(List<ChatMessage> messages, string[] keywords)
    {
        return keywords.Any(k =>
            messages.Any(m => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))
        );
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
        return _client.GetAsync<WaitResult>(
            $"/wait/menu?type={Uri.EscapeDataString(menuType)}&timeout={ToMs(timeout)}"
        );
    }

    /// <summary>
    /// Wait for farmhand selection screen.
    /// GET /wait/farmhands
    /// </summary>
    public Task<WaitResult?> ForFarmhands(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        return _client.GetAsync<WaitResult>($"/wait/farmhands?timeout={ToMs(timeout)}", ct);
    }

    /// <summary>
    /// Wait for character customization menu.
    /// GET /wait/character
    /// </summary>
    public Task<WaitResult?> ForCharacter(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        return _client.GetAsync<WaitResult>($"/wait/character?timeout={ToMs(timeout)}", ct);
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

    /// <summary>
    /// Cancellation token used by all HTTP calls. Set by the test infrastructure
    /// to abort in-flight requests when the server detects a fatal error.
    /// </summary>
    public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

    public CoopClient Coop { get; }
    public FarmhandClient Farmhands { get; }
    public CharacterClient Character { get; }
    public ActionsClient Actions { get; }
    public WaitClient Wait { get; }
    public ChatClient Chat { get; }

    public GameTestClient(
        string baseUrl = "http://localhost:5123",
        TimeSpan? defaultWaitTimeout = null
    )
        : this(baseUrl, defaultWaitTimeout, liveBaseUrl: null, healAsync: null) { }

    /// <summary>
    /// Constructs a client that transparently heals a dropped SSH forward (re-opens it +
    /// retries against the new port) so an in-flight request survives a transient master
    /// keepalive blip instead of failing the test. <paramref name="liveBaseUrl"/> returns
    /// the client container's CURRENT base URL; <paramref name="healAsync"/> re-opens the
    /// client's forward. Both null ⇒ a plain client (unchanged behavior).
    /// </summary>
    public GameTestClient(
        string baseUrl,
        TimeSpan? defaultWaitTimeout,
        Func<string>? liveBaseUrl,
        Func<CancellationToken, Task<bool>>? healAsync
    )
    {
        HttpMessageHandler inner = new HttpClientHandler();
        if (liveBaseUrl != null && healAsync != null)
        {
            inner = new ForwardHealingHandler(liveBaseUrl, healAsync) { InnerHandler = inner };
        }
        var handler = new TracingHandler("test-client") { InnerHandler = inner };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(2), // Long timeout for wait endpoints
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
    public Task<ErrorsResponse?> GetErrors(
        int? limit = null,
        bool clear = false,
        CancellationToken ct = default
    )
    {
        var query = new List<string>();
        if (limit.HasValue)
        {
            query.Add($"limit={limit.Value}");
        }

        if (clear)
        {
            query.Add("clear=true");
        }

        var path = query.Count > 0 ? $"/errors?{string.Join("&", query)}" : "/errors";
        return GetAsync<ErrorsResponse>(path, ct);
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
        if (status == null)
        {
            return null;
        }

        return new GameStateResult
        {
            Success = true,
            Location = status.Farmer?.CurrentLocation ?? string.Empty,
            IsConnected = status.Connection?.IsConnected ?? false,
            IsInGame = status.Connection?.WorldReady ?? false,
            PlayerName = status.Farmer?.Name,
            UniqueId = status.Farmer?.UniqueId,
            WeddingsRendered = status.WeddingsRendered,
        };
    }

    /// <summary>
    /// Raw client-side farmer info, including the festival diagnostic fields
    /// (weatherIcon, whereIsTodaysFest, festivalStart ready). Used to debug
    /// festival-entry failures — the host warps in only when this client has
    /// whereIsTodaysFest set locally and festivalStart ready.
    /// GET /status
    /// </summary>
    public async Task<FarmerInfoResponse?> GetFestivalDebug()
    {
        var status = await GetAsync<StatusResponse>("/status");
        return status?.Farmer;
    }

    /// <summary>
    /// Polls <see cref="GetState"/> until the player's location matches
    /// <paramref name="locationPattern"/> (a regular expression, case-insensitive).
    /// Returns the matching state, or null if timed out.
    ///
    /// <para>
    /// <b>Anchor explicitly.</b> The matcher is unanchored, so a bare <c>"Farm"</c>
    /// would still substring-match <c>"FarmHouse{guid}"</c> (the cabin interior's
    /// <c>NameOrUniqueName</c>, produced as <c>IndoorMap + GuidHelper.NewGuid()</c>
    /// in <c>Buildings/Building.cs</c>). Use anchors:
    /// <list type="bullet">
    ///   <item><c>"^Farm$"</c> — exactly the outdoor Farm.</item>
    ///   <item><c>"^" + <see cref="CabinLocationPrefix"/></c> — any cabin interior.</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<GameStateResult?> WaitForLocationAsync(
        string locationPattern,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var regex = new Regex(
            locationPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
        GameStateResult? state = null;
        var found = await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_GameTestClient_WaitForLocation,
            async () =>
            {
                state = await GetState();
                return state?.Location is string loc && regex.IsMatch(loc);
            },
            timeout ?? Helpers.TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        return found ? state : null;
    }

    /// <summary>
    /// Cabin interior location name prefix. Cabin interiors use IndoorMap="FarmHouse"
    /// (from Buildings.json), so their NameOrUniqueName is "FarmHouse{guid}".
    /// Both lobby cabins and player cabins share this prefix.
    /// </summary>
    internal const string CabinLocationPrefix = "FarmHouse";

    /// <summary>
    /// Waits for the post-auth warp to complete: the player's location must change
    /// from <paramref name="lobbyLocation"/> to a different cabin interior.
    ///
    /// The passout warp (type 29) sends the client through a fade-to-black animation.
    /// During this transition, the client's NetLocationRef may transiently resolve to
    /// "Farm" (the cabin's parent location) before settling on the real cabin interior.
    /// This method filters out that transient state by requiring the new location to
    /// start with the cabin prefix ("FarmHouse").
    /// </summary>
    /// <returns>True if the warp completed within the timeout.</returns>
    public async Task<bool> WaitForAuthWarpAsync(
        string lobbyLocation,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        return await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_GameTestClient_WaitForAuthWarp,
            async () =>
            {
                var s = await GetState();
                return !string.IsNullOrEmpty(s?.Location)
                    && s.Location != lobbyLocation
                    && s.Location.StartsWith(
                        CabinLocationPrefix,
                        StringComparison.OrdinalIgnoreCase
                    );
            },
            timeout ?? Helpers.TestTimings.AuthLoginAttemptTimeout,
            cancellationToken: ct
        );
    }

    /// <summary>
    /// Capture a screenshot from the game client via POST /screenshot.
    /// Returns a base64-encoded PNG.
    /// </summary>
    public Task<ScreenshotResponse?> CaptureScreenshot()
    {
        return PostAsync<ScreenshotResponse>("/screenshot", new { });
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

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
        where T : class
    {
        var effective = ct == default ? CancellationToken : ct;
        var response = await _httpClient.GetAsync(path, effective);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(effective);
    }

    public async Task<T?> PostAsync<T>(string path, object body)
        where T : class
    {
        var response = await _httpClient.PostAsJsonAsync(path, body, CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(CancellationToken);
    }

    public async Task DeleteAsync(string path)
    {
        var response = await _httpClient.DeleteAsync(path, CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    #endregion

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
