using JunimoTestClient.Diagnostics;
using JunimoTestClient.GameControl;

namespace JunimoTestClient.HttpServer;

/// <summary>
/// API endpoint definitions for OpenAPI spec generation.
/// The actual implementations are in ModEntry.cs - this class just defines the contract.
/// </summary>
public class ApiDefinitions
{
    // ============================================================
    // Status Endpoints
    // ============================================================

    [ApiEndpoint("GET", "/ping", Summary = "Health check", Description = "Simple ping endpoint to verify the API is responding", Tag = "Status")]
    [ApiResponse(typeof(PingResponse), 200)]
    private void Ping() { }

    [ApiEndpoint("GET", "/status", Summary = "Get game status", Description = "Returns overall game status including menu, connection, and farmer info", Tag = "Status")]
    [ApiResponse(typeof(StatusResponse), 200)]
    private void GetStatus() { }

    [ApiEndpoint("GET", "/menu", Summary = "Get current menu", Description = "Returns information about the currently active menu", Tag = "Status")]
    [ApiResponse(typeof(MenuInfo), 200)]
    private void GetMenu() { }

    [ApiEndpoint("GET", "/menu/buttons", Summary = "Get menu buttons", Description = "Returns clickable buttons in the current menu", Tag = "Status")]
    [ApiResponse(typeof(MenuButtonsInfo), 200)]
    private void GetMenuButtons() { }

    [ApiEndpoint("GET", "/menu/slots", Summary = "Get menu slots", Description = "Returns slots in the current menu (LoadGameMenu, CoopMenu, etc.)", Tag = "Status")]
    [ApiResponse(typeof(MenuSlotsInfo), 200)]
    private void GetMenuSlots() { }

    [ApiEndpoint("GET", "/connection", Summary = "Get connection status", Description = "Returns multiplayer connection status", Tag = "Status")]
    [ApiResponse(typeof(ConnectionStatus), 200)]
    private void GetConnection() { }

    [ApiEndpoint("GET", "/farmer", Summary = "Get farmer info", Description = "Returns current farmer information (null if not in game)", Tag = "Status")]
    [ApiResponse(typeof(FarmerInfo), 200)]
    private void GetFarmer() { }

    // ============================================================
    // Navigation Endpoints
    // ============================================================

    [ApiEndpoint("POST", "/navigate", Summary = "Navigate to menu", Description = "Navigate to a specific menu (e.g., 'coop', 'load', 'title')", Tag = "Navigation")]
    [ApiRequestBody(typeof(NavigateRequest))]
    [ApiResponse(typeof(NavigationResult), 200)]
    private void Navigate() { }

    [ApiEndpoint("POST", "/coop/tab", Summary = "Switch coop tab", Description = "Switch between tabs in the co-op menu (0=Join, 1=Host)", Tag = "Navigation")]
    [ApiRequestBody(typeof(CoopTabRequest))]
    [ApiResponse(typeof(NavigationResult), 200)]
    private void SwitchCoopTab() { }

    [ApiEndpoint("POST", "/exit", Summary = "Exit to title", Description = "Exit the current game and return to title screen", Tag = "Navigation")]
    [ApiResponse(typeof(NavigationResult), 200)]
    private void Exit() { }

    // ============================================================
    // Co-op Endpoints
    // ============================================================

    [ApiEndpoint("POST", "/coop/invite-code/open", Summary = "Open invite code menu", Description = "Navigate to the invite code entry screen", Tag = "Co-op")]
    [ApiResponse(typeof(NavigationResult), 200)]
    private void OpenInviteCodeMenu() { }

    [ApiEndpoint("POST", "/coop/invite-code/submit", Summary = "Submit invite code", Description = "Submit the invite code to connect to a server", Tag = "Co-op")]
    [ApiRequestBody(typeof(InviteCodeRequest))]
    [ApiResponse(typeof(NavigationResult), 200)]
    private void SubmitInviteCode() { }

    [ApiEndpoint("POST", "/coop/join-lan", Summary = "Join via LAN", Description = "Join a co-op game via LAN/IP address", Tag = "Co-op")]
    [ApiRequestBody(typeof(JoinLanRequest))]
    [ApiResponse(typeof(JoinResult), 200)]
    private void JoinLan() { }

