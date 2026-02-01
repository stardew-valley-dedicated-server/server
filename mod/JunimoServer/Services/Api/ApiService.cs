using JunimoServer.Services.CabinManager;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Services.Settings;
using JunimoServer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.SDKs.GogGalaxy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JunimoServer.Services.Api
{
    #region API Models

    /// <summary>
    /// Server status including player count and game state.
    /// </summary>
    public class ServerStatus
    {
        /// <summary>Number of connected players.</summary>
        public int PlayerCount { get; set; }

        /// <summary>Maximum allowed players.</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Steam invite code (S-prefixed). Only available when Steam SDR is enabled.</summary>
        public string? SteamInviteCode { get; set; }

        /// <summary>GOG/Galaxy invite code (G-prefixed). Always available when server is online.</summary>
        public string? GogInviteCode { get; set; }

        /// <summary>Server mod version.</summary>
        public string ServerVersion { get; set; } = "";

        /// <summary>Whether the server is online and hosting.</summary>
        public bool IsOnline { get; set; }

        /// <summary>Whether the server is ready to accept new connections (false during day transitions, saving, etc.).</summary>
        public bool IsReady { get; set; }

        /// <summary>ISO 8601 timestamp of last update.</summary>
        public string LastUpdated { get; set; } = "";

        /// <summary>Name of the farm.</summary>
        public string FarmName { get; set; } = "";

        /// <summary>Current day of the month (1-28).</summary>
        public int Day { get; set; }

        /// <summary>Current season (spring, summer, fall, winter).</summary>
        public string Season { get; set; } = "";

        /// <summary>Current year.</summary>
        public int Year { get; set; }

        /// <summary>Current time in 24-hour format (e.g., 1430 = 2:30 PM).</summary>
        public int TimeOfDay { get; set; }
    }

    /// <summary>
    /// Information about a connected player.
    /// </summary>
    public class PlayerInfo
    {
        /// <summary>Unique player ID.</summary>
        public long Id { get; set; }

        /// <summary>Player's display name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Whether the player is currently online.</summary>
        public bool IsOnline { get; set; }
    }

    /// <summary>
    /// Response containing list of connected players.
    /// </summary>
    public class PlayersResponse
    {
        /// <summary>List of connected players.</summary>
        public List<PlayerInfo> Players { get; set; } = new();
    }

    /// <summary>
    /// Response containing the server invite code.
    /// </summary>
    public class InviteCodeResponse
    {
        /// <summary>The invite code, or null if not available.</summary>
        public string? InviteCode { get; set; }

        /// <summary>Error message if invite code is not available.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Health check response.
    /// </summary>
    public class HealthResponse
    {
        /// <summary>Health status.</summary>
        public string Status { get; set; } = "ok";

        /// <summary>ISO 8601 timestamp.</summary>
        public string Timestamp { get; set; } = "";
    }

    /// <summary>
    /// Response for farmhand operations.
    /// </summary>
    public class FarmhandResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Message describing the result.</summary>
        public string? Message { get; set; }

        /// <summary>Error message if operation failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Information about a farmhand slot.
    /// </summary>
    public class FarmhandInfo
    {
        /// <summary>Unique multiplayer ID.</summary>
        public long Id { get; set; }

        /// <summary>Farmhand's display name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Whether the farmhand has been customized.</summary>
        public bool IsCustomized { get; set; }
    }

    /// <summary>
    /// Response containing list of farmhands.
    /// </summary>
    public class FarmhandsResponse
    {
        /// <summary>List of farmhand slots.</summary>
        public List<FarmhandInfo> Farmhands { get; set; } = new();
    }

    /// <summary>
    /// Current rendering status.
    /// </summary>
    public class RenderingStatus
    {
        /// <summary>Whether rendering is currently enabled.</summary>
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Response from rendering toggle operation.
    /// </summary>
    public class RenderingToggleResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Current rendering state after the operation.</summary>
        public bool Enabled { get; set; }

        /// <summary>Message describing the result.</summary>
        public string? Message { get; set; }

        /// <summary>Error message if operation failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Response from time set operation.
    /// </summary>
    public class TimeSetResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Current time of day after the operation.</summary>
        public int TimeOfDay { get; set; }

        /// <summary>Message describing the result.</summary>
        public string? Message { get; set; }

        /// <summary>Error message if operation failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Current server settings (from server-settings.json).
    /// </summary>
    public class SettingsResponse
    {
        /// <summary>Game creation settings (immutable after game created).</summary>
        public GameSettingsInfo Game { get; set; } = new();

        /// <summary>Server runtime settings (applied on every startup).</summary>
        public ServerRuntimeSettingsInfo Server { get; set; } = new();
    }

    /// <summary>
    /// Game creation settings.
    /// </summary>
    public class GameSettingsInfo
    {
        /// <summary>Farm name.</summary>
        public string FarmName { get; set; } = "";

        /// <summary>Farm type ID (0=Standard, 1=Riverland, 2=Forest, 3=Hilltop, 4=Wilderness, 5=Four Corners, 6=Beach, 7=Meadowlands).</summary>
        public int FarmType { get; set; }

        /// <summary>Profit margin multiplier (1.0 = normal).</summary>
        public float ProfitMargin { get; set; }

        /// <summary>Number of starting cabins.</summary>
        public int StartingCabins { get; set; }

        /// <summary>Spawn monsters at night: "true", "false", or "auto".</summary>
        public string SpawnMonstersAtNight { get; set; } = "";
    }

    /// <summary>
    /// Server runtime settings.
    /// </summary>
    public class ServerRuntimeSettingsInfo
    {
        /// <summary>Maximum number of players allowed.</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Cabin strategy: CabinStack, FarmhouseStack, or None.</summary>
        public string CabinStrategy { get; set; } = "";

        /// <summary>Whether each player has a separate wallet.</summary>
        public bool SeparateWallets { get; set; }

        /// <summary>How to handle existing visible cabins: KeepExisting or MoveToStack.</summary>
        public string ExistingCabinBehavior { get; set; } = "";
    }

    /// <summary>
    /// Cabin state snapshot.
    /// </summary>
    public class CabinsResponse
    {
        /// <summary>Active cabin strategy.</summary>
        public string Strategy { get; set; } = "";

        /// <summary>Total number of cabins on the farm.</summary>
        public int TotalCount { get; set; }

        /// <summary>Number of cabins assigned to players.</summary>
        public int AssignedCount { get; set; }

        /// <summary>Number of cabins available for new players.</summary>
        public int AvailableCount { get; set; }

        /// <summary>Individual cabin details.</summary>
        public List<CabinInfo> Cabins { get; set; } = new();
    }

    /// <summary>
    /// Information about a single cabin.
    /// </summary>
    public class CabinInfo
    {
        /// <summary>Tile X position on the farm.</summary>
        public int TileX { get; set; }

        /// <summary>Tile Y position on the farm.</summary>
        public int TileY { get; set; }

        /// <summary>Whether the cabin is at the hidden out-of-bounds location.</summary>
        public bool IsHidden { get; set; }

        /// <summary>Owner's multiplayer ID (0 if unassigned).</summary>
        public long OwnerId { get; set; }

        /// <summary>Owner's display name (empty if unassigned).</summary>
        public string OwnerName { get; set; } = "";

        /// <summary>Whether the cabin has an assigned owner.</summary>
        public bool IsAssigned { get; set; }
    }

    #endregion

    /// <summary>
    /// HTTP API service using HttpListener with NSwag OpenAPI support.
    /// </summary>
    public class ApiService : ModService
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private bool _isRunning;
        private string? _openApiSpec;

        private readonly ServerSettingsLoader _settings;
        private readonly PersistentOptions _persistentOptions;
        private readonly CabinManagerService _cabinManager;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        public ApiService(IModHelper helper, IMonitor monitor, ServerSettingsLoader settings, PersistentOptions persistentOptions, CabinManagerService cabinManager) : base(helper, monitor)
        {
            _settings = settings;
            _persistentOptions = persistentOptions;
            _cabinManager = cabinManager;
        }

        public override void Entry()
        {
            if (!Env.ApiEnabled)
            {
                Monitor.Log("API service is disabled (API_ENABLED=false)", LogLevel.Info);
                return;
            }

            Helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            StartServer();
        }

        private void StartServer()
        {
            if (_isRunning) return;

            try
            {
                // Generate OpenAPI spec
                var document = OpenApiGenerator.Generate(
                    typeof(ApiService),
                    "Stardew Dedicated Server API",
                    "v1",
                    "HTTP API for monitoring and interacting with the Stardew Valley dedicated server"
                );
                _openApiSpec = document.ToJson();

                // Create and configure listener
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{Env.ApiPort}/");
                _listener.Start();

                // Start processing requests
                _cts = new CancellationTokenSource();
                _serverTask = Task.Run(() => ProcessRequestsAsync(_cts.Token));

                _isRunning = true;

                Monitor.Log($"API server listening on port {Env.ApiPort} (docs: http://localhost:{Env.ApiPort}/docs)", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to start API server: {ex}", LogLevel.Error);
            }
        }

        private async Task ProcessRequestsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(context), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    // Expected during shutdown
                }
                catch (ObjectDisposedException)
                {
                    // Listener was disposed
                    break;
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error accepting request: {ex}", LogLevel.Warn);
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var path = request.Url?.AbsolutePath ?? "/";
                var method = request.HttpMethod;

                // Route request
                if (method == "GET")
                {
                    switch (path)
                    {
                        case "/status":
                            await WriteJsonAsync(response, HandleGetStatus());
                            break;
                        case "/players":
                            await WriteJsonAsync(response, HandleGetPlayers());
                            break;
                        case "/invite-code":
                            await WriteJsonAsync(response, HandleGetInviteCode());
                            break;
                        case "/farmhands":
                            await WriteJsonAsync(response, HandleGetFarmhands());
                            break;
                        case "/health":
                            await WriteJsonAsync(response, HandleGetHealth());
                            break;
                        case "/settings":
                            await WriteJsonAsync(response, HandleGetSettings());
                            break;
                        case "/cabins":
                            await WriteJsonAsync(response, HandleGetCabins());
                            break;
                        case "/rendering":
                            await WriteJsonAsync(response, HandleGetRendering());
                            break;
                        case "/swagger/v1/swagger.json":
                            await WriteJsonRawAsync(response, _openApiSpec ?? "{}");
                            break;
                        case "/docs":
                            await WriteHtmlAsync(response, GetScalarHtml());
                            break;
                        default:
                            response.StatusCode = 404;
                            await WriteJsonAsync(response, new { error = "Not found" });
                            break;
                    }
                }
                else if (method == "POST")
                {
                    switch (path)
                    {
                        case "/rendering":
                            var enabledParam = request.QueryString["enabled"];
                            await WriteJsonAsync(response, HandlePostRendering(enabledParam));
                            break;
                        case "/time":
                            var timeParam = request.QueryString["value"];
                            await WriteJsonAsync(response, HandlePostTime(timeParam));
                            break;
                        default:
                            response.StatusCode = 404;
                            await WriteJsonAsync(response, new { error = "Not found" });
                            break;
                    }
                }
                else if (method == "DELETE")
                {
                    switch (path)
                    {
                        case "/farmhands":
                            var nameParam = request.QueryString["name"];
                            await WriteJsonAsync(response, HandleDeleteFarmhand(nameParam));
                            break;
                        default:
                            response.StatusCode = 404;
                            await WriteJsonAsync(response, new { error = "Not found" });
                            break;
                    }
                }
                else
                {
                    response.StatusCode = 405;
                    await WriteJsonAsync(response, new { error = "Method not allowed" });
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error handling request: {ex}", LogLevel.Warn);
                try
                {
                    response.StatusCode = 500;
                    await WriteJsonAsync(response, new { error = "Internal server error" });
                }
                catch (Exception writeEx)
                {
                    Monitor.Log($"Failed to write error response: {writeEx}", LogLevel.Warn);
                }
            }
            finally
            {
                try { response.Close(); }
                catch (Exception closeEx)
                {
                    Monitor.Log($"Failed to close response: {closeEx}", LogLevel.Debug);
                }
            }
        }

        #region Endpoint Handlers

        [ApiEndpoint("GET", "/status", Summary = "Get server status", Tag = "Server")]
        [ApiResponse(typeof(ServerStatus), 200, Description = "Server status and game state")]
        private ServerStatus HandleGetStatus()
        {
            var modInfo = Helper.ModRegistry.Get("JunimoHost.Server");
            var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";

            // Use invite code file as the source of truth for "online" status.
            // Context.IsWorldReady may not be set during new game creation, even though
            // the server is fully hosting and accepting connections.
            var inviteCode = InviteCodeFile.Read(Monitor);
            var isOnline = !string.IsNullOrEmpty(inviteCode) && Game1.IsServer;

            // Derive Steam and GOG invite codes from the base code
            string? steamInviteCode = null;
            string? gogInviteCode = null;
            if (!string.IsNullOrEmpty(inviteCode))
            {
                var baseCode = inviteCode.Length > 1 ? inviteCode.Substring(1) : inviteCode;
                gogInviteCode = GalaxyNetHelper.GalaxyInvitePrefix + baseCode;
                if (SteamGameServer.SteamGameServerService.IsInitialized)
                {
                    steamInviteCode = GalaxyNetHelper.SteamInvitePrefix + baseCode;
                }
            }

            if (!isOnline)
            {
                return new ServerStatus
                {
                    PlayerCount = 0,
                    MaxPlayers = 4,
                    ServerVersion = version,
                    IsOnline = false,
                    IsReady = false,
                    LastUpdated = DateTime.UtcNow.ToString("o")
                };
            }

            try
            {
                var playerCount = Game1.server?.connectionsCount ?? 0;
                var maxPlayers = Game1.netWorldState.Value?.CurrentPlayerLimit ?? 4;
                var isReady = Game1.server?.isGameAvailable() ?? false;

                return new ServerStatus
                {
                    PlayerCount = playerCount,
                    MaxPlayers = maxPlayers,
                    SteamInviteCode = steamInviteCode,
                    GogInviteCode = gogInviteCode,
                    ServerVersion = version,
                    IsOnline = true,
                    IsReady = isReady,
                    LastUpdated = DateTime.UtcNow.ToString("o"),
                    FarmName = Game1.player?.farmName.Value ?? "",
                    Day = Game1.dayOfMonth,
                    Season = Game1.currentSeason ?? "",
                    Year = Game1.year,
                    TimeOfDay = Game1.timeOfDay
                };
            }
            catch (Exception ex)
            {
                // Game state access from the API thread can race with the game loop;
                // return a minimal online response if property reads fail.
                Monitor.Log($"Game state read failed (race with game loop): {ex}", LogLevel.Debug);
                return new ServerStatus
                {
                    SteamInviteCode = steamInviteCode,
                    GogInviteCode = gogInviteCode,
                    ServerVersion = version,
                    IsOnline = true,
                    IsReady = false,
                    LastUpdated = DateTime.UtcNow.ToString("o")
                };
            }
        }

        [ApiEndpoint("GET", "/players", Summary = "Get connected players", Tag = "Server")]
        [ApiResponse(typeof(PlayersResponse), 200, Description = "List of connected players")]
        private PlayersResponse HandleGetPlayers()
        {
            var players = new List<PlayerInfo>();

            if (Game1.gameMode != 3 || !Game1.IsServer)
            {
                return new PlayersResponse { Players = players };
            }

            try
            {
                foreach (var farmer in Game1.getOnlineFarmers())
                {
                    // Skip the host (server bot)
                    if (farmer.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
                    {
                        continue;
                    }

                    players.Add(new PlayerInfo
                    {
                        Id = farmer.UniqueMultiplayerID,
                        Name = farmer.Name ?? farmer.displayName ?? "Unknown",
                        IsOnline = true
                    });
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error building player list: {ex}", LogLevel.Warn);
            }

            return new PlayersResponse { Players = players };
        }

        [ApiEndpoint("GET", "/invite-code", Summary = "Get invite code", Tag = "Server")]
        [ApiResponse(typeof(InviteCodeResponse), 200, Description = "Current server invite code")]
        private InviteCodeResponse HandleGetInviteCode()
        {
            var inviteCode = InviteCodeFile.Read(Monitor);
            if (string.IsNullOrEmpty(inviteCode))
            {
                return new InviteCodeResponse { InviteCode = null, Error = "No invite code available" };
            }
            return new InviteCodeResponse { InviteCode = inviteCode };
        }

        [ApiEndpoint("GET", "/health", Summary = "Health check", Tag = "Health")]
        [ApiResponse(typeof(HealthResponse), 200, Description = "Health status")]
        private HealthResponse HandleGetHealth()
        {
            return new HealthResponse
            {
                Status = "ok",
                Timestamp = DateTime.UtcNow.ToString("o")
            };
        }

        [ApiEndpoint("GET", "/farmhands", Summary = "Get all farmhand slots", Tag = "Farmhands")]
        [ApiResponse(typeof(FarmhandsResponse), 200, Description = "List of farmhand slots")]
        private FarmhandsResponse HandleGetFarmhands()
        {
            var farmhands = new List<FarmhandInfo>();

            if (Game1.gameMode != 3 || !Game1.IsServer)
            {
                return new FarmhandsResponse { Farmhands = farmhands };
            }

            try
            {
                foreach (var farmer in Game1.getAllFarmhands())
                {
                    farmhands.Add(new FarmhandInfo
                    {
                        Id = farmer.UniqueMultiplayerID,
                        Name = farmer.Name ?? "",
                        IsCustomized = farmer.isCustomized.Value
                    });
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error getting farmhands: {ex}", LogLevel.Warn);
            }

            return new FarmhandsResponse { Farmhands = farmhands };
        }

        [ApiEndpoint("GET", "/settings", Summary = "Get server settings", Tag = "Settings")]
        [ApiResponse(typeof(SettingsResponse), 200, Description = "Current server settings")]
        private SettingsResponse HandleGetSettings()
        {
            var raw = _settings.Raw;
            return new SettingsResponse
            {
                Game = new GameSettingsInfo
                {
                    FarmName = raw.Game.FarmName,
                    FarmType = raw.Game.FarmType,
                    ProfitMargin = raw.Game.ProfitMargin,
                    StartingCabins = raw.Game.StartingCabins,
                    SpawnMonstersAtNight = raw.Game.SpawnMonstersAtNight
                },
                Server = new ServerRuntimeSettingsInfo
                {
                    MaxPlayers = raw.Server.MaxPlayers,
                    CabinStrategy = raw.Server.CabinStrategy,
                    SeparateWallets = raw.Server.SeparateWallets,
                    ExistingCabinBehavior = raw.Server.ExistingCabinBehavior
                }
            };
        }

        [ApiEndpoint("GET", "/cabins", Summary = "Get cabin state and positions", Tag = "Cabins")]
        [ApiResponse(typeof(CabinsResponse), 200, Description = "Cabin state snapshot")]
        private CabinsResponse HandleGetCabins()
        {
            var result = new CabinsResponse
            {
                Strategy = _persistentOptions.Data.CabinStrategy.ToString()
            };

            if (Game1.gameMode != 3 || !Game1.IsServer)
            {
                return result;
            }

            try
            {
                var farm = Game1.getFarm();
                var cabinBuildings = farm.buildings.Where(b => b.isCabin).ToList();

                foreach (var building in cabinBuildings)
                {
                    var cabin = building.GetIndoors<Cabin>();
                    var ownerId = cabin?.owner?.UniqueMultiplayerID ?? 0;
                    var ownerName = cabin?.owner?.Name ?? "";
                    var isAssigned = ownerId != 0 && cabin?.owner?.isCustomized.Value == true;

                    result.Cabins.Add(new CabinInfo
                    {
                        TileX = building.tileX.Value,
                        TileY = building.tileY.Value,
                        IsHidden = building.IsInHiddenStack(),
                        OwnerId = ownerId,
                        OwnerName = ownerName,
                        IsAssigned = isAssigned
                    });
                }

                result.TotalCount = result.Cabins.Count;
                result.AssignedCount = result.Cabins.Count(c => c.IsAssigned);
                result.AvailableCount = result.TotalCount - result.AssignedCount;
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error getting cabin state: {ex}", LogLevel.Warn);
            }

            return result;
        }

        [ApiEndpoint("GET", "/rendering", Summary = "Get rendering status", Tag = "Server")]
        [ApiResponse(typeof(RenderingStatus), 200, Description = "Current rendering state")]
        private RenderingStatus HandleGetRendering()
        {
            return new RenderingStatus
            {
                Enabled = ServerOptimizerOverrides.IsRenderingEnabled()
            };
        }

        [ApiEndpoint("POST", "/rendering", Summary = "Toggle rendering on or off", Tag = "Server")]
        [ApiResponse(typeof(RenderingToggleResponse), 200, Description = "Rendering toggled")]
        private RenderingToggleResponse HandlePostRendering(string? enabled)
        {
            if (string.IsNullOrEmpty(enabled))
            {
                return new RenderingToggleResponse
                {
                    Success = false,
                    Enabled = ServerOptimizerOverrides.IsRenderingEnabled(),
                    Error = "Missing 'enabled' query parameter (true or false)"
                };
            }

            if (!bool.TryParse(enabled, out var enableRendering))
            {
                return new RenderingToggleResponse
                {
                    Success = false,
                    Enabled = ServerOptimizerOverrides.IsRenderingEnabled(),
                    Error = $"Invalid value '{enabled}' for 'enabled' parameter (expected true or false)"
                };
            }

            ServerOptimizerOverrides.ToggleRendering(enableRendering, Monitor);

            return new RenderingToggleResponse
            {
                Success = true,
                Enabled = enableRendering,
                Message = enableRendering ? "Rendering enabled" : "Rendering disabled"
            };
        }

        [ApiEndpoint("POST", "/time", Summary = "Set game time of day", Tag = "Server")]
        [ApiResponse(typeof(TimeSetResponse), 200, Description = "Time set")]
        private TimeSetResponse HandlePostTime(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new TimeSetResponse
                {
                    Success = false,
                    TimeOfDay = Game1.timeOfDay,
                    Error = "Missing 'value' query parameter (e.g., 1200 for noon)"
                };
            }

            if (!int.TryParse(value, out var time))
            {
                return new TimeSetResponse
                {
                    Success = false,
                    TimeOfDay = Game1.timeOfDay,
                    Error = $"Invalid value '{value}' (expected integer like 1200 for noon)"
                };
            }

            if (time < 600 || time > 2600)
            {
                return new TimeSetResponse
                {
                    Success = false,
                    TimeOfDay = Game1.timeOfDay,
                    Error = $"Value {time} out of range (600-2600)"
                };
            }

            Game1.timeOfDay = time;
            Monitor.Log($"Time set to {time} via API", LogLevel.Info);

            return new TimeSetResponse
            {
                Success = true,
                TimeOfDay = time,
                Message = $"Time set to {time}"
            };
        }

        [ApiEndpoint("DELETE", "/farmhands", Summary = "Delete a farmhand by name", Tag = "Farmhands")]
        [ApiResponse(typeof(FarmhandResponse), 200, Description = "Farmhand deleted")]
        private FarmhandResponse HandleDeleteFarmhand(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return new FarmhandResponse { Success = false, Error = "Missing 'name' query parameter" };
            }

            if (Game1.gameMode != 3 || !Game1.IsServer)
            {
                return new FarmhandResponse { Success = false, Error = "Server not ready" };
            }

            try
            {
                // Find the farmhand by name
                Farmer? targetFarmhand = null;
                foreach (var farmer in Game1.getAllFarmhands())
                {
                    if (farmer.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        targetFarmhand = farmer;
                        break;
                    }
                }

                if (targetFarmhand == null)
                {
                    return new FarmhandResponse { Success = false, Error = $"Farmhand '{name}' not found" };
                }

                // Check if farmhand is online
                if (Game1.getOnlineFarmers().Any(f => f.UniqueMultiplayerID == targetFarmhand.UniqueMultiplayerID))
                {
                    return new FarmhandResponse { Success = false, Error = $"Cannot delete farmhand '{name}' - currently online" };
                }

                var farmhandId = targetFarmhand.UniqueMultiplayerID;

                // Find and remove the cabin owned by this farmhand
                var farm = Game1.getFarm();
                Building? cabinToRemove = null;
                foreach (var building in farm.buildings)
                {
                    if (!building.isCabin) continue;

                    var cabin = building.GetIndoors<Cabin>();
                    if (cabin?.owner?.UniqueMultiplayerID == farmhandId)
                    {
                        cabinToRemove = building;
                        break;
                    }
                }

                // Remove the cabin building entirely
                if (cabinToRemove != null)
                {
                    farm.buildings.Remove(cabinToRemove);
                    Monitor.Log($"Removed cabin building for farmhand '{name}'", LogLevel.Debug);
                }

                // Remove from farmhandData so the slot no longer appears to clients
                if (Game1.netWorldState.Value.farmhandData.FieldDict.ContainsKey(farmhandId))
                {
                    Game1.netWorldState.Value.farmhandData.FieldDict.Remove(farmhandId);
                    Monitor.Log($"Removed farmhand from farmhandData", LogLevel.Debug);
                }

                // Remove from cabin manager tracking
                if (_cabinManager.Data.AllPlayerIdsEverJoined.Remove(farmhandId))
                {
                    _cabinManager.Data.Write();
                    Monitor.Log($"Removed farmhand from cabin tracking", LogLevel.Debug);
                }

                Monitor.Log($"Deleted farmhand '{name}' (ID: {farmhandId})", LogLevel.Info);

                return new FarmhandResponse
                {
                    Success = true,
                    Message = $"Farmhand '{name}' deleted successfully"
                };
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error deleting farmhand: {ex}", LogLevel.Error);
                return new FarmhandResponse { Success = false, Error = ex.Message };
            }
        }

        #endregion

        #region Response Helpers

        private static async Task WriteJsonAsync(HttpListenerResponse response, object data)
        {
            var json = JsonConvert.SerializeObject(data, JsonSettings);
            await WriteJsonRawAsync(response, json);
        }

        private static async Task WriteJsonRawAsync(HttpListenerResponse response, string json)
        {
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
        {
            response.ContentType = "text/html";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private string GetScalarHtml()
        {
            return $@"<!DOCTYPE html>
<html>
<head>
    <title>Stardew Dedicated Server API</title>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
</head>
<body>
    <script id=""api-reference"" data-url=""http://localhost:{Env.ApiPort}/swagger/v1/swagger.json""></script>
    <script src=""https://cdn.jsdelivr.net/npm/@scalar/api-reference""></script>
</body>
</html>";
        }

        #endregion

        public async Task StopServerAsync()
        {
            if (!_isRunning) return;

            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();

                if (_serverTask != null)
                {
                    await Task.WhenAny(_serverTask, Task.Delay(5000));
                }

                _isRunning = false;
                Monitor.Log("API server stopped", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error stopping API server: {ex}", LogLevel.Warn);
            }
        }
    }
}
