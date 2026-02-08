using HarmonyLib;
using JunimoTestClient.Diagnostics;
using JunimoTestClient.GameControl;
using JunimoTestClient.GameTweaks;
using JunimoTestClient.HttpServer;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Steamworks;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace JunimoTestClient;

/// <summary>
/// Entry point for the JunimoTestClient mod.
/// Exposes an HTTP API for automated testing of Stardew Valley.
/// </summary>
public class ModEntry : Mod
{
    private TestApiServer? _server;
    private MenuNavigator? _navigator;
    private CoopController? _coopController;
    private ChatController? _chatController;
    private CharacterController? _characterController;
    private ActionsController? _actionsController;

    // Tweaks
    private ConvenienceTweaks? _tweaks;
    private SkipIntro? _skipIntro;
    private GodTool? _godTool;

    // Diagnostics
    private HealthWatchdog? _healthWatchdog;
    private PerformanceStats? _perfStats;
    private ScreenshotCapture? _screenshot;
    private ErrorCapture? _errorCapture;

    private const int DefaultPort = 5123;
    private const int DefaultWaitTimeout = 30000; // 30 seconds

    public override void Entry(IModHelper helper)
    {
        // Game control
        _navigator = new MenuNavigator(Monitor);
        _coopController = new CoopController(helper, Monitor);
        _chatController = new ChatController(Monitor);
        _characterController = new CharacterController(Monitor);
        _actionsController = new ActionsController(Monitor);

        // Tweaks
        _tweaks = new ConvenienceTweaks(helper, Monitor);
        _tweaks.Apply();

        _skipIntro = new SkipIntro(Monitor);
        _skipIntro.Apply();

        _godTool = new GodTool(helper, Monitor);
        _godTool.Apply();

        // Apply Steam diagnostics patches
        ApplySteamDiagnostics(helper);

        // Diagnostics
        _healthWatchdog = new HealthWatchdog(helper, Monitor);
        _healthWatchdog.Start();

        _perfStats = new PerformanceStats(helper, Monitor);
        _perfStats.Start();

        _screenshot = new ScreenshotCapture(Monitor);

        _errorCapture = new ErrorCapture(Monitor);
        _errorCapture.Start();

        // Events
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.Specialized.UnvalidatedUpdateTicked += OnUpdateTicked;
        helper.Events.Display.Rendered += OnRendered;
        helper.Events.Player.Warped += OnPlayerWarped;

        // Extended spawn logging
        helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        StartServer();
    }

    private void StartServer()
    {
        if (_server != null)
            return;

        var port = GetPortFromEnv();
        _server = new TestApiServer(port, Monitor);

        RegisterEndpoints();

        _server.Start();
        Monitor.Log($"Test API available at http://localhost:{port}/", LogLevel.Info);
    }

    private int GetPortFromEnv()
    {
        var portStr = Environment.GetEnvironmentVariable("JUNIMO_TEST_PORT");
        if (int.TryParse(portStr, out var port) && port > 0 && port < 65536)
            return port;
        return DefaultPort;
    }

    private void RegisterEndpoints()
    {
        if (_server == null) return;

        RegisterStatusEndpoints();
        RegisterNavigationEndpoints();
        RegisterCoopEndpoints();
        RegisterCharacterEndpoints();
        RegisterChatEndpoints();
        RegisterActionEndpoints();
        RegisterWaitEndpoints();
        RegisterDiagnosticsEndpoints();
    }