    [ApiEndpoint("GET", "/farmhands", Summary = "Get farmhand slots", Description = "Get available farmhand slots when connecting to a server", Tag = "Co-op")]
    [ApiResponse(typeof(FarmhandSelectionInfo), 200)]
    private void GetFarmhands() { }

    [ApiEndpoint("POST", "/farmhands/select", Summary = "Select farmhand", Description = "Select a farmhand slot to join the game", Tag = "Co-op")]
    [ApiRequestBody(typeof(SelectFarmhandRequest))]
    [ApiResponse(typeof(JoinResult), 200)]
    private void SelectFarmhand() { }

    // ============================================================
    // Character Endpoints
    // ============================================================

    [ApiEndpoint("POST", "/character/customize", Summary = "Customize character", Description = "Set character name and favorite thing during farmhand creation", Tag = "Character")]
    [ApiRequestBody(typeof(CustomizeCharacterRequest))]
    [ApiResponse(typeof(CustomizeResult), 200)]
    private void CustomizeCharacter() { }

    [ApiEndpoint("POST", "/character/confirm", Summary = "Confirm character", Description = "Confirm character creation and join the game", Tag = "Character")]
    [ApiResponse(typeof(JoinResult), 200)]
    private void ConfirmCharacter() { }

    // ============================================================
    // Chat Endpoints
    // ============================================================

    [ApiEndpoint("POST", "/chat/send", Summary = "Send chat message", Description = "Send a chat message to all players", Tag = "Chat")]
    [ApiRequestBody(typeof(ChatSendRequest))]
    [ApiResponse(typeof(ChatResult), 200)]
    private void SendChat() { }

    [ApiEndpoint("POST", "/chat/info", Summary = "Send local info", Description = "Display a local info message (only visible to this client)", Tag = "Chat")]
    [ApiRequestBody(typeof(ChatSendRequest))]
    [ApiResponse(typeof(ChatResult), 200)]
    private void SendInfo() { }

    [ApiEndpoint("GET", "/chat/history", Summary = "Get chat history", Description = "Get recent chat messages", Tag = "Chat")]
    [ApiQueryParam("count", typeof(int), Description = "Number of messages to return (default: 10)")]
    [ApiResponse(typeof(ChatHistoryResult), 200)]
    private void GetChatHistory() { }

    // ============================================================
    // Action Endpoints
    // ============================================================

    [ApiEndpoint("POST", "/actions/sleep", Summary = "Initiate sleep", Description = "Make the farmer go to bed", Tag = "Actions")]
    [ApiResponse(typeof(SleepResult), 200)]
    private void Sleep() { }

    // ============================================================
    // Wait Endpoints
    // ============================================================

