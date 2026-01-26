using JunimoServer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
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

        /// <summary>Server invite code for joining.</summary>
        public string InviteCode { get; set; } = "";

        /// <summary>Server mod version.</summary>
        public string ServerVersion { get; set; } = "";

        /// <summary>Whether the server is online and ready.</summary>
        public bool IsOnline { get; set; }

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

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        public ApiService(IModHelper helper, IMonitor monitor) : base(helper, monitor)
        {
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

                Monitor.Log($"API server started on port {Env.ApiPort}", LogLevel.Info);
                Monitor.Log($"  GET  /status             - Server status and game state", LogLevel.Info);
                Monitor.Log($"  GET  /players            - Connected players list", LogLevel.Info);
                Monitor.Log($"  GET  /invite-code        - Current invite code", LogLevel.Info);
                Monitor.Log($"  GET  /farmhands          - List all farmhand slots", LogLevel.Info);
                Monitor.Log($"  DELETE /farmhands?name=X - Delete farmhand by name", LogLevel.Info);
                Monitor.Log($"  GET  /health             - Health check", LogLevel.Info);
                Monitor.Log($"  GET  /swagger/v1/swagger.json - OpenAPI specification", LogLevel.Info);
                Monitor.Log($"  GET  /docs               - Interactive API documentation", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to start API server: {ex.Message}", LogLevel.Error);
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
                    Monitor.Log($"Error accepting request: {ex.Message}", LogLevel.Warn);
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
                Monitor.Log($"Error handling request: {ex.Message}", LogLevel.Warn);
                try
                {
                    response.StatusCode = 500;
                    await WriteJsonAsync(response, new { error = "Internal server error" });
                }
                catch { /* Ignore errors when writing error response */ }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        #region Endpoint Handlers

        [ApiEndpoint("GET", "/status", Summary = "Get server status", Tag = "Server")]
        [ApiResponse(typeof(ServerStatus), 200, Description = "Server status and game state")]
        private ServerStatus HandleGetStatus()
        {
            var modInfo = Helper.ModRegistry.Get("JunimoHost.Server");
            var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";

            if (!Context.IsWorldReady || !Game1.IsServer)
            {
                return new ServerStatus
                {
                    PlayerCount = 0,
                    MaxPlayers = 4,
                    InviteCode = "",
                    ServerVersion = version,
                    IsOnline = false,
                    LastUpdated = DateTime.UtcNow.ToString("o")
                };
            }

            var inviteCode = InviteCodeFile.Read(Monitor);
            var playerCount = Game1.server?.connectionsCount ?? 0;
            var maxPlayers = Game1.netWorldState.Value?.CurrentPlayerLimit ?? 4;

            return new ServerStatus
            {
                PlayerCount = playerCount,
                MaxPlayers = maxPlayers,
                InviteCode = inviteCode ?? "",
                ServerVersion = version,
                IsOnline = true,
                LastUpdated = DateTime.UtcNow.ToString("o"),
                FarmName = Game1.player?.farmName.Value ?? "",
                Day = Game1.dayOfMonth,
                Season = Game1.currentSeason ?? "",
                Year = Game1.year,
                TimeOfDay = Game1.timeOfDay
            };
        }

        [ApiEndpoint("GET", "/players", Summary = "Get connected players", Tag = "Server")]
        [ApiResponse(typeof(PlayersResponse), 200, Description = "List of connected players")]
        private PlayersResponse HandleGetPlayers()
        {
            var players = new List<PlayerInfo>();

            if (!Context.IsWorldReady || !Game1.IsServer)
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
                Monitor.Log($"Error building player list: {ex.Message}", LogLevel.Warn);
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

            if (!Context.IsWorldReady || !Game1.IsServer)
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
                Monitor.Log($"Error getting farmhands: {ex.Message}", LogLevel.Warn);
            }

            return new FarmhandsResponse { Farmhands = farmhands };
        }

        [ApiEndpoint("DELETE", "/farmhands", Summary = "Delete a farmhand by name", Tag = "Farmhands")]
        [ApiResponse(typeof(FarmhandResponse), 200, Description = "Farmhand deleted")]
        private FarmhandResponse HandleDeleteFarmhand(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return new FarmhandResponse { Success = false, Error = "Missing 'name' query parameter" };
            }

            if (!Context.IsWorldReady || !Game1.IsServer)
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

                // Remove from farmhandData
                var farmhandId = targetFarmhand.UniqueMultiplayerID;
                Game1.netWorldState.Value.farmhandData.Remove(farmhandId);

                Monitor.Log($"Deleted farmhand '{name}' (ID: {farmhandId})", LogLevel.Info);

                return new FarmhandResponse
                {
                    Success = true,
                    Message = $"Farmhand '{name}' deleted successfully"
                };
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error deleting farmhand: {ex.Message}", LogLevel.Error);
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
                Monitor.Log($"Error stopping API server: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}