    private void RegisterStatusEndpoints()
    {
        // GET /ping - Simple health check
        _server!.Get("ping", _ => new { pong = true, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

        // GET /status - Overall game status
        _server.Get("status", _ => new
        {
            menu = MenuDetector.GetCurrentMenu(),
            connection = MenuDetector.GetConnectionStatus(),
            farmer = MenuDetector.GetFarmerInfo(),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        // GET /menu - Current menu info
        _server.Get("menu", _ => MenuDetector.GetCurrentMenu());

        // GET /menu/buttons - Get clickable buttons in current menu
        _server.Get("menu/buttons", _ =>
        {
            MenuButtonsInfo? result = null;
            ExecuteOnGameThread(() => { result = MenuDetector.GetMenuButtons(); return result; });
            return result;
        });

        // GET /menu/slots - Get slots in current menu (LoadGameMenu, CoopMenu, etc.)
        _server.Get("menu/slots", _ =>
        {
            MenuSlotsInfo? result = null;
            ExecuteOnGameThread(() => { result = MenuDetector.GetMenuSlots(); return result; });
            return result;
        });

        // GET /connection - Connection status
        _server.Get("connection", _ => MenuDetector.GetConnectionStatus());

        // GET /farmer - Current farmer info
        _server.Get("farmer", _ => MenuDetector.GetFarmerInfo());
    }

    private void RegisterNavigationEndpoints()
    {
        // POST /navigate - Navigate to a menu
        _server!.Post("navigate", req =>
        {
            var body = TestApiServer.ReadBody<NavigateRequest>(req);
            if (body == null || string.IsNullOrEmpty(body.Target))
            {
                return new NavigationResult { Success = false, Error = "Missing 'target' in request body" };
            }

            return ExecuteOnGameThread(() => _navigator!.NavigateTo(body.Target));
        });

        // POST /coop/tab - Switch coop menu tab
        _server.Post("coop/tab", req =>
        {
            var body = TestApiServer.ReadBody<CoopTabRequest>(req);
            if (body == null)
            {
                return new NavigationResult { Success = false, Error = "Missing request body" };
            }

            return ExecuteOnGameThread(() => _navigator!.SwitchCoopTab(body.Tab));
        });

        // POST /exit - Exit to title
        _server.Post("exit", _ => ExecuteOnGameThread(() => _navigator!.ExitToTitle()));
    }

    private void RegisterCoopEndpoints()
    {
        // POST /coop/invite-code/open - Open the invite code input dialog
        _server!.Post("coop/invite-code/open", _ =>
            ExecuteOnGameThread(() => _coopController!.OpenInviteCodeMenu()));

        // POST /coop/invite-code/submit - Submit invite code in the text input menu
        _server.Post("coop/invite-code/submit", req =>
        {
            var body = TestApiServer.ReadBody<JoinInviteRequest>(req);
            if (body == null || string.IsNullOrEmpty(body.InviteCode))
            {
                return new JoinResult { Success = false, Error = "Missing 'inviteCode' in request body" };
            }

            return ExecuteOnGameThread(() => _coopController!.SubmitInviteCode(body.InviteCode));
        });

        // POST /coop/join-lan - Join via LAN/IP address
        _server.Post("coop/join-lan", req =>
        {
            var body = TestApiServer.ReadBody<JoinLanRequest>(req);
            var address = body?.Address ?? "localhost";

            return ExecuteOnGameThread(() => _coopController!.EnterLanAddress(address));
        });

        // GET /farmhands - Get available farmhand slots
        _server.Get("farmhands", _ =>
        {
            FarmhandSelectionInfo? result = null;
            ExecuteOnGameThread(() =>
            {
                result = _coopController!.GetFarmhandSlots();
                return result;
            });
            return result;
        });

        // POST /farmhands/select - Select a farmhand slot
        _server.Post("farmhands/select", req =>
        {
            var body = TestApiServer.ReadBody<SelectFarmhandRequest>(req);
            if (body == null)
            {
                return new JoinResult { Success = false, Error = "Missing request body" };
            }

            return ExecuteOnGameThread(() => _coopController!.SelectFarmhand(body.SlotIndex));
        });
    }

    private void RegisterCharacterEndpoints()
    {
        // GET /character - Get current CharacterCustomization state
        _server!.Get("character", _ =>
        {
            CharacterInfo? result = null;
            ExecuteOnGameThread(() =>
            {
                result = _characterController!.GetCharacterInfo();
                return result;
            });
            return result;
        });

        // POST /character/customize - Set name and favorite thing
        _server.Post("character/customize", req =>
        {
            var body = TestApiServer.ReadBody<CustomizeCharacterRequest>(req);
            if (body == null)
            {
                return new CustomizeResult { Success = false, Error = "Missing request body" };
            }

            return ExecuteOnGameThread(() => _characterController!.SetCharacterData(body.Name, body.FavoriteThing));
        });

        // POST /character/confirm - Click OK to confirm character
        _server.Post("character/confirm", _ =>
            ExecuteOnGameThread(() => _characterController!.ConfirmCharacter()));

        // GET /wait/character - Wait for CharacterCustomization menu
        _server.Get("wait/character", req =>
        {
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            return WaitForCondition(
                () =>
                {
                    var menu = MenuDetector.GetCurrentMenu();
                    return menu.Type == "CharacterCustomization" ||
                           (menu.SubMenu?.Type == "CharacterCustomization");
                },
                timeout,
                "character-customization"
            );
        });
    }

    private void RegisterChatEndpoints()
    {
        // POST /chat/send - Send a chat message
        _server!.Post("chat/send", req =>
        {
            var body = TestApiServer.ReadBody<ChatSendRequest>(req);
            if (body == null || string.IsNullOrEmpty(body.Message))
            {
                return new ChatResult { Success = false, Error = "Missing 'message' in request body" };
            }

            return ExecuteOnGameThread(() => _chatController!.SendMessage(body.Message));
        });

        // POST /chat/info - Send a local info message
        _server.Post("chat/info", req =>
        {
            var body = TestApiServer.ReadBody<ChatSendRequest>(req);
            if (body == null || string.IsNullOrEmpty(body.Message))
            {
                return new ChatResult { Success = false, Error = "Missing 'message' in request body" };
            }

            return ExecuteOnGameThread(() => _chatController!.SendLocalInfo(body.Message));
        });

        // GET /chat/history - Get recent chat messages
        _server.Get("chat/history", req =>
        {
            var countStr = req.QueryString["count"];
            var count = int.TryParse(countStr, out var c) ? c : 10;

            ChatHistoryResult? result = null;
            ExecuteOnGameThread(() =>
            {
                result = _chatController!.GetRecentMessages(count);
                return result;
            });
            return result;
        });
    }

    private void RegisterActionEndpoints()
    {
        // POST /actions/sleep - Make the player go to sleep
        _server!.Post("actions/sleep", _ =>
            ExecuteOnGameThread(() => _actionsController!.GoToSleep()));
    }

    private void RegisterWaitEndpoints()
    {
        // GET /wait/menu - Wait for a specific menu type
        _server!.Get("wait/menu", req =>
        {
            var menuType = req.QueryString["type"];
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            if (string.IsNullOrEmpty(menuType))
            {
                return new WaitResult { Success = false, Error = "Missing 'type' query parameter" };
            }

            return WaitForCondition(
                () =>
                {
                    var menu = MenuDetector.GetCurrentMenu();
                    return menu.Type.Equals(menuType, StringComparison.OrdinalIgnoreCase) ||
                           (menu.SubMenu?.Type.Equals(menuType, StringComparison.OrdinalIgnoreCase) ?? false);
                },
                timeout,
                $"menu:{menuType}"
            );
        });

        // GET /wait/connected - Wait until connected to a server
        _server.Get("wait/connected", req =>
        {
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            return WaitForCondition(
                () => MenuDetector.GetConnectionStatus().IsConnected,
                timeout,
                "connected"
            );
        });

        // GET /wait/world-ready - Wait until world is ready
        _server.Get("wait/world-ready", req =>
        {
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            return WaitForCondition(
                () => MenuDetector.GetConnectionStatus().WorldReady,
                timeout,
                "world-ready"
            );
        });

        // GET /wait/farmhands - Wait for farmhand selection screen with slots loaded
        _server.Get("wait/farmhands", req =>
        {
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            var menuSlotsField = typeof(LoadGameMenu).GetField("menuSlots",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            return WaitForCondition(
                () =>
                {
                    // Check if FarmhandMenu is active
                    FarmhandMenu? farmhandMenu = null;
                    if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is FarmhandMenu sub)
                        farmhandMenu = sub;
                    else if (Game1.activeClickableMenu is FarmhandMenu direct)
                        farmhandMenu = direct;

                    if (farmhandMenu == null)
                        return false;

                    // Check that menu is not still loading farmhands
                    if (farmhandMenu.gettingFarmhands)
                        return false;

                    // Check that slots are actually populated
                    if (menuSlotsField?.GetValue(farmhandMenu) is not System.Collections.IList slots || slots.Count == 0)
                        return false;

                    return true;
                },
                timeout,
                "farmhand-menu"
            );
        });

        // GET /wait/title - Wait for title screen
        _server.Get("wait/title", req =>
        {
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            return WaitForCondition(
                () =>
                {
                    var menu = MenuDetector.GetCurrentMenu();
                    return menu.Type == "TitleMenu" && menu.SubMenu == null;
                },
                timeout,
                "title"
            );
        });

        // GET /wait/text-input - Wait for TitleTextInputMenu (invite code / LAN input dialog)
        _server.Get("wait/text-input", req =>
        {
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            return WaitForCondition(
                () =>
                {
                    // Check if TitleTextInputMenu is active as a submenu or directly
                    if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is TitleTextInputMenu)
                        return true;
                    if (Game1.activeClickableMenu is TitleTextInputMenu)
                        return true;
                    return false;
                },
                timeout,
                "text-input"
            );
        });

        // GET /wait/disconnected - Wait until fully disconnected (no active connection)
        // Includes stability delay to ensure Galaxy SDK fully cleans up
        _server.Get("wait/disconnected", req =>
        {
            var timeoutStr = req.QueryString["timeout"];
            var timeout = int.TryParse(timeoutStr, out var t) ? t : DefaultWaitTimeout;

            // First wait for the basic disconnection condition
            var result = WaitForCondition(
                () =>
                {
                    // Check we're at title with no submenu
                    var menu = MenuDetector.GetCurrentMenu();
                    if (menu.Type != "TitleMenu" || menu.SubMenu != null)
                        return false;

                    // Check no active multiplayer connection
                    if (Game1.IsMultiplayer || Game1.IsClient || Game1.IsServer)
                        return false;

                    // Check no client object exists
                    if (Game1.client != null)
                        return false;

                    return true;
                },
                timeout,
                "disconnected"
            );

            return result;
        });
    }

    private void RegisterDiagnosticsEndpoints()
    {
        // GET /health - Health check with watchdog status
        _server!.Get("health", _ => _healthWatchdog!.GetStatus());

        // GET /stats - Performance statistics
        _server.Get("stats", _ => _perfStats!.GetStats());

        // POST /stats/reset - Reset max tick tracking
        _server.Post("stats/reset", _ =>
        {
            _perfStats!.ResetMax();
            return new { success = true };
        });

        // GET /errors - Get captured errors
        _server.Get("errors", req =>
        {
            var limitStr = req.QueryString["limit"];
            var clearStr = req.QueryString["clear"];
            var limit = int.TryParse(limitStr, out var l) ? l : (int?)null;
            var clear = clearStr == "true" || clearStr == "1";

            return _errorCapture!.GetErrors(limit, clear);
        });

        // DELETE /errors - Clear all errors
        _server.Delete("errors", _ =>
        {
            _errorCapture!.Clear();
            return new { success = true, message = "Errors cleared" };
        });

        // POST /screenshot - Capture screenshot (returns base64 PNG)
        _server.Post("screenshot", _ =>
        {
            ScreenshotResult? result = null;

            // Capture synchronously on game thread
            ExecuteOnGameThread(() =>
            {
                result = _screenshot!.CaptureToBase64();
                return result;
            });

            return result;
        });

        // POST /screenshot/file - Capture screenshot to file
        _server.Post("screenshot/file", req =>
        {
            var body = TestApiServer.ReadBody<ScreenshotRequest>(req);
            var filename = body?.Filename;

            // Queue and wait for capture
            var task = _screenshot!.CaptureAsync(filename);

            // Wait for render to complete capture
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < timeout)
            {
                Thread.Sleep(16);
            }

            return task.IsCompleted ? task.Result : new ScreenshotResult
            {
                Success = false,
                Error = "Screenshot capture timed out"
            };
        });

        // GET /steam/lobby - Diagnose Steam lobby by ID (pass ?id=123456)
        _server.Get("steam/lobby", req =>
        {
            var lobbyIdStr = req.QueryString["id"];
            if (string.IsNullOrEmpty(lobbyIdStr) || !ulong.TryParse(lobbyIdStr, out var lobbyId))
            {
                return new { error = "Missing or invalid 'id' query parameter" };
            }
            return _coopController!.DiagnoseSteamLobby(lobbyId);
        });

        // POST /steam/lobby/join-diagnose - Join lobby, diagnose, then leave (pass ?id=123456)
        _server.Post("steam/lobby/join-diagnose", req =>
        {
            var lobbyIdStr = req.QueryString["id"];
            if (string.IsNullOrEmpty(lobbyIdStr) || !ulong.TryParse(lobbyIdStr, out var lobbyId))
            {
                return new { error = "Missing or invalid 'id' query parameter" };
            }
            _coopController!.DiagnoseSteamLobbyWithJoin(lobbyId);
            return new { success = true, message = "Join initiated - check logs for diagnostic output" };
        });

        // GET /openapi.json - OpenAPI 3.0 specification
        _server.GetRaw("openapi.json", _ => (
            OpenApiGenerator.GenerateJson(
                typeof(ApiDefinitions),
                "JunimoTestClient API",
                "0.1.0",
                "HTTP API for automated testing of Stardew Valley client"),
            "application/json"));

        // GET /openapi.yaml - OpenAPI 3.0 specification (YAML)
        _server.GetRaw("openapi.yaml", _ => (
            OpenApiGenerator.GenerateYaml(
                typeof(ApiDefinitions),
                "JunimoTestClient API",
                "0.1.0",
                "HTTP API for automated testing of Stardew Valley client"),
            "application/x-yaml"));

        // GET /docs - Scalar API documentation UI
        _server.GetRaw("docs", _ => (GetScalarHtml(), "text/html"));

        // GET /swagger - Swagger UI
        _server.GetRaw("swagger", _ => (GetSwaggerHtml(), "text/html"));
    }

    private string GetSwaggerHtml()
    {
        var port = _server?.Port ?? DefaultPort;
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>JunimoTestClient API - Swagger</title>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui.css"" />
</head>
<body>
    <div id=""swagger-ui""></div>
    <script src=""https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-bundle.js""></script>
    <script src=""https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-standalone-preset.js""></script>
    <script>
        window.onload = () => {{
            SwaggerUIBundle({{
                url: 'http://localhost:{port}/openapi.json',
                dom_id: '#swagger-ui',
                presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
                layout: 'StandaloneLayout'
            }});
        }};
    </script>
</body>
</html>";
    }

    private string GetScalarHtml()
    {
        var port = _server?.Port ?? DefaultPort;
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>JunimoTestClient API</title>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
</head>
<body>
    <script id=""api-reference"" data-url=""http://localhost:{port}/openapi.json""></script>
    <script src=""https://cdn.jsdelivr.net/npm/@scalar/api-reference""></script>
</body>
</html>";
    }

    #region Game Thread Execution

    private readonly Queue<Action> _gameActions = new();
    private readonly object _actionLock = new();

    private T ExecuteOnGameThread<T>(Func<T> action, int timeoutMs = 5000)
    {
        T result = default!;
        var completed = false;
        Exception? error = null;

        QueueGameAction(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
                _errorCapture?.CaptureException("GameThread", ex);
            }
            finally
            {
                completed = true;
            }
        });

        // Wait for completion
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!completed && DateTime.UtcNow < timeout)
        {
            Thread.Sleep(16); // ~60fps
        }