    [ApiEndpoint("GET", "/wait/menu", Summary = "Wait for menu", Description = "Block until a specific menu type is active", Tag = "Wait")]
    [ApiQueryParam("type", typeof(string), Required = true, Description = "Menu type to wait for")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForMenu() { }

    [ApiEndpoint("GET", "/wait/connected", Summary = "Wait for connection", Description = "Block until connected to a server", Tag = "Wait")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForConnected() { }

    [ApiEndpoint("GET", "/wait/world-ready", Summary = "Wait for world ready", Description = "Block until the world is fully loaded and ready", Tag = "Wait")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForWorldReady() { }

    [ApiEndpoint("GET", "/wait/farmhands", Summary = "Wait for farmhand menu", Description = "Block until the farmhand selection menu appears", Tag = "Wait")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForFarmhands() { }

    [ApiEndpoint("GET", "/wait/title", Summary = "Wait for title screen", Description = "Block until returned to the title screen", Tag = "Wait")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForTitle() { }

    [ApiEndpoint("GET", "/wait/disconnected", Summary = "Wait for disconnection", Description = "Block until disconnected from server", Tag = "Wait")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForDisconnected() { }

    [ApiEndpoint("GET", "/wait/text-input", Summary = "Wait for text input", Description = "Block until a text input dialog appears", Tag = "Wait")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForTextInput() { }

    [ApiEndpoint("GET", "/wait/character-customization", Summary = "Wait for character customization", Description = "Block until the character customization screen appears", Tag = "Wait")]
    [ApiQueryParam("timeout", typeof(int), Description = "Timeout in milliseconds (default: 30000)")]
    [ApiResponse(typeof(WaitResult), 200)]
    private void WaitForCharacterCustomization() { }

    // ============================================================
    // Diagnostics Endpoints
    // ============================================================

    [ApiEndpoint("GET", "/health", Summary = "Health watchdog status", Description = "Returns health status including freeze detection", Tag = "Diagnostics")]
    [ApiResponse(typeof(HealthStatus), 200)]
    private void GetHealth() { }

    [ApiEndpoint("GET", "/stats", Summary = "Performance stats", Description = "Returns performance statistics (FPS, tick time, memory)", Tag = "Diagnostics")]
    [ApiResponse(typeof(PerfStats), 200)]
    private void GetStats() { }

    [ApiEndpoint("POST", "/stats/reset", Summary = "Reset stats", Description = "Reset max tick tracking", Tag = "Diagnostics")]
    [ApiResponse(typeof(SuccessResponse), 200)]
    private void ResetStats() { }

    [ApiEndpoint("GET", "/errors", Summary = "Get captured errors", Description = "Returns captured errors/exceptions", Tag = "Diagnostics")]
    [ApiQueryParam("limit", typeof(int), Description = "Limit number of errors returned")]
    [ApiQueryParam("clear", typeof(bool), Description = "Clear errors after retrieving (true/1)")]
    [ApiResponse(typeof(ErrorsResponse), 200)]
    private void GetErrors() { }

    [ApiEndpoint("POST", "/errors/clear", Summary = "Clear errors", Description = "Clear all captured errors", Tag = "Diagnostics")]
    [ApiResponse(typeof(SuccessResponse), 200)]
    private void ClearErrors() { }

    [ApiEndpoint("POST", "/screenshot", Summary = "Capture screenshot", Description = "Capture a screenshot and return as base64 PNG", Tag = "Diagnostics")]
    [ApiResponse(typeof(ScreenshotResult), 200)]
    private void TakeScreenshot() { }

    [ApiEndpoint("POST", "/screenshot/file", Summary = "Save screenshot", Description = "Capture a screenshot and save to file", Tag = "Diagnostics")]
    [ApiRequestBody(typeof(ScreenshotFileRequest))]
    [ApiResponse(typeof(ScreenshotResult), 200)]
    private void SaveScreenshot() { }

    // ============================================================
    // Meta Endpoints
    // ============================================================

    [ApiEndpoint("GET", "/openapi.json", Summary = "OpenAPI spec (JSON)", Description = "Returns this OpenAPI specification in JSON format", Tag = "Meta")]
    private void GetOpenApiJson() { }

    [ApiEndpoint("GET", "/openapi.yaml", Summary = "OpenAPI spec (YAML)", Description = "Returns this OpenAPI specification in YAML format", Tag = "Meta")]
    private void GetOpenApiYaml() { }

    [ApiEndpoint("GET", "/docs", Summary = "API documentation (Scalar)", Description = "Interactive API documentation powered by Scalar", Tag = "Meta")]
    private void GetScalarDocs() { }

    [ApiEndpoint("GET", "/swagger", Summary = "API documentation (Swagger)", Description = "Interactive API documentation powered by Swagger UI", Tag = "Meta")]
    private void GetSwaggerDocs() { }
}

// ============================================================
// Request/Response Types (only those not defined elsewhere)
// ============================================================

public class PingResponse
{
    public bool Pong { get; set; }
    public long Timestamp { get; set; }
}

public class StatusResponse
{
    public MenuInfo? Menu { get; set; }
    public ConnectionStatus? Connection { get; set; }
    public FarmerInfo? Farmer { get; set; }
    public long Timestamp { get; set; }
}

public class NavigateRequest
{
    public string Target { get; set; } = "";
}

public class CoopTabRequest
{
    public int Tab { get; set; }
}

public class InviteCodeRequest
{
    public string InviteCode { get; set; } = "";
}

public class JoinLanRequest
{
    public string Address { get; set; } = "localhost";
}

public class SelectFarmhandRequest
{
    public int SlotIndex { get; set; }
}

public class ChatSendRequest
{
    public string Message { get; set; } = "";
}

public class ScreenshotFileRequest
{
    public string? Filename { get; set; }
}

public class WaitResult
{
    public bool Success { get; set; }
    public string? Condition { get; set; }
    public string? Error { get; set; }
    public int WaitedMs { get; set; }
}

public class SuccessResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
}