        if (error != null)
            throw error;

        return result;
    }

    private void QueueGameAction(Action action)
    {
        lock (_actionLock)
        {
            _gameActions.Enqueue(action);
        }
    }

    private void OnUpdateTicked(object? sender, UnvalidatedUpdateTickedEventArgs e)
    {
        // Execute any queued actions on the game thread
        lock (_actionLock)
        {
            while (_gameActions.Count > 0)
            {
                try
                {
                    var action = _gameActions.Dequeue();
                    action();
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error executing queued action: {ex.Message}", LogLevel.Error);
                    _errorCapture?.CaptureException("QueuedAction", ex);
                }
            }
        }
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        // Handle pending screenshot captures
        _screenshot?.OnPostRender();
    }

    #endregion

    #region Wait/Polling

    private WaitResult WaitForCondition(Func<bool> condition, int timeoutMs, string conditionName)
    {
        var startTime = DateTime.UtcNow;
        var deadline = startTime.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            bool satisfied = false;

            // Check condition on game thread
            var checkCompleted = false;
            QueueGameAction(() =>
            {
                try
                {
                    satisfied = condition();
                }
                catch
                {
                    satisfied = false;
                }
                finally
                {
                    checkCompleted = true;
                }
            });

            // Wait for check to complete
            var checkTimeout = DateTime.UtcNow.AddSeconds(2);
            while (!checkCompleted && DateTime.UtcNow < checkTimeout)
            {
                Thread.Sleep(16);
            }

            if (satisfied)
            {
                return new WaitResult
                {
                    Success = true,
                    Condition = conditionName,
                    WaitedMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds
                };
            }

            Thread.Sleep(100); // Poll every 100ms
        }

        return new WaitResult
        {
            Success = false,
            Condition = conditionName,
            Error = $"Timeout waiting for {conditionName} after {timeoutMs}ms",
            WaitedMs = timeoutMs
        };
    }

    #endregion

    #region Event Logging

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        Monitor.Log("Returned to title screen", LogLevel.Trace);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        Monitor.Log($"Save loaded - Farmer: {StardewValley.Game1.player?.Name}", LogLevel.Trace);
        LogSpawnInfo("SaveLoaded");

        // Increase chat buffer size for testing (default is 10, which truncates long command outputs)
        // TODO: Add pagination to server command output so commands with many lines don't overflow the chat buffer
        if (Game1.chatBox != null)
        {
            Game1.chatBox.maxMessages = 20;
            Monitor.Log("Increased chat buffer to 20 messages for testing", LogLevel.Trace);
        }
    }

    private void OnPlayerWarped(object? sender, StardewModdingAPI.Events.WarpedEventArgs e)
    {
        if (e.IsLocalPlayer)
        {
            Monitor.Log($"[Spawn] Player warped: {e.OldLocation?.Name ?? "null"} -> {e.NewLocation?.Name ?? "null"}", LogLevel.Info);
            LogSpawnInfo("AfterWarp");
        }
    }

    private void OnPeerConnected(object? sender, StardewModdingAPI.Events.PeerConnectedEventArgs e)
    {
        Monitor.Log($"[Spawn] Peer connected: {e.Peer.PlayerID}", LogLevel.Info);
        LogSpawnInfo("PeerConnected");
    }

    private void LogSpawnInfo(string context)
    {
        // Verbose spawn debugging - commented out to reduce log noise
        // var player = Game1.player;
        // if (player == null)
        // {
        //     Monitor.Log($"[Spawn:{context}] Player is null", LogLevel.Warn);
        //     return;
        // }
        // Monitor.Log($"[Spawn:{context}] ========== SPAWN DEBUG INFO ==========", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] Player ID: {player.UniqueMultiplayerID}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] Player Name: {player.Name}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] isCustomized: {player.isCustomized.Value}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] currentLocation: {player.currentLocation?.NameOrUniqueName ?? "null"}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] Position: {player.Position}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] TileLocation: {player.Tile}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] homeLocation: {player.homeLocation.Value}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] lastSleepLocation: {player.lastSleepLocation.Value ?? "null"}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] lastSleepPoint: {player.lastSleepPoint.Value}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] sleptInTemporaryBed: {player.sleptInTemporaryBed.Value}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] disconnectDay: {player.disconnectDay.Value}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] disconnectLocation: {player.disconnectLocation.Value ?? "null"}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] disconnectPosition: {player.disconnectPosition.Value}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] Game1.currentLocation: {Game1.currentLocation?.NameOrUniqueName ?? "null"}", LogLevel.Alert);
        // Monitor.Log($"[Spawn:{context}] ======================================", LogLevel.Alert);
    }

    #endregion

    #region Steam Diagnostics

    private static IMonitor? _staticMonitor;

    private void ApplySteamDiagnostics(IModHelper helper)
    {
        _staticMonitor = Monitor;

        try
        {
            var harmony = new Harmony("JunimoTestClient.SteamDiagnostics");

            // Patch SteamMatchmaking.SetLobbyGameServer to log what the vanilla client sends
            var setLobbyGameServerMethod = AccessTools.Method(
                typeof(SteamMatchmaking),
                nameof(SteamMatchmaking.SetLobbyGameServer));

            if (setLobbyGameServerMethod != null)
            {
                harmony.Patch(
                    setLobbyGameServerMethod,
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(SetLobbyGameServer_Prefix)));
                Monitor.Log("Patched SteamMatchmaking.SetLobbyGameServer for diagnostics", LogLevel.Debug);
            }
            else
            {
                Monitor.Log("Could not find SteamMatchmaking.SetLobbyGameServer method", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to apply Steam diagnostics: {ex.Message}", LogLevel.Error);
        }
    }

    private static bool SetLobbyGameServer_Prefix(CSteamID steamIDLobby, uint unGameServerIP, ushort unGameServerPort, CSteamID steamIDGameServer)
    {
        _staticMonitor?.Log("=== SetLobbyGameServer CALLED ===", LogLevel.Alert);
        _staticMonitor?.Log($"  Lobby ID: {steamIDLobby.m_SteamID}", LogLevel.Alert);
        _staticMonitor?.Log($"  Game Server IP: {unGameServerIP} (0x{unGameServerIP:X8})", LogLevel.Alert);
        _staticMonitor?.Log($"  Game Server Port: {unGameServerPort}", LogLevel.Alert);
        _staticMonitor?.Log($"  Game Server Steam ID: {steamIDGameServer.m_SteamID}", LogLevel.Alert);
        _staticMonitor?.Log($"  Game Server Steam ID Valid: {steamIDGameServer.IsValid()}", LogLevel.Alert);
        _staticMonitor?.Log("=================================", LogLevel.Alert);

        // Let the original method run
        return true;
    }

    #endregion
}

#region Request DTOs

public class NavigateRequest
{
    public string Target { get; set; } = "";
}

public class CoopTabRequest
{
    public int Tab { get; set; }
}

public class JoinInviteRequest
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

public class WaitResult
{
    public bool Success { get; set; }
    public string? Condition { get; set; }
    public string? Error { get; set; }
    public int WaitedMs { get; set; }
}

public class ScreenshotRequest
{
    public string? Filename { get; set; }
}

#endregion
