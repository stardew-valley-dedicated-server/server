using JunimoServer.Services.CabinManager;
using JunimoServer.Services.CropSaver;
using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameManager;
using JunimoServer.Services.Lobby;
using JunimoServer.Services.PasswordProtection;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Roles;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Services.Settings;
using JunimoServer.Shared;
using JunimoServer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.SDKs.GogGalaxy;
using StardewValley.TerrainFeatures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
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

        /// <summary>Farm type key as returned by Game1.GetFarmTypeKey() (a vanilla name, or the farm Id for Data/AdditionalFarms farms).</summary>
        public string FarmTypeKey { get; set; } = "";

        /// <summary>Whether the game clock is currently paused (no players connected, or time not passing).</summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// Monotonic snapshot version used by /wait/* long-poll endpoints to
        /// detect a changed snapshot. Test clients pass this back as
        /// <c>?since=N</c> so the server only returns when a newer snapshot
        /// satisfies the requested filters.
        /// </summary>
        public long Version { get; set; }
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

        /// <summary>
        /// Monotonic snapshot version (same field as <see cref="ServerStatus.Version"/>).
        /// Test clients pass this back as <c>?since=N</c> on /wait/players to wait
        /// for a newer snapshot.
        /// </summary>
        public long Version { get; set; }
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
    /// Ground-truth server state for E2E test failure diagnosis. Dumps the
    /// internal fields that the periodic <c>GameStateSnapshot</c> aggregates
    /// from, plus the game-engine state usually hidden behind reflection
    /// (<c>netReady</c>, <c>newDaySync</c>, <c>activeClickableMenu</c>).
    /// Every sub-field is best-effort: if a reflection read fails, its entry
    /// becomes a <c>{"error":"..."}</c> object and the rest of the response
    /// still returns 200. Callers use this from failure-path handlers so a
    /// timeout becomes a diagnostic signal rather than a mystery.
    /// </summary>
    public class DiagnosticsStateResponse
    {
        public string CapturedAt { get; set; } = "";
        public long[] OtherFarmerUids { get; set; } = System.Array.Empty<long>();
        public int OnlineFarmerCount { get; set; }
        public List<ReadyCheckState> NetReady { get; set; } = new();
        public NewDaySyncState NewDaySync { get; set; } = new();
        public string? ActiveClickableMenu { get; set; }
        public int TimeOfDay { get; set; }
        public int DayOfMonth { get; set; }
        public string Season { get; set; } = "";
        public int Year { get; set; }
        public int GameMode { get; set; }
        public bool? IsGameAvailable { get; set; }
        public long? LastTickMs { get; set; }
        public double AvgGameThreadWaitMs { get; set; }
        /// <summary>Live cabin ownership read from <c>Game1.getFarm().buildings</c>.</summary>
        public List<DiagnosticsCabinState> Cabins { get; set; } = new();
        /// <summary>Farmhand slots from <c>Game1.netWorldState.Value.farmhandData</c>.</summary>
        public List<DiagnosticsFarmhandState> FarmhandData { get; set; } = new();
        /// <summary>UMIs in <c>Multiplayer.disconnectingFarmers</c> (mid-disconnect).</summary>
        public long[] DisconnectingFarmers { get; set; } = System.Array.Empty<long>();
        /// <summary>Fields that failed to be read. Empty on fully-successful dumps.</summary>
        public List<string> FailedFields { get; set; } = new();
    }

    public class DiagnosticsCabinState
    {
        public int TileX { get; set; }
        public int TileY { get; set; }
        public string IndoorsName { get; set; } = "";
        public long OwnerId { get; set; }
        public string OwnerName { get; set; } = "";
        public bool OwnerIsCustomized { get; set; }

        /// <summary>Whether the owner has a platform ID (Steam/GOG) stamped; true with
        /// OwnerIsCustomized=false is the abandoned-claim state. Resolved via cabin.owner, which
        /// yields the live otherFarmers copy while the owner is connected (so the in-flight userID
        /// stamp is visible here before disconnect persists it to farmhandData). Exposed as a bool,
        /// not the raw ID: /diagnostics/state is unauthenticated and the ID is a stable identifier.</summary>
        public bool OwnerHasUserId { get; set; }

        public string HomeLocationOfOwner { get; set; } = "";
        public bool FarmhandReferenceDefined { get; set; }
        public long FarmhandReferenceUid { get; set; }
    }

    public class DiagnosticsFarmhandState
    {
        public long UniqueMultiplayerId { get; set; }
        public string Name { get; set; } = "";
        public bool IsCustomized { get; set; }
        public string HomeLocation { get; set; } = "";
        public string LastSleepLocation { get; set; } = "";

        /// <summary>Whether a platform ID (Steam/GOG) is stamped on this slot; true with
        /// IsCustomized=false is the abandoned-claim state. Exposed as a bool, not the raw ID:
        /// /diagnostics/state is unauthenticated and the ID is a stable identifier.</summary>
        public bool HasUserId { get; set; }
    }

    public class ReadyCheckState
    {
        public string Id { get; set; } = "";
        public int NumberReady { get; set; }
        public int NumberRequired { get; set; }
        public bool IsReady { get; set; }
        public bool IsLocked { get; set; }
    }

    public class NewDaySyncState
    {
        public bool HasStarted { get; set; }
        public bool HasFinished { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Health check response.
    /// </summary>
    public class HealthResponse
    {
        /// <summary>Health status ("ok" or "degraded").</summary>
        public string Status { get; set; } = "ok";

        /// <summary>ISO 8601 timestamp.</summary>
        public string Timestamp { get; set; } = "";

        /// <summary>Milliseconds since the last game thread tick, or null if no tick recorded yet.</summary>
        public long? LastTickMs { get; set; }

        /// <summary>Number of actions queued for execution on the game thread.</summary>
        public int PendingActions { get; set; }

        /// <summary>Whether the game server reports itself as available for connections.</summary>
        public bool? GameAvailable { get; set; }

        /// <summary>
        /// Cumulative game-thread tick count since process start. Used by the
        /// test harness's WaitUntilGameReady wait strategy to verify the game
        /// loop is actually running before declaring the container ready.
        /// </summary>
        public long TickCount { get; set; }

        /// <summary>
        /// True when the game thread hasn't ticked recently (lastTickMs above
        /// <see cref="HealthFrozenThresholdMs"/>). Mirrors the existing
        /// <see cref="Status"/> "degraded" semantic in a boolean form so tests
        /// don't have to string-compare. Used by WaitUntilGameReady to reject
        /// a server whose HTTP listener is up but whose game loop has stalled.
        /// </summary>
        public bool IsFrozen { get; set; }
    }

    /// <summary>
    /// Performance stats for the game process.
    /// </summary>
    public class StatsResponse
    {
        /// <summary>Current frames per second.</summary>
        public double Fps { get; set; }

        /// <summary>Current game ticks per second.</summary>
        public double Tps { get; set; }

        /// <summary>Target TPS for the server.</summary>
        public int TargetTps { get; set; } = Env.ServerTps;

        /// <summary>Average tick duration in milliseconds (rolling 60-tick window).</summary>
        public double AvgTickMs { get; set; }

        /// <summary>Process memory usage in MB.</summary>
        public double MemoryMb { get; set; }

        /// <summary>Cumulative GC generation 0 collection count.</summary>
        public int GcGen0 { get; set; }

        /// <summary>Cumulative GC generation 1 collection count.</summary>
        public int GcGen1 { get; set; }

        /// <summary>Cumulative GC generation 2 collection count.</summary>
        public int GcGen2 { get; set; }

        /// <summary>Number of actions queued for the game thread.</summary>
        public int PendingActions { get; set; }

        /// <summary>Rolling average game thread wait time in milliseconds (60-sample window).</summary>
        public double GameThreadWaitMs { get; set; }
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

        /// <summary>
        /// Monotonic snapshot version (same field as <see cref="ServerStatus.Version"/>).
        /// Test clients pass this back as <c>?since=N</c> on /wait/farmhands to wait
        /// for a newer snapshot.
        /// </summary>
        public long Version { get; set; }
    }

    /// <summary>
    /// Current rendering status.
    /// </summary>
    public class RenderingStatus
    {
        /// <summary>Render rate: 0 = disabled, N &gt; 0 = enabled at N fps.</summary>
        public int Fps { get; set; }
    }

    /// <summary>
    /// Response from screenshot capture.
    /// </summary>
    public class ScreenshotResponse
    {
        /// <summary>Whether the capture succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Base64-encoded PNG image data.</summary>
        public string? Base64Png { get; set; }

        /// <summary>Image width in pixels.</summary>
        public int Width { get; set; }

        /// <summary>Image height in pixels.</summary>
        public int Height { get; set; }

        /// <summary>Error message if capture failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Response from a set-render-rate operation.
    /// </summary>
    public class RenderingSetResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Render rate after the operation: 0 = disabled, N &gt; 0 = at N fps.</summary>
        public int Fps { get; set; }

        /// <summary>Render rate before the operation.</summary>
        public int PreviousFps { get; set; }

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
    /// Response from clock speed adjustment.
    /// </summary>
    public class ClockSpeedResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>The applied multiplier.</summary>
        public double Multiplier { get; set; }

        /// <summary>The effective milliseconds per game minute after adjustment.</summary>
        public int EffectiveMs { get; set; }

        /// <summary>Error message if operation failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Response from role grant operation.
    /// </summary>
    public class RoleGrantResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>The player's unique multiplayer ID.</summary>
        public long PlayerId { get; set; }

        /// <summary>The player's display name.</summary>
        public string? PlayerName { get; set; }

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

        public FarmTypeSetting FarmType { get; set; } = FarmTypeSetting.Default;

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

        /// <summary>
        /// UniqueMultiplayerIDs of players who have an explicitly-placed cabin position
        /// recorded (via the cabin command). These cabins are exempt from the
        /// MoveToStack / strategy-migration sweep. Cleared when the farmhand is deleted.
        /// </summary>
        public List<long> SavedPositionPlayerIds { get; set; } = new();
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

        /// <summary>Whether the cabin is at a hidden off-map location.</summary>
        public bool IsHidden { get; set; }

        /// <summary>
        /// The cabin type indicating its purpose/management mode.
        /// Values: "Normal", "CabinStack", "FarmhouseStack", "Lobby"
        /// </summary>
        public string Type { get; set; } = "Normal";

        /// <summary>Owner's multiplayer ID (0 if unassigned).</summary>
        public long OwnerId { get; set; }

        /// <summary>Owner's display name (empty if unassigned).</summary>
        public string OwnerName { get; set; } = "";

        /// <summary>Whether the cabin has an assigned owner.</summary>
        public bool IsAssigned { get; set; }
    }

    /// <summary>
    /// Authentication/password protection status.
    /// </summary>
    public class AuthStatusResponse
    {
        /// <summary>Whether password protection is enabled on the server.</summary>
        public bool Enabled { get; set; }

        /// <summary>Number of players currently authenticated.</summary>
        public int AuthenticatedCount { get; set; }

        /// <summary>Number of players waiting to authenticate (in lobby).</summary>
        public int PendingCount { get; set; }

        /// <summary>Authentication timeout in seconds (0 = disabled).</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>Maximum failed login attempts before kick.</summary>
        public int MaxAttempts { get; set; }
    }

    /// <summary>
    /// Response from auth timeout update operation.
    /// </summary>
    public class AuthTimeoutResponse
    {
        /// <summary>Whether the operation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Current timeout in seconds after the operation.</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>Previous timeout in seconds before the change.</summary>
        public int PreviousTimeoutSeconds { get; set; }

        /// <summary>Error message if operation failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Request body for POST /newgame.
    /// </summary>
    public class NewGameRequest
    {
        /// <summary>Absent = use the server's configured farm type.</summary>
        public FarmTypeSetting? FarmType { get; set; }

        /// <summary>Farm name.</summary>
        public string? FarmName { get; set; }

        /// <summary>Number of starting cabins.</summary>
        public int? StartingCabins { get; set; }

        /// <summary>Cabin strategy: CabinStack, FarmhouseStack, or None.</summary>
        public string? CabinStrategy { get; set; }

        /// <summary>Maximum number of players.</summary>
        public int? MaxPlayers { get; set; }

        /// <summary>Whether to allow direct IP connections.</summary>
        public bool? AllowIpConnections { get; set; }

        /// <summary>Profit margin multiplier (1.0 = normal).</summary>
        public float? ProfitMargin { get; set; }

        /// <summary>Whether each player has a separate wallet.</summary>
        public bool? SeparateWallets { get; set; }
    }

    /// <summary>
    /// Response from POST /newgame.
    /// </summary>
    public class NewGameResponse
    {
        /// <summary>Whether the new game was created successfully.</summary>
        public bool Success { get; set; }

        /// <summary>Message describing the result.</summary>
        public string? Message { get; set; }

        /// <summary>Error message if the operation failed.</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Response from POST /reload.
    /// </summary>
    public class ReloadResponse
    {
        /// <summary>Whether the world reloaded successfully.</summary>
        public bool Success { get; set; }

        /// <summary>Message describing the result.</summary>
        public string? Message { get; set; }

        /// <summary>Error message if the operation failed.</summary>
        public string? Error { get; set; }
    }

    #endregion

    /// <summary>
    /// HTTP API service using HttpListener with NSwag OpenAPI support.
    /// </summary>
    public partial class ApiService : ModService
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private bool _isRunning;
        private string? _openApiSpec;

        private readonly ServerSettingsLoader _settings;
        private readonly PersistentOptions _persistentOptions;
        private readonly CabinManagerService _cabinManager;
        private readonly RoleService _roleService;
        private readonly PasswordProtectionService? _passwordProtectionService;

        // WebSocket client management
        private readonly List<WebSocket> _wsClients = new();
        private readonly object _wsLock = new();
        private Timer? _wsCleanupTimer;

        /// <summary>
        /// Queue of actions to execute on the main game thread.
        /// Used for game state modifications that would cause collection modification errors
        /// if executed from the async HTTP thread (e.g., removing buildings during draw).
        /// Each action includes a TaskCompletionSource to signal completion back to the caller.
        /// </summary>
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource<bool> Completion)> _pendingGameActions = new();

        /// <summary>
        /// Ticks timestamp of the last OnUpdateTicked call, used by /health to detect game thread stalls.
        /// Updated via Interlocked.Exchange so it can be read from HTTP threads without locking.
        /// </summary>
        private long _lastTickTimestamp;

        // TCS that /wait/health long-poll waiters await. Rotated on every
        // OnUnvalidatedUpdateTicked, after the _lastTickTimestamp write.
        // Distinct from _snapshotChanged: snapshot publishes are gated to 1 Hz
        // and skipped during Game1.newDay, both of which /health must remain
        // immune to (it reports game-thread liveness, not game-state freshness).
        // Rotation order matches PublishSnapshot at :1012-1016 — install fresh
        // TCS first, then signal the old one — to avoid the missed-wakeup race.
        private TaskCompletionSource _lastTickChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Cumulative game-thread tick counter. Used by /health (and the test-side
        /// WaitUntilGameReady wait strategy) to verify the game loop is actually
        /// running, not just that the HTTP listener accepts connections. Distinct
        /// from <see cref="_tickCount"/> (resets every second for TPS calculation).
        /// </summary>
        private long _totalTickCount;

        // ── Performance metrics for /stats endpoint ──
        private double _lastTickMs;
        private double _avgTickMs;
        private readonly Queue<double> _tickHistory = new();
        private const int TickHistorySize = 60;
        private readonly Stopwatch _tickStopwatch = new();
        private int _frameCount;
        private double _currentFps;
        private DateTime _lastFpsUpdate = DateTime.UtcNow;
        private int _tickCount;
        private double _currentTps;
        private DateTime _lastTpsUpdate = DateTime.UtcNow;

        // ── Game thread wait time tracking for /stats endpoint ──
        // Ring buffer + running sum for O(1) average with zero allocations.
        private double _avgGameThreadWaitMs;
        private const int GameThreadWaitBufferSize = 60;
        private readonly double[] _gameThreadWaitBuffer = new double[GameThreadWaitBufferSize];
        private int _gameThreadWaitIndex;
        private int _gameThreadWaitCount;
        private double _gameThreadWaitSum;
        private readonly ConcurrentQueue<double> _completedWaitTimes = new();

        // ── Game state snapshot for read-only endpoints ──
        // Populated once per second on the game thread (UnvalidatedUpdateTicked),
        // read by HTTP threads without blocking. This decouples /status, /players,
        // /farmhands, /cabins, and /auth from game thread availability, so they
        // respond instantly even during long ticks, saves, or thread starvation.
        private volatile GameStateSnapshot _snapshot = new();
        private DateTime _lastSnapshotUpdate;

        // Monotonic snapshot version. Incremented every time a new snapshot is
        // published; used by /wait/* long-poll endpoints to let clients block
        // until the snapshot they care about has changed.
        private long _snapshotVersion;

        // TCS that long-poll waiters await. Rotated atomically on every
        // snapshot publish: capture the old TCS, allocate a new one, publish
        // the new snapshot, then signal the old TCS so any waiters wake up
        // and re-evaluate. Lock-free read for waiters.
        private TaskCompletionSource _snapshotChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Previous snapshot's farmer uids (everyone except the host), used to
        // emit otherfarmers_changed events only on actual set diff. Keeping this
        // a HashSet makes the symmetric-diff cheap; bounded by MaxPlayers.
        private HashSet<long>? _previousOtherFarmerUids;

        // Previous snapshot's cabin ownership map, keyed by (tileX, tileY) so
        // cabin position (which is stable after placement) identifies the slot,
        // and (ownerId, ownerIsCustomized) captures the binding state. Used to
        // emit cabin_owner_changed events on ownership transitions only —
        // without this, post-mortem analysis has no deterministic signal for
        // "when did cabin.owner.UniqueMultiplayerID flip from 0 to X", which is
        // the exact race PasswordProtection / CabinManager integration can
        // expose under concurrent joins.
        private Dictionary<(int TileX, int TileY), (long OwnerId, bool IsCustomized)>? _previousCabinOwners;

        // Latches so we only emit newDay-skip and recurring errors once per
        // transition, not every tick.
        private bool _snapshotNewDayLatched;
        private string? _lastSnapshotErrorKey;
        private DateTime _lastSnapshotErrorEmittedAt;

        private sealed class GameStateSnapshot
        {
            // /status
            public bool IsOnline;
            public bool IsReady;
            public int PlayerCount;
            public int MaxPlayers = 4;
            public string FarmName = "";
            public int Day;
            public string Season = "";
            public int Year;
            public int TimeOfDay;
            public string FarmTypeKey = "";
            public bool IsPaused;

            // /players
            public List<PlayerInfo> Players = new();

            // /farmhands
            public List<FarmhandInfo> Farmhands = new();

            // /cabins
            public int CabinTotalCount;
            public int CabinAssignedCount;
            public int CabinAvailableCount;
            public List<CabinInfo> Cabins = new();

            // /auth
            public int AuthenticatedCount;
            public int PendingCount;

            public string CapturedAt = "";

            // Monotonic wall-clock capture time used to compute X-Snapshot-Age-Ms on
            // response. Kept alongside the existing ISO-string CapturedAt so readers
            // of the string field are unaffected. Nullable because snapshots built
            // for the offline branch (line ~779) are never served.
            public DateTime? CapturedAtUtc;

            /// <summary>
            /// Monotonic version number used by /wait/* long-poll endpoints to
            /// detect a changed snapshot since the client's last observed value.
            /// Stamped at publish time so a waiter can compare against an
            /// inbound `?since=N` parameter.
            /// </summary>
            public long Version;

            // ── Per-field change-time tracking ────────────────────────────────
            //
            // Each <c>FieldChangedAtUtc</c> is the wall-clock instant when that
            // field last *changed value* across consecutive snapshots. Carried
            // forward unchanged when the value is stable; reset to <c>now</c>
            // when the value differs from the previous published snapshot.
            // Computed once in <see cref="TakeGameStateSnapshot"/> by diffing
            // against the previously published snapshot.
            //
            // Used by <c>/wait/*</c> handlers to report the predicate-transition
            // time on the response (header <c>X-Predicate-Changed-At-Ms-Ago</c>),
            // which the test harness uses as producer-time on <c>wait_matched</c>.
            // The snapshot's <see cref="CapturedAtUtc"/> answers "when was this
            // data sampled?" — coarser than "when did this predicate become
            // true?" because snapshot publication is gated to 1Hz. The per-field
            // change-time is sub-tick-accurate because it's captured the moment
            // the diffing snapshot is built (which IS on a tick).
            //
            // For collection membership (Players, Farmhands), per-element
            // first-seen times are tracked on the elements themselves
            // (<see cref="PlayerInfoTracked.FirstSeenAtUtc"/> etc.) — see below.
            public DateTime IsReadyChangedAtUtc;
            public DateTime IsPausedChangedAtUtc;
            public DateTime PlayerCountChangedAtUtc;
            public DateTime FarmNameChangedAtUtc;
            public DateTime DayChangedAtUtc;
            public DateTime SeasonChangedAtUtc;
            public DateTime YearChangedAtUtc;
            public DateTime TimeOfDayChangedAtUtc;
            public DateTime IsOnlineChangedAtUtc;

            /// <summary>
            /// Per-player first-seen times, keyed by player ID. Parallel to
            /// <see cref="Players"/>. Kept off the wire-serialized
            /// <see cref="PlayerInfo"/> so the public <c>/players</c> response
            /// shape doesn't change.
            /// </summary>
            public Dictionary<long, DateTime> PlayerFirstSeenAtUtc = new();

            /// <summary>
            /// Per-farmhand-id tracking, keyed by farmhand ID. Stores when the
            /// farmhand was first seen, and when its <c>IsCustomized</c> flag
            /// last changed (predicates like
            /// <c>/wait/farmhands?hasFarmhand=X&amp;requireCustomized=true</c>
            /// need the latter).
            /// </summary>
            public Dictionary<long, FarmhandChangeTrack> FarmhandChangeTracks = new();
        }

        /// <summary>
        /// Per-farmhand change-time data, kept on the snapshot but not
        /// serialized to the wire. See
        /// <see cref="GameStateSnapshot.FarmhandChangeTracks"/>.
        /// </summary>
        private sealed class FarmhandChangeTrack
        {
            public DateTime FirstSeenAtUtc;
            public DateTime IsCustomizedChangedAtUtc;
            public bool IsCustomizedLastValue;
        }

        /// <summary>
        /// Default value of Game1.realMilliSecondsPerGameMinute, captured on first clock-speed call.
        /// </summary>
        private int _defaultRealMsPerGameMinute = -1;

        /// <summary>
        /// Event fired when a chat message is received from a WebSocket client (e.g., Discord bot).
        /// Parameters: author, message
        /// </summary>
        public event Action<string, string>? OnExternalChatMessage;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Whether API key authentication is enabled.
        /// </summary>
        private static readonly bool _authEnabled = !string.IsNullOrEmpty(Env.ApiKey);

        public ApiService(IModHelper helper, IMonitor monitor, ServerSettingsLoader settings, PersistentOptions persistentOptions, CabinManagerService cabinManager, RoleService roleService, PasswordProtectionService? passwordProtectionService = null) : base(helper, monitor)
        {
            _settings = settings;
            _persistentOptions = persistentOptions;
            _cabinManager = cabinManager;
            _roleService = roleService;
            _passwordProtectionService = passwordProtectionService;
        }

        public override void Entry()
        {
            if (!Env.ApiEnabled)
            {
                Monitor.Log("API service is disabled (API_ENABLED=false)", LogLevel.Info);
                return;
            }

            Helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            Helper.Events.Specialized.UnvalidatedUpdateTicking += OnUnvalidatedUpdateTicking;
            Helper.Events.Specialized.UnvalidatedUpdateTicked += OnUnvalidatedUpdateTicked;
            Helper.Events.Display.Rendered += OnRendered;

            // Server-side mirror of the test-client's HealthWatchdog. Polls
            // _lastTickTimestamp at 1 Hz on a dedicated thread so transient
            // stalls between /health polls don't hide. Fires exactly two
            // events per stall: _started and _recovered.
            StartGameThreadStallWatchdog();
        }

        private CancellationTokenSource? _stallWatchdogCts;

        /// <summary>Threshold above which a gap between ticks counts as a stall.</summary>
        private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);

        private void StartGameThreadStallWatchdog()
        {
            _stallWatchdogCts = new CancellationTokenSource();
            var ct = _stallWatchdogCts.Token;
            var thread = new System.Threading.Thread(() =>
            {
                var stalled = false;
                var stallStartUtc = default(DateTime);
                long stallStartLastTick = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var tickTicks = Interlocked.Read(ref _lastTickTimestamp);
                        if (tickTicks > 0)
                        {
                            var last = new DateTime(tickTicks, DateTimeKind.Utc);
                            var gap = DateTime.UtcNow - last;
                            if (!stalled && gap > StallThreshold)
                            {
                                stalled = true;
                                stallStartUtc = DateTime.UtcNow;
                                stallStartLastTick = tickTicks;
                                Diagnostics.ModEventLog.Emit("game_thread_stall_started", new
                                {
                                    lastTickMs = (long)gap.TotalMilliseconds
                                });
                            }
                            else if (stalled && gap <= StallThreshold)
                            {
                                var totalStallMs = (long)(DateTime.UtcNow - stallStartUtc).TotalMilliseconds;
                                stalled = false;
                                Diagnostics.ModEventLog.Emit("game_thread_stall_recovered", new
                                {
                                    durationMs = totalStallMs
                                });
                                // Suppress unused warning; stallStartLastTick is
                                // kept for future diagnostics expansion.
                                _ = stallStartLastTick;
                            }
                        }
                    }
                    catch
                    {
                        // Watchdog must never crash; swallow and keep polling.
                    }

                    try { System.Threading.Thread.Sleep(1000); } catch { break; }
                }
            })
            {
                IsBackground = true,
                Name = "ApiService.StallWatchdog"
            };
            thread.Start();
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // Process pending game actions on the main thread.
            // Intentionally in UpdateTicked (not UnvalidatedUpdateTicked). SMAPI
            // suppresses this during saving, which prevents mutating API callbacks
            // (POST /time, /newgame, DELETE /farmhands) from corrupting save data.
            // Read-only endpoints use the periodic snapshot instead (see TakeGameStateSnapshot).
            var actionsProcessed = false;
            while (_pendingGameActions.TryDequeue(out var item))
            {
                actionsProcessed = true;
                try
                {
                    item.Action();
                    item.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error executing pending game action: {ex}", LogLevel.Error);
                    item.Completion.TrySetException(ex);
                }
            }

            // Mutations (DELETE /farmhands, POST /time, etc.) change game state that
            // read-only endpoints serve from the snapshot. Refresh immediately so
            // subsequent reads see the updated state without waiting up to 1 second.
            if (actionsProcessed)
            {
                TakeGameStateSnapshot();
            }

            // Drain completed wait times and update rolling average (zero-allocation ring buffer)
            while (_completedWaitTimes.TryDequeue(out var waitMs))
            {
                if (_gameThreadWaitCount == GameThreadWaitBufferSize)
                    _gameThreadWaitSum -= _gameThreadWaitBuffer[_gameThreadWaitIndex];
                else
                    _gameThreadWaitCount++;

                _gameThreadWaitBuffer[_gameThreadWaitIndex] = waitMs;
                _gameThreadWaitSum += waitMs;
                _gameThreadWaitIndex = (_gameThreadWaitIndex + 1) % GameThreadWaitBufferSize;

                Volatile.Write(ref _avgGameThreadWaitMs, _gameThreadWaitSum / _gameThreadWaitCount);
            }
        }

        private void OnUnvalidatedUpdateTicking(object? sender, UnvalidatedUpdateTickingEventArgs e)
        {
            _tickStopwatch.Restart();
        }

        private void OnUnvalidatedUpdateTicked(object? sender, UnvalidatedUpdateTickedEventArgs e)
        {
            // Record that the game thread is alive. Read by /health without RunOnGameThreadAsync.
            // Uses UnvalidatedUpdateTicked so /health stays accurate during saves.
            Interlocked.Exchange(ref _lastTickTimestamp, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _totalTickCount);

            // Wake any /wait/health waiters. Install a fresh TCS for future
            // waiters BEFORE signaling the old one — same ordering rule as
            // PublishSnapshot. RunContinuationsAsynchronously keeps continuations
            // off the game thread.
            var oldTickTcs = Interlocked.Exchange(
                ref _lastTickChanged,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            oldTickTcs.TrySetResult();

            // Record tick timing for /stats
            _tickStopwatch.Stop();
            var tickMs = _tickStopwatch.Elapsed.TotalMilliseconds;
            _tickHistory.Enqueue(tickMs);
            if (_tickHistory.Count > TickHistorySize) _tickHistory.Dequeue();
            Volatile.Write(ref _lastTickMs, tickMs);
            Volatile.Write(ref _avgTickMs, _tickHistory.Average());

            // TPS tracking (same pattern as FPS but for game ticks)
            _tickCount++;
            var tpsNow = DateTime.UtcNow;
            var tpsElapsed = (tpsNow - _lastTpsUpdate).TotalSeconds;
            if (tpsElapsed >= 1.0)
            {
                Volatile.Write(ref _currentTps, _tickCount / tpsElapsed);
                _tickCount = 0;
                _lastTpsUpdate = tpsNow;
            }

            // Refresh game state snapshot once per second for read-only endpoints.
            // Uses wall-clock time (not tick count) because TPS can drop well below 60
            // during startup or resource contention, making IsMultipleOf(60) unreliable.
            // Skip during day transitions: _newDayAfterFade runs saveFarmhands() on
            // a thread pool thread, which mutates farmhandData and cabin state. Reading
            // those collections here would risk corrupted state. The pre-transition
            // snapshot remains valid until UpdateTicked resumes and refreshes it.
            var now = DateTime.UtcNow;
            if ((now - _lastSnapshotUpdate).TotalSeconds >= 1.0)
            {
                if (Game1.newDay)
                {
                    // Latch — one event per newDay streak, not per tick. Reset
                    // by TakeGameStateSnapshot's success path once newDay ends.
                    if (!_snapshotNewDayLatched)
                    {
                        _snapshotNewDayLatched = true;
                        Diagnostics.ModEventLog.Emit("snapshot_skipped_newday");
                    }
                }
                else
                {
                    _lastSnapshotUpdate = now;
                    TakeGameStateSnapshot();
                }
            }
        }

        /// <summary>
        /// Publishes a new snapshot atomically: stamps the next version number,
        /// rotates the snapshot-changed TCS so any /wait/* long-poll waiters
        /// wake up, and assigns to <see cref="_snapshot"/>. Lock-free reads for
        /// waiters; concurrent publishers are serialized by the call site
        /// (<see cref="OnUnvalidatedUpdateTicked"/> runs on the game thread).
        /// </summary>
        private void PublishSnapshot(GameStateSnapshot snap)
        {
            snap.Version = Interlocked.Increment(ref _snapshotVersion);
            // Rotate the TCS: install a fresh one for future waiters BEFORE
            // signaling the old one. Otherwise a fast waiter that completes
            // its post-resume snapshot read could observe the new snapshot
            // version, return early, then re-await — only to await the
            // already-completed TCS we hadn't yet rotated.
            var oldTcs = Interlocked.Exchange(
                ref _snapshotChanged,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _snapshot = snap;
            oldTcs.TrySetResult();
        }

        /// <summary>
        /// Builds a fresh <see cref="GameStateSnapshot"/> from current game state and
        /// publishes it atomically via <see cref="_snapshot"/>. Called from two places:
        /// <list type="bullet">
        /// <item><see cref="OnUnvalidatedUpdateTicked"/> (periodic, 1/sec) -- fires even during
        /// game thread stalls but skipped during day transitions to avoid concurrent access
        /// with the save task's farmhand/cabin mutations.</item>
        /// <item><see cref="OnUpdateTicked"/> (after mutations) -- ensures changes from
        /// mutating endpoints are immediately visible to subsequent reads.</item>
        /// </list>
        /// HTTP threads read the published snapshot without blocking.
        /// </summary>
        private void TakeGameStateSnapshot()
        {
            try
            {
                var capturedAt = DateTime.UtcNow;
                // Read the previously-published snapshot under volatile semantics;
                // it's the diff baseline for per-field change-time tracking.
                var prevSnap = _snapshot;
                var snap = new GameStateSnapshot
                {
                    IsOnline = Game1.IsServer && Game1.gameMode == 3,
                    CapturedAt = capturedAt.ToString("o"),
                    CapturedAtUtc = capturedAt
                };

                // IsOnline always tracked (its change is observable independently
                // of the early-return branch below).
                snap.IsOnlineChangedAtUtc = prevSnap.IsOnline == snap.IsOnline
                    ? (prevSnap.IsOnlineChangedAtUtc == default ? capturedAt : prevSnap.IsOnlineChangedAtUtc)
                    : capturedAt;

                if (!snap.IsOnline)
                {
                    PublishSnapshot(snap);
                    return;
                }

                // /status fields
                var onlineFarmers = Game1.getOnlineFarmers().ToList();
                snap.PlayerCount = onlineFarmers.Count(f => f.UniqueMultiplayerID != Game1.player?.UniqueMultiplayerID);
                snap.MaxPlayers = Game1.netWorldState.Value?.CurrentPlayerLimit ?? 4;
                snap.IsReady = Game1.server?.isGameAvailable() ?? false;
                snap.FarmName = Game1.player?.farmName.Value ?? "";
                snap.Day = Game1.dayOfMonth;
                snap.Season = Game1.currentSeason ?? "";
                snap.Year = Game1.year;
                snap.TimeOfDay = Game1.timeOfDay;
                snap.FarmTypeKey = Game1.GetFarmTypeKey();
                snap.IsPaused = Game1.netWorldState.Value?.IsPaused ?? false;

                // Per-field change-time tracking. The local helper compares the
                // previous-snapshot's value to the new value: same → carry over
                // the prior change-time (or stamp `capturedAt` if the prior had
                // none); different → stamp `capturedAt`. This makes the
                // change-time sharp to whichever tick the field actually flipped,
                // not gated by the 1Hz snapshot publish cadence.
                DateTime CarryOrStamp<T>(T newVal, T prevVal, DateTime prevChangedAt)
                    => EqualityComparer<T>.Default.Equals(newVal, prevVal)
                        ? (prevChangedAt == default ? capturedAt : prevChangedAt)
                        : capturedAt;

                snap.IsReadyChangedAtUtc = CarryOrStamp(snap.IsReady, prevSnap.IsReady, prevSnap.IsReadyChangedAtUtc);
                snap.IsPausedChangedAtUtc = CarryOrStamp(snap.IsPaused, prevSnap.IsPaused, prevSnap.IsPausedChangedAtUtc);
                snap.PlayerCountChangedAtUtc = CarryOrStamp(snap.PlayerCount, prevSnap.PlayerCount, prevSnap.PlayerCountChangedAtUtc);
                snap.FarmNameChangedAtUtc = CarryOrStamp(snap.FarmName, prevSnap.FarmName, prevSnap.FarmNameChangedAtUtc);
                snap.DayChangedAtUtc = CarryOrStamp(snap.Day, prevSnap.Day, prevSnap.DayChangedAtUtc);
                snap.SeasonChangedAtUtc = CarryOrStamp(snap.Season, prevSnap.Season, prevSnap.SeasonChangedAtUtc);
                snap.YearChangedAtUtc = CarryOrStamp(snap.Year, prevSnap.Year, prevSnap.YearChangedAtUtc);
                snap.TimeOfDayChangedAtUtc = CarryOrStamp(snap.TimeOfDay, prevSnap.TimeOfDay, prevSnap.TimeOfDayChangedAtUtc);

                // /players
                foreach (var farmer in onlineFarmers)
                {
                    if (farmer.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
                        continue;

                    snap.Players.Add(new PlayerInfo
                    {
                        Id = farmer.UniqueMultiplayerID,
                        Name = farmer.Name ?? farmer.displayName ?? "Unknown",
                        IsOnline = true
                    });
                }

                // Per-player first-seen times. Carry over from the prior
                // snapshot's tracking dict for IDs still present; new IDs get
                // `capturedAt`. Stale IDs (player left) are dropped, so a
                // subsequent rejoin counts as a fresh first-seen.
                foreach (var p in snap.Players)
                {
                    snap.PlayerFirstSeenAtUtc[p.Id] =
                        prevSnap.PlayerFirstSeenAtUtc.TryGetValue(p.Id, out var existing)
                            ? existing
                            : capturedAt;
                }

                // /farmhands
                foreach (var farmer in Game1.getAllFarmhands())
                {
                    try
                    {
                        snap.Farmhands.Add(new FarmhandInfo
                        {
                            Id = farmer.UniqueMultiplayerID,
                            Name = farmer.Name ?? "",
                            IsCustomized = farmer.isCustomized.Value
                        });
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"[Snapshot] Error reading farmhand {farmer.UniqueMultiplayerID}: {ex.Message}", LogLevel.Debug);
                    }
                }

                // Per-farmhand change-tracking. Carry forward `FirstSeenAtUtc`
                // and compare `IsCustomized` to detect customization transitions.
                foreach (var f in snap.Farmhands)
                {
                    if (prevSnap.FarmhandChangeTracks.TryGetValue(f.Id, out var prevTrack))
                    {
                        snap.FarmhandChangeTracks[f.Id] = new FarmhandChangeTrack
                        {
                            FirstSeenAtUtc = prevTrack.FirstSeenAtUtc,
                            IsCustomizedChangedAtUtc = f.IsCustomized == prevTrack.IsCustomizedLastValue
                                ? prevTrack.IsCustomizedChangedAtUtc
                                : capturedAt,
                            IsCustomizedLastValue = f.IsCustomized
                        };
                    }
                    else
                    {
                        snap.FarmhandChangeTracks[f.Id] = new FarmhandChangeTrack
                        {
                            FirstSeenAtUtc = capturedAt,
                            IsCustomizedChangedAtUtc = capturedAt,
                            IsCustomizedLastValue = f.IsCustomized
                        };
                    }
                }

                // /cabins
                SnapshotCabins(snap);

                // /auth
                if (_passwordProtectionService?.IsEnabled == true)
                {
                    foreach (var farmer in onlineFarmers)
                    {
                        if (farmer.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
                            continue;

                        if (_passwordProtectionService.IsPlayerAuthenticated(farmer.UniqueMultiplayerID))
                            snap.AuthenticatedCount++;
                        else
                            snap.PendingCount++;
                    }
                }

                PublishSnapshot(snap);

                // Reset the newDay skip latch once we've completed a real
                // snapshot after the transition.
                _snapshotNewDayLatched = false;

                // Emit otherfarmers_changed on set diff only. Cheap set-diff
                // using HashSet<long>; bounded by MaxPlayers.
                var currentUids = new HashSet<long>();
                foreach (var p in snap.Players) currentUids.Add(p.Id);

                if (_previousOtherFarmerUids == null)
                {
                    // First snapshot after boot. If any players are visible,
                    // record them as additions; otherwise stay silent.
                    if (currentUids.Count > 0)
                    {
                        Diagnostics.ModEventLog.Emit("otherfarmers_changed", new
                        {
                            added = currentUids.ToArray(),
                            removed = System.Array.Empty<long>(),
                            total = currentUids.Count
                        });
                    }
                }
                else if (!currentUids.SetEquals(_previousOtherFarmerUids))
                {
                    var added = currentUids.Except(_previousOtherFarmerUids).ToArray();
                    var removed = _previousOtherFarmerUids.Except(currentUids).ToArray();
                    Diagnostics.ModEventLog.Emit("otherfarmers_changed", new
                    {
                        added,
                        removed,
                        total = currentUids.Count
                    });
                }
                _previousOtherFarmerUids = currentUids;

                // Cabin-ownership diff. Only emit on actual change. Captures
                // the (oldOwnerId → newOwnerId) transition plus names so the
                // timeline shows exactly when a cabin's owner binding flipped.
                var currentCabinOwners = new Dictionary<(int, int), (long, bool)>();
                var currentCabinMeta = new Dictionary<(int, int), (string OwnerName, string Type)>();
                foreach (var c in snap.Cabins)
                {
                    var key = (c.TileX, c.TileY);
                    currentCabinOwners[key] = (c.OwnerId, c.IsAssigned);
                    currentCabinMeta[key] = (c.OwnerName, c.Type);
                }

                if (_previousCabinOwners != null)
                {
                    foreach (var kv in currentCabinOwners)
                    {
                        _previousCabinOwners.TryGetValue(kv.Key, out var prev);
                        if (prev == kv.Value) continue;

                        currentCabinMeta.TryGetValue(kv.Key, out var meta);
                        Diagnostics.ModEventLog.Emit("cabin_owner_changed", new
                        {
                            tileX = kv.Key.Item1,
                            tileY = kv.Key.Item2,
                            oldOwnerId = prev.Item1,
                            newOwnerId = kv.Value.Item1,
                            newOwnerName = meta.OwnerName,
                            newOwnerIsCustomized = kv.Value.Item2,
                            cabinType = meta.Type
                        });
                    }
                }
                _previousCabinOwners = currentCabinOwners;
            }
            catch (Exception ex)
            {
                // Never crash the game thread. The stale snapshot remains valid.
                // Promote to Warn and emit a structured event so silent snapshot
                // breakage can be detected — the old Debug-level log masked
                // exactly this class of bug. Throttle by exception-type+top-frame
                // so a recurring error doesn't flood the log.
                var topFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
                var key = $"{ex.GetType().Name}|{topFrame}";
                var now = DateTime.UtcNow;
                if (_lastSnapshotErrorKey != key
                    || (now - _lastSnapshotErrorEmittedAt).TotalSeconds >= 1.0)
                {
                    _lastSnapshotErrorKey = key;
                    _lastSnapshotErrorEmittedAt = now;
                    Monitor.Log($"[Snapshot] Error building game state snapshot: {ex.Message}", LogLevel.Warn);
                    Diagnostics.ModEventLog.Emit("snapshot_error", new
                    {
                        exception = ex.GetType().Name,
                        message = ex.Message,
                        stackTop = topFrame
                    });
                }
            }
        }

        /// <summary>
        /// Populates cabin data in the snapshot. Reads cabin ownership from farmhandReference,
        /// which is set correctly during connection by PasswordProtectionService.EnsureRealCabinAssignment.
        ///
        /// Note: <c>IsAssigned</c> requires <c>cabin.owner.isCustomized.Value == true</c>, so
        /// it stays false for several seconds after a fresh-customized join while the
        /// customize XML round-trips. Test code that polls for ownership of a brand-new
        /// player should match by <c>OwnerId</c> alone, not by <c>IsAssigned</c>.
        /// </summary>
        private void SnapshotCabins(GameStateSnapshot snap)
        {
            try
            {
                var farm = Game1.getFarm();
                if (farm == null) return;

                var cabinBuildings = farm.buildings.Where(b => b.isCabin).ToList();
                var strategy = _persistentOptions.Data.CabinStrategy;

                foreach (var building in cabinBuildings)
                {
                    try
                    {
                        var cabin = building.GetIndoors<Cabin>();
                        var ownerId = cabin?.owner?.UniqueMultiplayerID ?? 0;
                        var ownerName = cabin?.owner?.Name ?? "";
                        var isAssigned = ownerId != 0 && cabin?.owner?.isCustomized.Value == true;

                        var role = building.GetCabinRole();
                        string cabinType = role switch
                        {
                            CabinRole.SharedLobby => "SharedLobby",
                            CabinRole.IndividualLobby => "IndividualLobby",
                            CabinRole.Editing => "Editing",
                            _ when strategy == CabinStrategy.FarmhouseStack => "FarmhouseStack",
                            _ when strategy == CabinStrategy.CabinStack => "CabinStack",
                            _ => "Normal"
                        };
                        var isHidden = role != CabinRole.Player || building.IsInHiddenStack();

                        snap.Cabins.Add(new CabinInfo
                        {
                            TileX = building.tileX.Value,
                            TileY = building.tileY.Value,
                            IsHidden = isHidden,
                            Type = cabinType,
                            OwnerId = ownerId,
                            OwnerName = ownerName,
                            IsAssigned = isAssigned
                        });
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"[Snapshot] Error reading cabin at ({building.tileX.Value},{building.tileY.Value}): {ex.Message}", LogLevel.Debug);
                    }
                }

                snap.CabinTotalCount = snap.Cabins.Count;
                snap.CabinAssignedCount = snap.Cabins.Count(c => c.IsAssigned);
                snap.CabinAvailableCount = snap.CabinTotalCount - snap.CabinAssignedCount;
            }
            catch (Exception ex)
            {
                Monitor.Log($"[Snapshot] Error reading cabins: {ex.Message}", LogLevel.Debug);
            }
        }

        private void OnRendered(object? sender, RenderedEventArgs e)
        {
            _frameCount++;
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastFpsUpdate).TotalSeconds;
            if (elapsed >= 1.0)
            {
                Volatile.Write(ref _currentFps, _frameCount / elapsed);
                _frameCount = 0;
                _lastFpsUpdate = now;
            }

        }

        /// <summary>
        /// Queues an action to run on the main game thread and waits for it to complete.
        /// Use this for game state modifications from async HTTP handlers.
        /// Captures the ambient ModRequestContext.RequestId at queue time and
        /// re-binds it on the game-thread side so structured events emitted
        /// inside <paramref name="action"/> carry the triggering request id.
        /// AsyncLocal does not flow across the external pump boundary.
        /// </summary>
        private async Task RunOnGameThreadAsync(Action action, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var capturedRequestId = Diagnostics.ModRequestContext.RequestId;
            Action wrapped = () =>
            {
                using var _correlationScope = Diagnostics.ModRequestContext.Bind(capturedRequestId);
                action();
            };
            _pendingGameActions.Enqueue((wrapped, tcs));

            using var cts = new CancellationTokenSource(timeoutMs);
            using var registration = cts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                await tcs.Task.ConfigureAwait(false);
                sw.Stop();
                _completedWaitTimes.Enqueue(sw.Elapsed.TotalMilliseconds);
            }
            catch (TaskCanceledException)
            {
                // Timeout: don't record (not representative of normal wait times)
                throw;
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            StartServer();
        }

        /// <summary>
        /// Validates the API key from the Authorization header.
        /// Expects format: "Bearer &lt;api-key&gt;"
        /// Returns true if authentication passes (no key required or valid key provided).
        /// </summary>
        private bool ValidateApiKey(HttpListenerRequest request)
        {
            if (!_authEnabled) return true;

            var authHeader = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(authHeader)) return false;

            // Expect "Bearer <token>" format
            if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;

            var providedKey = authHeader.Substring(7).Trim();
            return !string.IsNullOrEmpty(providedKey) && providedKey == Env.ApiKey;
        }

        /// <summary>
        /// Writes a 401 Unauthorized response.
        /// </summary>
        private static async Task WriteUnauthorizedAsync(HttpListenerResponse response)
        {
            response.StatusCode = 401;
            response.Headers.Add("WWW-Authenticate", "Bearer");
            await WriteJsonAsync(response, new { error = "Unauthorized. Provide a valid Authorization header: Bearer <api-key>" });
        }

        /// <summary>
        /// Writes the standard 404 Not Found response. The single source of the 404 body so
        /// every dispatcher arm (and the production /test/* gate) returns byte-identical output.
        /// The requested path is echoed to match the test-client mirror's 404 shape
        /// (tests/test-client/HttpServer/TestApiServer.cs).
        /// </summary>
        private static async Task WriteNotFoundAsync(HttpListenerResponse response, string path)
        {
            response.StatusCode = 404;
            await WriteJsonAsync(response, new { error = "Not found", path });
        }

        private void StartServer()
        {
            if (_isRunning) return;

            try
            {
                // Generate OpenAPI spec. Test-only endpoints are spec-visible iff they are
                // runtime-reachable (Env.IsTest) — the same gate the dispatcher uses, so the
                // published contract and the routing cannot drift.
                // SEAM: if INCLUDE_TEST_ENDPOINTS is introduced, wrap this predicate's IsTestPath/Env.IsTest use.
                var document = OpenApiGenerator.Generate(
                    typeof(ApiService),
                    "Stardew Dedicated Server API",
                    "v1",
                    "HTTP API for monitoring and interacting with the Stardew Valley dedicated server",
                    includeMethod: m =>
                    {
                        var ep = m.GetCustomAttribute<ApiEndpointAttribute>();
                        return ep == null || !IsTestPath(ep.Path) || Env.IsTest;
                    }
                );
                _openApiSpec = document.ToJson();

                // Create and configure listener
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{Env.ApiPort}/");
                _listener.Start();

                // Start processing requests
                _cts = new CancellationTokenSource();
                _serverTask = Task.Run(() => ProcessRequestsAsync(_cts.Token));

                // Start WebSocket cleanup timer (every 30 seconds)
                _wsCleanupTimer = new Timer(_ => CleanupDeadWebSocketClients(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

                _isRunning = true;

                Monitor.Log($"API server listening on port {Env.ApiPort} (docs: http://localhost:{Env.ApiPort}/docs)", LogLevel.Info);
                if (_authEnabled)
                {
                    Monitor.Log("API authentication enabled - all endpoints require Authorization header", LogLevel.Info);
                }
                else
                {
                    Monitor.Log("***********************************************************************", LogLevel.Warn);
                    Monitor.Log("*                                                                     *", LogLevel.Warn);
                    Monitor.Log("*    WARNING: API authentication is disabled!                         *", LogLevel.Warn);
                    Monitor.Log("*    All endpoints are publicly accessible.                           *", LogLevel.Warn);
                    Monitor.Log("*    Set the API_KEY environment variable for production use.         *", LogLevel.Warn);
                    Monitor.Log("*                                                                     *", LogLevel.Warn);
                    Monitor.Log("***********************************************************************", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                // Clean up partially-initialized listener to prevent background accept
                // callbacks from firing on thread pool threads (which would trigger
                // FAIL_FAST via AppDomain.UnhandledException).
                try { _listener?.Close(); }
                catch (Exception closeEx)
                {
                    Diagnostics.ModEventLog.Emit("exception_swallowed", new
                    {
                        location = "ApiService.StartServer.listenerClose",
                        exceptionType = closeEx.GetType().Name,
                        message = closeEx.Message
                    });
                }
                _listener = null;
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

            // Extract the correlation header sent by the test harness. This
            // flows to mod-side structured events so a single requestId
            // stitches logs across containers. Safe to be null in production
            // (the mod runs outside tests too).
            var requestId = request.Headers["X-Request-Id"];

            // Echo the same id on the response so the client-side
            // TracingHandler can confirm the round-trip and the mod's view
            // of the id matches the caller's. Header writes must happen
            // before the body is streamed — set it first.
            if (!string.IsNullOrEmpty(requestId))
            {
                try { response.Headers["X-Request-Id"] = requestId; }
                catch (Exception ex) { Monitor.Log($"[API] Failed to echo X-Request-Id: {ex.Message}", LogLevel.Debug); }
            }

            // Snapshot age — tells the test harness how stale the data it's about
            // to receive actually is. Critical for diagnosing "I polled /players 10x
            // in 10s and the uid never appeared": without this the test cannot
            // distinguish "the snapshot was fresh and the uid really wasn't there"
            // from "the snapshot was 9.8s stale". Computed from CapturedAtUtc so
            // only real online snapshots carry a value (offline-branch snapshots
            // leave it null and the header is omitted).
            try
            {
                var snapForHeader = _snapshot;
                if (snapForHeader.CapturedAtUtc is DateTime cap)
                {
                    var ageMs = (long)(DateTime.UtcNow - cap).TotalMilliseconds;
                    if (ageMs < 0) ageMs = 0; // clock skew paranoia
                    response.Headers["X-Snapshot-Age-Ms"] = ageMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) { Monitor.Log($"[API] Failed to emit X-Snapshot-Age-Ms: {ex.Message}", LogLevel.Debug); }

            using var _correlationScope = Diagnostics.ModRequestContext.Bind(requestId);

            // Per-request stopwatch fed into the http_served event in the
            // finally block. Captured here so early-return paths (WebSocket
            // upgrade, 404/405, auth reject) are still measured.
            var _httpServedStopwatch = System.Diagnostics.Stopwatch.StartNew();
            string _servedPath = request.Url?.AbsolutePath ?? "/";
            string _servedMethod = request.HttpMethod;

            try
            {
                var path = request.Url?.AbsolutePath ?? "/";
                var method = request.HttpMethod;

                // Handle WebSocket upgrade
                if (path == "/ws" && request.IsWebSocketRequest)
                {
                    await HandleWebSocketAsync(context);
                    return;
                }

                // Public endpoints (no auth required).
                // /diagnostics/state is public because it is a test-harness
                // failure-diagnosis endpoint: callers are the test containers
                // inside the Testcontainers network, not external Internet
                // clients. Exposing it unauthenticated lets a timed-out poll
                // grab ground-truth state without having to pass the API key.
                var isPublicEndpoint = path == "/health" || path == "/wait/health" || path == "/stats" || path == "/docs" || path == "/swagger/v1/swagger.json" || path == "/diagnostics/state";

                // Validate API key for protected endpoints
                if (!isPublicEndpoint && !ValidateApiKey(request))
                {
                    await WriteUnauthorizedAsync(response);
                    return;
                }

                // Test-only routes — a third early-return gate, mirroring the WebSocket and auth
                // gates above. Placed AFTER auth so production behaves identically to an unknown
                // route: an unauthenticated caller already got 401 (same as any non-public path),
                // and an authenticated caller gets the same 404 a missing route gets.
                // DO NOT add /test/* to isPublicEndpoint — that would both unauthenticate these
                // routes AND leak their existence (404 vs 401) in production.
                // SEAM: if INCLUDE_TEST_ENDPOINTS is introduced, wrap this block.
                if (IsTestPath(path))
                {
                    if (!Env.IsTest)
                    {
                        await WriteNotFoundAsync(response, path);
                        return;
                    }
                    await DispatchTestEndpointAsync(method, path, request, response);
                    return;
                }

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
                            await ProfileFarmhandsAsync(response);
                            break;
                        case "/health":
                            await WriteJsonAsync(response, HandleGetHealth());
                            break;
                        case "/wait/status":
                            await HandleWaitStatusAsync(request, response, requestId);
                            break;
                        case "/wait/players":
                            await HandleWaitPlayersAsync(request, response, requestId);
                            break;
                        case "/wait/health":
                            await HandleWaitHealthAsync(request, response, requestId);
                            break;
                        case "/wait/farmhands":
                            await HandleWaitFarmhandsAsync(request, response, requestId);
                            break;
                        case "/diagnostics/state":
                            await WriteJsonAsync(response, await HandleGetDiagnosticsStateAsync());
                            break;
                        case "/diagnostics/handler-timing":
                            await WriteJsonAsync(response, BuildHandlerTimingReport());
                            break;
                        case "/stats":
                            await WriteJsonAsync(response, HandleGetStats());
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
                        case "/screenshot":
                            await WriteJsonAsync(response, await HandleGetScreenshotAsync());
                            break;
                        case "/auth":
                            await WriteJsonAsync(response, HandleGetAuthStatus());
                            break;
                        case "/swagger/v1/swagger.json":
                            await WriteJsonRawAsync(response, _openApiSpec ?? "{}");
                            break;
                        case "/docs":
                            await WriteHtmlAsync(response, GetScalarHtml());
                            break;
                        default:
                            await WriteNotFoundAsync(response, path);
                            break;
                    }
                }
                else if (method == "POST")
                {
                    switch (path)
                    {
                        case "/rendering":
                            var fpsParam = request.QueryString["fps"];
                            await WriteJsonAsync(response, await HandlePostRenderingAsync(fpsParam));
                            break;
                        case "/time":
                            var timeParam = request.QueryString["value"];
                            await WriteJsonAsync(response, await HandlePostTimeAsync(timeParam));
                            break;
                        case "/clock-speed":
                            var multiplierParam = request.QueryString["multiplier"];
                            await WriteJsonAsync(response, await HandlePostClockSpeedAsync(multiplierParam));
                            break;
                        case "/roles/admin":
                            var adminNameParam = request.QueryString["name"];
                            var adminIdParam = request.QueryString["playerId"];
                            await WriteJsonAsync(response, await HandlePostGrantAdminAsync(adminNameParam, adminIdParam));
                            break;
                        case "/auth/timeout":
                            var timeoutParam = request.QueryString["value"];
                            await WriteJsonAsync(response, HandlePostAuthTimeout(timeoutParam));
                            break;
                        case "/newgame":
                            await HandlePostNewGameAsync(request, response);
                            break;
                        case "/reload":
                            await HandlePostReloadAsync(response);
                            break;
                        default:
                            await WriteNotFoundAsync(response, path);
                            break;
                    }
                }
                else if (method == "DELETE")
                {
                    switch (path)
                    {
                        case "/farmhands":
                            var nameParam = request.QueryString["name"];
                            var farmhandIdParam = request.QueryString["playerId"];
                            await WriteJsonAsync(response, await HandleDeleteFarmhandAsync(nameParam, farmhandIdParam));
                            break;
                        default:
                            await WriteNotFoundAsync(response, path);
                            break;
                    }
                }
                else
                {
                    response.StatusCode = 405;
                    await WriteJsonAsync(response, new { error = "Method not allowed" });
                }
            }
            catch (TaskCanceledException)
            {
                // Game thread busy (RunOnGameThreadAsync timed out). Transient, not an error.
                Monitor.Log($"Request to {request.Url?.AbsolutePath} timed out waiting for game thread", LogLevel.Debug);
                try
                {
                    response.StatusCode = 503;
                    await WriteJsonAsync(response, new
                    {
                        error = "Game thread is blocked (likely a day transition or save sync) and cannot process requests right now. Retry after a few seconds.",
                        retry = true
                    });
                }
                catch (Exception writeEx)
                {
                    Monitor.Log($"Failed to write error response: {writeEx}", LogLevel.Warn);
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
                // Server-side mirror of the client's http_request event. Lets
                // test-side analysis compare "time the request spent on the
                // server" against "total time observed by the client" — a
                // large delta fingerprints network/container latency.
                try
                {
                    _httpServedStopwatch.Stop();
                    int statusCode;
                    try { statusCode = response.StatusCode; }
                    catch { statusCode = 0; } // response already closed/disposed
                    Diagnostics.ModEventLog.Emit("http_served", new
                    {
                        method = _servedMethod,
                        path = _servedPath,
                        status = statusCode,
                        durationMs = _httpServedStopwatch.ElapsedMilliseconds
                    });
                }
                catch { /* never let instrumentation fail a request close */ }

                // TODO: Don't close/write to the response for WebSocket requests (here and above)
                //       It's already been consumed/disposed by the WebSocket upgrade handshake.
                try { response.Close(); }
                catch (Exception closeEx)
                {
                    Monitor.Log($"Failed to close response: {closeEx}", LogLevel.Debug);
                }
            }
        }

        #region WebSocket

        /// <summary>
        /// Broadcasts a chat message to all connected WebSocket clients.
        /// </summary>
        public void BroadcastChatMessage(string playerName, string message)
        {
            var wsMessage = new WebSocketMessage
            {
                Type = "chat",
                Payload = JObject.FromObject(new ChatEventPayload
                {
                    PlayerName = playerName,
                    Message = message,
                    Timestamp = DateTime.UtcNow.ToString("o")
                })
            };

            BroadcastToAllClients(wsMessage);
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context)
        {
            WebSocket? ws = null;
            var isAuthenticated = false;
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                ws = wsContext.WebSocket;

                Monitor.Log("[API] WebSocket client connected, awaiting authentication", LogLevel.Debug);

                // If auth is disabled, auto-authenticate and add to clients
                if (!_authEnabled)
                {
                    isAuthenticated = true;
                    lock (_wsLock) { _wsClients.Add(ws); }
                    Monitor.Log("[API] WebSocket client authenticated (auth disabled)", LogLevel.Debug);
                }

                isAuthenticated = await ProcessWebSocketAsync(ws, isAuthenticated);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[API] WebSocket error: {ex.Message}", LogLevel.Warn);
            }
            finally
            {
                if (ws != null)
                {
                    if (isAuthenticated)
                    {
                        lock (_wsLock) { _wsClients.Remove(ws); }
                    }
                    try { ws.Dispose(); }
                    catch (Exception disposeEx)
                    {
                        Diagnostics.ModEventLog.Emit("exception_swallowed", new
                        {
                            location = "ApiService.HandleWebSocketAsync.dispose",
                            exceptionType = disposeEx.GetType().Name,
                            message = disposeEx.Message
                        });
                    }
                    Monitor.Log("[API] WebSocket client disconnected", LogLevel.Debug);
                }
            }
        }

        private async Task<bool> ProcessWebSocketAsync(WebSocket ws, bool isAuthenticated)
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();
            const int maxMessageSize = 16384; // 16KB max message size
            const int authTimeoutMs = 10000; // 10 seconds to authenticate

            // If auth is required, wait for auth message with timeout
            if (_authEnabled && !isAuthenticated)
            {
                using var authCts = new CancellationTokenSource(authTimeoutMs);
                try
                {
                    var authResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), authCts.Token);
                    if (authResult.MessageType == WebSocketMessageType.Text && authResult.EndOfMessage)
                    {
                        var authJson = Encoding.UTF8.GetString(buffer, 0, authResult.Count);
                        var authMsg = JsonConvert.DeserializeObject<WebSocketMessage>(authJson);

                        if (authMsg?.Type == "auth" && authMsg.Payload?["token"]?.ToString() == Env.ApiKey)
                        {
                            isAuthenticated = true;
                            lock (_wsLock) { _wsClients.Add(ws); }
                            await SendWebSocketMessageAsync(ws, new { type = "auth_success" });
                            Monitor.Log("[API] WebSocket client authenticated", LogLevel.Debug);
                        }
                        else
                        {
                            await SendWebSocketMessageAsync(ws, new { type = "auth_failed", error = "Invalid token" });
                            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication failed", CancellationToken.None);
                            Monitor.Log("[API] WebSocket client authentication failed - invalid token", LogLevel.Warn);
                            return false;
                        }
                    }
                    else
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Expected auth message", CancellationToken.None);
                        Monitor.Log("[API] WebSocket client authentication failed - unexpected message type", LogLevel.Warn);
                        return false;
                    }
                }
                catch (OperationCanceledException)
                {
                    await SendWebSocketMessageAsync(ws, new { type = "auth_failed", error = "Authentication timeout" });
                    try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication timeout", CancellationToken.None); }
                    catch (Exception closeEx)
                    {
                        Diagnostics.ModEventLog.Emit("exception_swallowed", new
                        {
                            location = "ApiService.ProcessWebSocketAsync.authTimeoutClose",
                            exceptionType = closeEx.GetType().Name,
                            message = closeEx.Message
                        });
                    }
                    Monitor.Log("[API] WebSocket client authentication timeout", LogLevel.Warn);
                    return false;
                }
            }

            while (ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                        // Check if message is too large
                        if (messageBuilder.Length > maxMessageSize)
                        {
                            Monitor.Log("[API] WebSocket message too large, discarding", LogLevel.Warn);
                            messageBuilder.Clear();
                            continue;
                        }

                        // Process complete messages only
                        if (result.EndOfMessage)
                        {
                            var json = messageBuilder.ToString();
                            messageBuilder.Clear();
                            await HandleWebSocketMessageAsync(ws, json);
                        }
                    }
                }
                catch (WebSocketException)
                {
                    // Client disconnected
                    break;
                }
            }

            return isAuthenticated;
        }

        private async Task HandleWebSocketMessageAsync(WebSocket ws, string json)
        {
            try
            {
                var msg = JsonConvert.DeserializeObject<WebSocketMessage>(json);
                if (msg == null) return;

                switch (msg.Type)
                {
                    case "ping":
                        await SendWebSocketMessageAsync(ws, new { type = "pong" });
                        break;

                    case "chat_send":
                        if (msg.Payload != null)
                        {
                            var chatPayload = msg.Payload.ToObject<ChatSendPayload>();
                            if (chatPayload != null &&
                                !string.IsNullOrWhiteSpace(chatPayload.Author) &&
                                !string.IsNullOrWhiteSpace(chatPayload.Message))
                            {
                                // Queue on game thread - SendPublicMessage iterates Game1.otherFarmers
                                // which could throw if a player connects/disconnects during iteration
                                var author = chatPayload.Author;
                                var message = chatPayload.Message;
                                await RunOnGameThreadAsync(() => OnExternalChatMessage?.Invoke(author, message));
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[API] Invalid WebSocket message: {ex.Message}", LogLevel.Debug);
            }
        }

        private async Task SendWebSocketMessageAsync(WebSocket ws, object message)
        {
            if (ws.State != WebSocketState.Open) return;

            var json = JsonConvert.SerializeObject(message, JsonSettings);
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            try
            {
                await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Client disconnected
            }
        }

        private void BroadcastToAllClients(object message)
        {
            var json = JsonConvert.SerializeObject(message, JsonSettings);
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            List<WebSocket> clients;
            lock (_wsLock) { clients = _wsClients.ToList(); }

            foreach (var ws in clients)
            {
                if (ws.State == WebSocketState.Open)
                {
                    // Fire and forget - don't block on individual sends
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        catch (WebSocketException)
                        {
                            // Client disconnected, will be cleaned up by periodic cleanup
                        }
                    });
                }
            }
        }

        private void CleanupDeadWebSocketClients()
        {
            List<WebSocket> deadClients;
            lock (_wsLock)
            {
                deadClients = _wsClients
                    .Where(ws => ws.State != WebSocketState.Open)
                    .ToList();

                foreach (var ws in deadClients)
                {
                    _wsClients.Remove(ws);
                }
            }

            if (deadClients.Count > 0)
            {
                Monitor.Log($"[API] Cleaned up {deadClients.Count} dead WebSocket client(s)", LogLevel.Debug);
            }

            foreach (var ws in deadClients)
            {
                try { ws.Dispose(); }
                catch (Exception disposeEx)
                {
                    Diagnostics.ModEventLog.Emit("exception_swallowed", new
                    {
                        location = "ApiService.BroadcastToAllClients.deadClientDispose",
                        exceptionType = disposeEx.GetType().Name,
                        message = disposeEx.Message
                    });
                }
            }
        }

        #endregion

        #region Endpoint Handlers

        /// <summary>
        /// Returns server status from the periodic snapshot. Never blocks on the game thread.
        /// Invite code derivation and version are computed per-request (both thread-safe).
        /// Game state fields come from <see cref="TakeGameStateSnapshot"/>.
        /// </summary>
        [ApiEndpoint("GET", "/status", Summary = "Get server status", Tag = "Server")]
        [ApiResponse(typeof(ServerStatus), 200, Description = "Server status and game state")]
        private ServerStatus HandleGetStatus()
        {
            var modInfo = Helper.ModRegistry.Get("JunimoHost.Server");
            var version = modInfo?.Manifest?.Version?.ToString() ?? "unknown";
            var snap = _snapshot;

            // Derive invite codes from file (thread-safe file read)
            var inviteCode = InviteCodeFile.Read(Monitor);
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

            if (!snap.IsOnline)
            {
                return new ServerStatus
                {
                    PlayerCount = 0,
                    MaxPlayers = 4,
                    ServerVersion = version,
                    IsOnline = false,
                    IsReady = false,
                    LastUpdated = snap.CapturedAt,
                    Version = snap.Version,
                };
            }

            return new ServerStatus
            {
                PlayerCount = snap.PlayerCount,
                MaxPlayers = snap.MaxPlayers,
                SteamInviteCode = steamInviteCode,
                GogInviteCode = gogInviteCode,
                ServerVersion = version,
                IsOnline = true,
                IsReady = snap.IsReady,
                LastUpdated = snap.CapturedAt,
                FarmName = snap.FarmName,
                Day = snap.Day,
                Season = snap.Season,
                Year = snap.Year,
                TimeOfDay = snap.TimeOfDay,
                FarmTypeKey = snap.FarmTypeKey,
                IsPaused = snap.IsPaused,
                Version = snap.Version,
            };
        }

        /// <summary>Reads from periodic snapshot. See <see cref="TakeGameStateSnapshot"/>.</summary>
        [ApiEndpoint("GET", "/players", Summary = "Get connected players", Tag = "Server")]
        [ApiResponse(typeof(PlayersResponse), 200, Description = "List of connected players")]
        private PlayersResponse HandleGetPlayers()
        {
            var snap = _snapshot;
            return new PlayersResponse { Players = snap.Players, Version = snap.Version };
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

        /// <summary>
        /// Ground-truth dump of live game-engine state for test failure analysis.
        /// Reads every field under an individual try/catch so one bad reflection
        /// site does not fail the whole response. Always returns 200 with
        /// <c>failedFields</c> listing any sub-reads that threw.
        ///
        /// Unlike <c>/status</c> / <c>/players</c> / <c>/farmhands</c> this is
        /// NOT served from the periodic snapshot — it reads directly from
        /// <c>Game1</c> so the caller sees the instantaneous state, which is
        /// the whole point on a timeout. Kept cheap (&lt;1 ms) so it is safe
        /// to call from every failure path.
        /// </summary>
        [ApiEndpoint("GET", "/diagnostics/state", Summary = "Live game-engine state snapshot (test harness)", Tag = "Diagnostics")]
        [ApiResponse(typeof(DiagnosticsStateResponse), 200)]
        private async Task<DiagnosticsStateResponse> HandleGetDiagnosticsStateAsync()
        {
            var resp = new DiagnosticsStateResponse
            {
                CapturedAt = DateTime.UtcNow.ToString("o")
            };

            // Phase 1 — primitive / thread-safe reads. These are value-type
            // snapshots or use cached Interlocked counters; safe from the HTTP
            // thread. Each block is independent; a failure only disables its
            // own field.
            TryRead("timeOfDay", resp.FailedFields, () => { resp.TimeOfDay = Game1.timeOfDay; });
            TryRead("dayOfMonth", resp.FailedFields, () => { resp.DayOfMonth = Game1.dayOfMonth; });
            TryRead("season", resp.FailedFields, () => { resp.Season = Game1.currentSeason ?? ""; });
            TryRead("year", resp.FailedFields, () => { resp.Year = Game1.year; });
            TryRead("gameMode", resp.FailedFields, () => { resp.GameMode = Game1.gameMode; });
            TryRead("lastTickMs", resp.FailedFields, () =>
            {
                var tickTicks = Interlocked.Read(ref _lastTickTimestamp);
                resp.LastTickMs = tickTicks > 0
                    ? (long)(DateTime.UtcNow - new DateTime(tickTicks, DateTimeKind.Utc)).TotalMilliseconds
                    : (long?)null;
            });
            TryRead("avgGameThreadWaitMs", resp.FailedFields, () =>
            {
                resp.AvgGameThreadWaitMs = Volatile.Read(ref _avgGameThreadWaitMs);
            });

            // Phase 2 — reads that touch Netcode collections or game-thread
            // mutable state. Marshalled to the game thread so we don't tear
            // enumerations that the tick loop is actively mutating. A single
            // marshal covers every unsafe block; one short hop is cheaper than
            // six. 3 s budget — tight enough to fail fast if the game thread
            // is stuck (the very condition the caller is diagnosing), loose
            // enough to absorb a legitimate save-in-progress hiccup.
            try
            {
                await RunOnGameThreadAsync(() =>
                {
                    TryRead("otherFarmerUids", resp.FailedFields, () =>
                    {
                        resp.OtherFarmerUids = Game1.otherFarmers?.Keys?.ToArray() ?? System.Array.Empty<long>();
                    });

                    TryRead("onlineFarmerCount", resp.FailedFields, () =>
                    {
                        resp.OnlineFarmerCount = Game1.getOnlineFarmers()?.Count() ?? 0;
                    });

                    TryRead("netReady", resp.FailedFields, () =>
                    {
                        resp.NetReady = ReadNetReadyState();
                    });

                    TryRead("newDaySync", resp.FailedFields, () =>
                    {
                        resp.NewDaySync = ReadNewDaySyncState();
                    });

                    TryRead("activeClickableMenu", resp.FailedFields, () =>
                    {
                        resp.ActiveClickableMenu = Game1.activeClickableMenu?.GetType().Name;
                    });

                    TryRead("isGameAvailable", resp.FailedFields, () =>
                    {
                        resp.IsGameAvailable = Game1.server?.isGameAvailable();
                    });

                    TryRead("cabins", resp.FailedFields, () =>
                    {
                        resp.Cabins = ReadCabinDiagnostics();
                    });

                    TryRead("farmhandData", resp.FailedFields, () =>
                    {
                        resp.FarmhandData = ReadFarmhandDiagnostics();
                    });

                    TryRead("disconnectingFarmers", resp.FailedFields, () =>
                    {
                        resp.DisconnectingFarmers = ReadDisconnectingFarmers();
                    });
                }, timeoutMs: 3000);
            }
            catch (TaskCanceledException)
            {
                // Game thread stuck. Mark all marshalled fields as failed so
                // the caller knows *why* these blocks are empty (distinct from
                // "no data available"). Still return 200 with whatever Phase 1
                // collected — that's already a strong diagnostic signal for
                // "the game thread is unresponsive", which is the core reason
                // /diagnostics/state exists.
                resp.FailedFields.Add("gameThreadTimeout");
            }

            return resp;
        }

        private static List<DiagnosticsCabinState> ReadCabinDiagnostics()
        {
            var list = new List<DiagnosticsCabinState>();
            var farm = Game1.getFarm();
            if (farm == null) return list;

            foreach (var building in farm.buildings)
            {
                try
                {
                    if (!building.isCabin) continue;
                    var cabin = building.GetIndoors<StardewValley.Locations.Cabin>();
                    var owner = cabin?.owner;
                    list.Add(new DiagnosticsCabinState
                    {
                        TileX = building.tileX.Value,
                        TileY = building.tileY.Value,
                        IndoorsName = cabin?.NameOrUniqueName ?? "",
                        OwnerId = owner?.UniqueMultiplayerID ?? 0,
                        OwnerName = owner?.Name ?? "",
                        OwnerIsCustomized = owner?.isCustomized?.Value ?? false,
                        OwnerHasUserId = !string.IsNullOrEmpty(owner?.userID?.Value),
                        HomeLocationOfOwner = owner?.homeLocation?.Value ?? "",
                        FarmhandReferenceDefined = cabin?.farmhandReference?.defined?.Value ?? false,
                        FarmhandReferenceUid = cabin?.farmhandReference?.uid?.Value ?? 0
                    });
                }
                catch { /* skip single-cabin read failures; the dump is best-effort */ }
            }
            return list;
        }

        private static List<DiagnosticsFarmhandState> ReadFarmhandDiagnostics()
        {
            var list = new List<DiagnosticsFarmhandState>();
            var farmhandData = Game1.netWorldState?.Value?.farmhandData;
            if (farmhandData == null) return list;

            foreach (var kv in farmhandData.Pairs)
            {
                try
                {
                    var f = kv.Value;
                    if (f == null) continue;
                    list.Add(new DiagnosticsFarmhandState
                    {
                        UniqueMultiplayerId = f.UniqueMultiplayerID,
                        Name = f.Name ?? "",
                        IsCustomized = f.isCustomized?.Value ?? false,
                        HomeLocation = f.homeLocation?.Value ?? "",
                        LastSleepLocation = f.lastSleepLocation?.Value ?? "",
                        HasUserId = !string.IsNullOrEmpty(f.userID?.Value)
                    });
                }
                catch { /* per-entry failure tolerated */ }
            }
            return list;
        }

        private static System.Reflection.FieldInfo? _disconnectingFarmersField;
        private static System.Reflection.FieldInfo? _game1MultiplayerField;

        private static long[] ReadDisconnectingFarmers()
        {
            // Game1.multiplayer is non-public; resolve via reflection the same way
            // ModHelperExtensions.GetMultiplayer does.
            var mpField = _game1MultiplayerField ??= typeof(Game1)
                .GetField("multiplayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mpField == null) return System.Array.Empty<long>();
            var mp = mpField.GetValue(null) as Multiplayer;
            if (mp == null) return System.Array.Empty<long>();

            var fi = _disconnectingFarmersField ??= typeof(Multiplayer)
                .GetField("disconnectingFarmers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi == null) return System.Array.Empty<long>();
            if (fi.GetValue(mp) is not System.Collections.Generic.IEnumerable<long> ids) return System.Array.Empty<long>();
            return ids.ToArray();
        }

        private static void TryRead(string fieldName, List<string> failedFields, System.Action read)
        {
            try { read(); }
            catch (Exception) { failedFields.Add(fieldName); }
        }

        // FieldInfo for ReadySynchronizer.ReadyChecks (private). Cached once at
        // first use; reflection lookup cost is negligible after caching.
        private static System.Reflection.FieldInfo? _readyChecksField;

        private static List<ReadyCheckState> ReadNetReadyState()
        {
            var list = new List<ReadyCheckState>();
            var netReady = Game1.netReady;
            if (netReady == null) return list;

            var fi = _readyChecksField ??= typeof(StardewValley.Network.NetReady.ReadySynchronizer)
                .GetField("ReadyChecks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi == null) return list;

            if (fi.GetValue(netReady) is not System.Collections.IDictionary dict) return list;

            foreach (System.Collections.DictionaryEntry kv in dict)
            {
                var id = kv.Key?.ToString() ?? "";
                var check = kv.Value;
                if (check == null) continue;

                // BaseReadyCheck has public NumberReady/NumberRequired/IsReady/State.
                // State enum values: NotReady, Ready, Locked — we surface IsLocked
                // as State==Locked. Read each defensively.
                int numberReady = 0, numberRequired = 0;
                bool isReady = false, isLocked = false;
                try { numberReady = (int)(check.GetType().GetProperty("NumberReady")?.GetValue(check) ?? 0); } catch { }
                try { numberRequired = (int)(check.GetType().GetProperty("NumberRequired")?.GetValue(check) ?? 0); } catch { }
                try { isReady = (bool)(check.GetType().GetProperty("IsReady")?.GetValue(check) ?? false); } catch { }
                try
                {
                    var state = check.GetType().GetProperty("State")?.GetValue(check);
                    isLocked = state != null && state.ToString() == "Locked";
                }
                catch { }

                list.Add(new ReadyCheckState
                {
                    Id = id,
                    NumberReady = numberReady,
                    NumberRequired = numberRequired,
                    IsReady = isReady,
                    IsLocked = isLocked
                });
            }

            return list;
        }

        private static NewDaySyncState ReadNewDaySyncState()
        {
            var s = new NewDaySyncState();
            var sync = Game1.newDaySync;
            if (sync == null) return s;

            // hasInstance() / hasStarted() / hasFinished() are public on NewDaySynchronizer.
            try { s.HasStarted = sync.hasStarted(); } catch { }
            try { s.HasFinished = sync.hasFinished(); } catch { }
            try { s.IsActive = sync.hasInstance(); } catch { }
            return s;
        }

        [ApiEndpoint("GET", "/health", Summary = "Health check", Tag = "Health")]
        [ApiResponse(typeof(HealthResponse), 200, Description = "Health status")]
        private HealthResponse HandleGetHealth()
        {
            // Read game thread liveness without RunOnGameThreadAsync. /health must respond even when stuck.
            var tickTicks = Interlocked.Read(ref _lastTickTimestamp);
            long? lastTickMs = tickTicks > 0
                ? (long)(DateTime.UtcNow - new DateTime(tickTicks, DateTimeKind.Utc)).TotalMilliseconds
                : null;

            var pendingActions = _pendingGameActions.Count;

            bool? gameAvailable = null;
            try { gameAvailable = Game1.server?.isGameAvailable(); }
            catch (Exception ex)
            {
                // This is the single most important exception swallow in the
                // mod: /health is supposed to REPORT this condition, and the
                // prior `catch { }` was masking the very failure it exists to
                // surface. Emit a structured event so a broken /health is
                // correlatable across containers, but keep the health
                // response going (with gameAvailable left null) so the
                // endpoint itself never 500s.
                Diagnostics.ModEventLog.Emit("exception_swallowed", new
                {
                    location = "ApiService.HandleGetHealth.isGameAvailable",
                    exceptionType = ex.GetType().Name,
                    message = ex.Message
                });
            }

            var isFrozen = lastTickMs is null or > HealthFrozenThresholdMs;
            var status = isFrozen ? "degraded" : "ok";
            var totalTicks = Interlocked.Read(ref _totalTickCount);

            return new HealthResponse
            {
                Status = status,
                Timestamp = DateTime.UtcNow.ToString("o"),
                LastTickMs = lastTickMs,
                PendingActions = pendingActions,
                GameAvailable = gameAvailable,
                TickCount = totalTicks,
                IsFrozen = isFrozen,
            };
        }

        // Server-side hard cap on /wait/* timeouts. Long-poll handlers run on
        // the HttpListener thread pool; an unbounded await would starve under
        // load. 10s is well below any client read timeout and leaves plenty of
        // room above the snapshot's 1 Hz refresh cadence.
        private static readonly TimeSpan WaitMaxTimeout = TimeSpan.FromSeconds(10);

        // Threshold for considering the game thread frozen. /health reports
        // status="degraded" and IsFrozen=true when the gap since _lastTickTimestamp
        // exceeds this; /wait/health's ready=true predicate uses the inverse.
        private const long HealthFrozenThresholdMs = 5000;

        /// <summary>
        /// Long-poll variant of <c>/status</c>. Query params:
        /// <list type="bullet">
        /// <item><c>since=N</c> — last observed snapshot version. Required.</item>
        /// <item><c>isReady=true|false</c> — wait until the snapshot's IsReady matches.</item>
        /// <item><c>isPaused=true|false</c> — wait until the snapshot's IsPaused matches.</item>
        /// <item><c>day=N</c> — wait until the snapshot's Day matches.</item>
        /// <item><c>playerCount=N</c> — wait until the snapshot's PlayerCount equals N.</item>
        /// <item><c>timeout=ms</c> — bounded by <see cref="WaitMaxTimeout"/>.</item>
        /// </list>
        /// Returns 200 + current /status response when filters match a newer
        /// snapshot, or 408 if the timeout elapses with no match.
        /// </summary>
        private async Task HandleWaitStatusAsync(HttpListenerRequest request, HttpListenerResponse response, string? requestId)
        {
            var qs = request.QueryString;
            var since = ParseLong(qs["since"]) ?? 0;
            var isReadyFilter = ParseBool(qs["isReady"]);
            var isPausedFilter = ParseBool(qs["isPaused"]);
            var dayFilter = ParseInt(qs["day"]);
            var playerCountFilter = ParseInt(qs["playerCount"]);
            var timeout = ResolveWaitTimeout(qs["timeout"]);

            bool Matches(GameStateSnapshot snap)
            {
                if (snap.Version <= since) return false;
                if (isReadyFilter is bool b && snap.IsReady != b) return false;
                if (isPausedFilter is bool ip && snap.IsPaused != ip) return false;
                if (dayFilter is int d && snap.Day != d) return false;
                if (playerCountFilter is int pc && snap.PlayerCount != pc) return false;
                return true;
            }

            var matched = await WaitForSnapshotAsync(Matches, timeout, requestId);
            if (matched == null)
            {
                response.StatusCode = 408;
                return;
            }

            // Predicate-transition time: latest of the filtered fields'
            // change-times. The predicate became satisfiable the moment the
            // last-to-change contributing field flipped to its predicate-
            // satisfying value, which is the max across the filters in play.
            // Version-only / unfiltered case has no field to report; the
            // header is omitted and the harness falls back to snapshot-age.
            var changedAt = default(DateTime);
            if (isReadyFilter is not null) changedAt = MaxUtc(changedAt, matched.IsReadyChangedAtUtc);
            if (isPausedFilter is not null) changedAt = MaxUtc(changedAt, matched.IsPausedChangedAtUtc);
            if (dayFilter is not null) changedAt = MaxUtc(changedAt, matched.DayChangedAtUtc);
            if (playerCountFilter is not null) changedAt = MaxUtc(changedAt, matched.PlayerCountChangedAtUtc);
            EmitPredicateChangedAtHeader(response, changedAt);

            // Delegate to the regular status handler so the response shape is
            // identical to /status (caller's deserializer doesn't need a
            // separate type). HandleGetStatus reads _snapshot atomically; the
            // snapshot version may have advanced since Matches saw it, but the
            // returned data is at least as fresh as what the predicate matched.
            await WriteJsonAsync(response, HandleGetStatus());
        }

        private static DateTime MaxUtc(DateTime a, DateTime b) => a > b ? a : b;

        /// <summary>
        /// Long-poll variant of <c>/players</c>. Query params:
        /// <list type="bullet">
        /// <item><c>since=N</c> — last observed snapshot version.</item>
        /// <item><c>playerId=N</c> — wait until the snapshot's Players list contains the id.</item>
        /// <item><c>timeout=ms</c> — bounded by <see cref="WaitMaxTimeout"/>.</item>
        /// </list>
        /// </summary>
        // Per-handler phase timer aggregator. Used by /diagnostics/handler-timing
        // to surface where /farmhands time goes when the regression cause is
        // unclear. Counts in microseconds for sub-millisecond granularity.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HandlerTimingAccumulator> _handlerTimings = new();

        private sealed class HandlerTimingAccumulator
        {
            public long Calls;
            public long SnapshotReadUs;
            public long SerializeUs;
            public long EmitUs;
            public long WriteUs;
            public long TotalUs;
        }

        public sealed class HandlerTimingResponse
        {
            public string Handler { get; set; } = "";
            public long Calls { get; set; }
            public double SnapshotReadAvgMs { get; set; }
            public double SerializeAvgMs { get; set; }
            public double EmitAvgMs { get; set; }
            public double WriteAvgMs { get; set; }
            public double TotalAvgMs { get; set; }
        }

        public sealed class HandlerTimingReport
        {
            public List<HandlerTimingResponse> Handlers { get; set; } = new();
        }

        /// <summary>
        /// /farmhands handler with per-phase timing. Aggregates into
        /// <see cref="_handlerTimings"/> so a follow-up call to
        /// <c>/diagnostics/handler-timing</c> surfaces where time goes when
        /// the snapshot endpoint regresses.
        /// </summary>
        private async Task ProfileFarmhandsAsync(HttpListenerResponse response)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var snapshotReadStart = sw.Elapsed;
            var farmhands = HandleGetFarmhands();
            var snapshotReadEnd = sw.Elapsed;

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(farmhands, JsonSettings);
            var serializeEnd = sw.Elapsed;

            // No event emitted from /farmhands; emit_ms is the gap between
            // serialize and the actual response stream write — buffer encoding
            // + content-length header writes. Cheap probe but useful when
            // chasing a regression.
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            var buffer = System.Text.Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            var emitEnd = sw.Elapsed;

            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            var writeEnd = sw.Elapsed;

            var acc = _handlerTimings.GetOrAdd("/farmhands", _ => new HandlerTimingAccumulator());
            Interlocked.Increment(ref acc.Calls);
            Interlocked.Add(ref acc.SnapshotReadUs, ToUs(snapshotReadEnd - snapshotReadStart));
            Interlocked.Add(ref acc.SerializeUs, ToUs(serializeEnd - snapshotReadEnd));
            Interlocked.Add(ref acc.EmitUs, ToUs(emitEnd - serializeEnd));
            Interlocked.Add(ref acc.WriteUs, ToUs(writeEnd - emitEnd));
            Interlocked.Add(ref acc.TotalUs, ToUs(writeEnd));
        }

        private static long ToUs(TimeSpan ts) => (long)(ts.TotalMilliseconds * 1000.0);

        private HandlerTimingReport BuildHandlerTimingReport()
        {
            var report = new HandlerTimingReport();
            foreach (var (name, acc) in _handlerTimings)
            {
                var calls = Interlocked.Read(ref acc.Calls);
                if (calls == 0) continue;
                report.Handlers.Add(new HandlerTimingResponse
                {
                    Handler = name,
                    Calls = calls,
                    SnapshotReadAvgMs = Interlocked.Read(ref acc.SnapshotReadUs) / 1000.0 / calls,
                    SerializeAvgMs = Interlocked.Read(ref acc.SerializeUs) / 1000.0 / calls,
                    EmitAvgMs = Interlocked.Read(ref acc.EmitUs) / 1000.0 / calls,
                    WriteAvgMs = Interlocked.Read(ref acc.WriteUs) / 1000.0 / calls,
                    TotalAvgMs = Interlocked.Read(ref acc.TotalUs) / 1000.0 / calls,
                });
            }
            return report;
        }

        private async Task HandleWaitPlayersAsync(HttpListenerRequest request, HttpListenerResponse response, string? requestId)
        {
            var qs = request.QueryString;
            var since = ParseLong(qs["since"]) ?? 0;
            var playerIdFilter = ParseLong(qs["playerId"]);
            var timeout = ResolveWaitTimeout(qs["timeout"]);

            bool Matches(GameStateSnapshot snap)
            {
                if (snap.Version <= since) return false;
                if (playerIdFilter is long pid)
                {
                    var found = false;
                    foreach (var p in snap.Players) { if (p.Id == pid) { found = true; break; } }
                    if (!found) return false;
                }
                return true;
            }

            var matched = await WaitForSnapshotAsync(Matches, timeout, requestId);
            if (matched == null)
            {
                response.StatusCode = 408;
                return;
            }

            // Predicate-transition time: for the playerId filter, when that
            // player first appeared in any snapshot. For the version-only case
            // (no playerId filter), the snapshot's capture time is the best
            // we can do — there's no single "field that changed" to report.
            if (playerIdFilter is long matchedPid
                && matched.PlayerFirstSeenAtUtc.TryGetValue(matchedPid, out var firstSeen))
            {
                EmitPredicateChangedAtHeader(response, firstSeen);
            }
            await WriteJsonAsync(response, HandleGetPlayers());
        }

        /// <summary>
        /// Long-poll variant of <c>/farmhands</c>. Snapshot-backed; reuses
        /// <see cref="WaitForSnapshotAsync"/>. Query params:
        /// <list type="bullet">
        /// <item><c>since=N</c> — last observed snapshot version.</item>
        /// <item><c>farmhandCount=N</c> — wait until the snapshot's farmhand list
        /// contains exactly N entries.</item>
        /// <item><c>hasFarmhand=&lt;name&gt;</c> — wait until a farmhand with the
        /// given name is present (case-insensitive).</item>
        /// <item><c>requireCustomized=true|false</c> — when combined with
        /// <c>hasFarmhand</c>, requires the matching farmhand's
        /// <c>IsCustomized</c> flag.</item>
        /// <item><c>timeout=ms</c> — bounded by <see cref="WaitMaxTimeout"/>.</item>
        /// </list>
        /// </summary>
        private async Task HandleWaitFarmhandsAsync(HttpListenerRequest request, HttpListenerResponse response, string? requestId)
        {
            var qs = request.QueryString;
            var since = ParseLong(qs["since"]) ?? 0;
            var farmhandCountFilter = ParseInt(qs["farmhandCount"]);
            var hasFarmhandFilter = qs["hasFarmhand"];
            var requireCustomizedFilter = ParseBool(qs["requireCustomized"]);
            var timeout = ResolveWaitTimeout(qs["timeout"]);

            bool Matches(GameStateSnapshot snap)
            {
                if (snap.Version <= since) return false;
                if (farmhandCountFilter is int fc && snap.Farmhands.Count != fc) return false;
                if (!string.IsNullOrEmpty(hasFarmhandFilter))
                {
                    var found = false;
                    foreach (var f in snap.Farmhands)
                    {
                        if (!string.Equals(f.Name, hasFarmhandFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (requireCustomizedFilter is bool rc && f.IsCustomized != rc)
                            continue;
                        found = true;
                        break;
                    }
                    if (!found) return false;
                }
                return true;
            }

            var matched = await WaitForSnapshotAsync(Matches, timeout, requestId);
            if (matched == null)
            {
                response.StatusCode = 408;
                return;
            }

            // Predicate-transition time: when the matching farmhand was first
            // seen (for `hasFarmhand`) and/or its customization last flipped
            // (when `requireCustomized` is involved). For `farmhandCount` we
            // take the latest first-seen across all current farmhands — that's
            // when the count last reached the current size. Each contributing
            // predicate is folded via max; the header lands on the latest
            // contributing transition.
            var changedAt = default(DateTime);
            if (!string.IsNullOrEmpty(hasFarmhandFilter))
            {
                foreach (var f in matched.Farmhands)
                {
                    if (!string.Equals(f.Name, hasFarmhandFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (matched.FarmhandChangeTracks.TryGetValue(f.Id, out var track))
                    {
                        changedAt = MaxUtc(changedAt, track.FirstSeenAtUtc);
                        if (requireCustomizedFilter is not null)
                            changedAt = MaxUtc(changedAt, track.IsCustomizedChangedAtUtc);
                    }
                    break;
                }
            }
            else if (farmhandCountFilter is not null)
            {
                foreach (var track in matched.FarmhandChangeTracks.Values)
                    changedAt = MaxUtc(changedAt, track.FirstSeenAtUtc);
            }
            EmitPredicateChangedAtHeader(response, changedAt);

            await WriteJsonAsync(response, HandleGetFarmhands());
        }

        /// <summary>
        /// Long-poll variant of <c>/health</c>. Source-of-truth is
        /// <c>_lastTickTimestamp</c>, not the snapshot — game-thread liveness
        /// updates every tick (~60 Hz) regardless of snapshot publish gating
        /// or Game1.newDay skips. Filters:
        /// <list type="bullet">
        /// <item><c>ready=true</c> — block until <c>IsFrozen == false</c>
        /// (lastTickMs non-null and ≤ 5 s).</item>
        /// <item><c>timeout=ms</c> — bounded by <see cref="WaitMaxTimeout"/>.</item>
        /// </list>
        /// Stateless predicate: no <c>since</c> cursor, just "is the server
        /// healthy now". A waiter that arrives during cold-start (before any
        /// tick) sees nothing to rotate the TCS, so the call returns 408 after
        /// <see cref="WaitMaxTimeout"/>; callers must wrap in an outer
        /// re-issue loop.
        /// </summary>
        private async Task HandleWaitHealthAsync(HttpListenerRequest request, HttpListenerResponse response, string? requestId)
        {
            var qs = request.QueryString;
            var readyFilter = ParseBool(qs["ready"]);
            var timeout = ResolveWaitTimeout(qs["timeout"]);

            bool Matches()
            {
                if (readyFilter is not bool b || !b) return true;
                var tickTicks = Interlocked.Read(ref _lastTickTimestamp);
                if (tickTicks <= 0) return false;
                var lastTickMs = (long)(DateTime.UtcNow - new DateTime(tickTicks, DateTimeKind.Utc)).TotalMilliseconds;
                return lastTickMs <= HealthFrozenThresholdMs;
            }

            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    response.StatusCode = 408;
                    return;
                }

                // Capture TCS BEFORE evaluating the predicate. If a tick rotates
                // the TCS between these two reads, the rotation also signals
                // the TCS we just captured — the await returns immediately and
                // we re-evaluate. The reverse order has a missed-wakeup race.
                var tcs = _lastTickChanged;
                if (Matches())
                {
                    await WriteJsonAsync(response, HandleGetHealth());
                    return;
                }

                try
                {
                    await tcs.Task.WaitAsync(remaining);
                }
                catch (TimeoutException)
                {
                    response.StatusCode = 408;
                    return;
                }

                // Re-bind requestId for any downstream emits — the continuation
                // may have resumed on a fresh thread-pool worker that didn't
                // flow our AsyncLocal cleanly. See .claude/rules/asynclocal-pitfalls.md.
                using var _ = Diagnostics.ModRequestContext.Bind(requestId);
            }
        }

        /// <summary>
        /// Awaits a snapshot satisfying <paramref name="predicate"/>, bounded
        /// by <paramref name="timeout"/>. Returns the matching snapshot or
        /// null on timeout.
        ///
        /// <para>
        /// AsyncLocal correctness: <see cref="ModRequestContext"/> is bound by
        /// the outer <see cref="HandleRequestAsync"/> via <c>using</c>; that
        /// scope flows through the await, so any post-resume
        /// <c>ModEventLog.Emit</c> here would still see the requestId. We
        /// re-bind explicitly anyway because some HttpListener
        /// implementations resume continuations on a new thread-pool worker
        /// without flowing the AsyncLocal cleanly. See
        /// <c>.claude/rules/asynclocal-pitfalls.md</c>.
        /// </para>
        /// </summary>
        /// <summary>
        /// Emits the <c>X-Predicate-Changed-At-Ms-Ago</c> response header so the
        /// test harness can stamp <c>wait_matched</c>'s producer-time with the
        /// moment the predicate first became satisfiable — not the snapshot's
        /// capture time, which lags by up to one snapshot publish cycle (1s).
        ///
        /// <para>
        /// The argument is the transition-time UTC instant of the field (or
        /// the latest of several fields, for compound predicates) that the
        /// matching predicate keyed on. Computed by the caller because only
        /// the wait handler knows which fields its predicate looked at.
        /// </para>
        ///
        /// <para>
        /// Skipped silently when <paramref name="changedAtUtc"/> is <c>default</c>
        /// (no usable change-time available) — falls back to the harness's
        /// snapshot-age estimate.
        /// </para>
        /// </summary>
        private static void EmitPredicateChangedAtHeader(HttpListenerResponse response, DateTime changedAtUtc)
        {
            if (changedAtUtc == default) return;
            var msAgo = (long)(DateTime.UtcNow - changedAtUtc).TotalMilliseconds;
            if (msAgo < 0) msAgo = 0;
            response.Headers["X-Predicate-Changed-At-Ms-Ago"] =
                msAgo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task<GameStateSnapshot?> WaitForSnapshotAsync(
            Func<GameStateSnapshot, bool> predicate, TimeSpan timeout, string? requestId)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;

                // Capture TCS BEFORE reading the snapshot. If PublishSnapshot rotates
                // the TCS between these two reads, the rotation also signals the TCS
                // we just captured — the await returns immediately and we re-evaluate.
                // The reverse order has a missed-wakeup window: a rotation between
                // the snapshot read and the TCS read leaves us awaiting the *next*
                // publish for a snapshot we'd already see if we re-read.
                var tcs = _snapshotChanged;
                var current = _snapshot;
                if (predicate(current)) return current;

                try
                {
                    await tcs.Task.WaitAsync(remaining);
                }
                catch (TimeoutException)
                {
                    return null;
                }

                // Continuation may have resumed on a fresh thread-pool worker;
                // re-bind so any structured event we emit below carries the
                // caller's requestId.
                using var _ = Diagnostics.ModRequestContext.Bind(requestId);
            }
        }

        private TimeSpan ResolveWaitTimeout(string? raw)
        {
            if (long.TryParse(raw, out var ms) && ms > 0)
            {
                var requested = TimeSpan.FromMilliseconds(ms);
                return requested < WaitMaxTimeout ? requested : WaitMaxTimeout;
            }
            return WaitMaxTimeout;
        }

        private static long? ParseLong(string? raw)
            => long.TryParse(raw, out var v) ? v : null;

        private static int? ParseInt(string? raw)
            => int.TryParse(raw, out var v) ? v : null;

        private static bool? ParseBool(string? raw)
        {
            if (raw == null) return null;
            if (bool.TryParse(raw, out var b)) return b;
            return null;
        }

        [ApiEndpoint("GET", "/stats", Summary = "Performance stats", Tag = "Diagnostics")]
        [ApiResponse(typeof(StatsResponse), 200)]
        private StatsResponse HandleGetStats()
        {
            return new StatsResponse
            {
                Fps = Math.Round(Volatile.Read(ref _currentFps), 1),
                Tps = Math.Round(Volatile.Read(ref _currentTps), 1),
                AvgTickMs = Math.Round(Volatile.Read(ref _avgTickMs), 2),
                MemoryMb = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1),
                GcGen0 = GC.CollectionCount(0),
                GcGen1 = GC.CollectionCount(1),
                GcGen2 = GC.CollectionCount(2),
                PendingActions = _pendingGameActions.Count,
                GameThreadWaitMs = Math.Round(Volatile.Read(ref _avgGameThreadWaitMs), 2),
            };
        }

        /// <summary>Reads from periodic snapshot. See <see cref="TakeGameStateSnapshot"/>.</summary>
        [ApiEndpoint("GET", "/farmhands", Summary = "Get all farmhand slots", Tag = "Farmhands")]
        [ApiResponse(typeof(FarmhandsResponse), 200, Description = "List of farmhand slots")]
        private FarmhandsResponse HandleGetFarmhands()
        {
            var snap = _snapshot;
            return new FarmhandsResponse { Farmhands = snap.Farmhands, Version = snap.Version };
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

        /// <summary>Reads from periodic snapshot. See <see cref="TakeGameStateSnapshot"/>.</summary>
        [ApiEndpoint("GET", "/cabins", Summary = "Get cabin state and positions", Tag = "Cabins")]
        [ApiResponse(typeof(CabinsResponse), 200, Description = "Cabin state snapshot")]
        private CabinsResponse HandleGetCabins()
        {
            var snap = _snapshot;
            return new CabinsResponse
            {
                Strategy = _persistentOptions.Data.CabinStrategy.ToString(),
                TotalCount = snap.CabinTotalCount,
                AssignedCount = snap.CabinAssignedCount,
                AvailableCount = snap.CabinAvailableCount,
                Cabins = snap.Cabins,
                SavedPositionPlayerIds = _cabinManager.Data.PlayerCabinPositions.Keys.ToList()
            };
        }

        [ApiEndpoint("GET", "/rendering", Summary = "Get render rate", Tag = "Server")]
        [ApiResponse(typeof(RenderingStatus), 200, Description = "Current render rate (0 = disabled)")]
        private RenderingStatus HandleGetRendering()
        {
            return new RenderingStatus
            {
                Fps = ServerOptimizerOverrides.GetCurrentServerFps()
            };
        }

        [ApiEndpoint("GET", "/screenshot", Summary = "Capture a screenshot of the game", Tag = "Server")]
        [ApiResponse(typeof(ScreenshotResponse), 200, Description = "Screenshot as base64-encoded PNG")]
        private async Task<ScreenshotResponse> HandleGetScreenshotAsync()
        {
            var result = new ScreenshotResponse();
            try
            {
                await RunOnGameThreadAsync(() =>
                {
                    var device = Game1.graphics.GraphicsDevice;
                    var pp = device.PresentationParameters;
                    var width = pp.BackBufferWidth;
                    var height = pp.BackBufferHeight;

                    var backBuffer = new Microsoft.Xna.Framework.Color[width * height];
                    device.GetBackBufferData(backBuffer);

                    using var texture = new Microsoft.Xna.Framework.Graphics.Texture2D(
                        device, width, height, false, Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color);
                    texture.SetData(backBuffer);

                    using var stream = new System.IO.MemoryStream();
                    texture.SaveAsPng(stream, width, height);

                    result.Success = true;
                    result.Base64Png = Convert.ToBase64String(stream.ToArray());
                    result.Width = width;
                    result.Height = height;
                });
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }

        // TODO: Consider splitting config fields (Enabled, TimeoutSeconds, MaxAttempts) from
        // live state (AuthenticatedCount, PendingCount). Config is cheap; live state requires
        /// <summary>
        /// Reads player counts from periodic snapshot, config from the service directly.
        /// See <see cref="TakeGameStateSnapshot"/>.
        /// </summary>
        [ApiEndpoint("GET", "/auth", Summary = "Get authentication/password protection status", Tag = "Auth")]
        [ApiResponse(typeof(AuthStatusResponse), 200, Description = "Authentication status")]
        private AuthStatusResponse HandleGetAuthStatus()
        {
            if (_passwordProtectionService == null || !_passwordProtectionService.IsEnabled)
            {
                return new AuthStatusResponse
                {
                    Enabled = false,
                    AuthenticatedCount = 0,
                    PendingCount = 0,
                    TimeoutSeconds = 0,
                    MaxAttempts = 0
                };
            }

            var snap = _snapshot;
            return new AuthStatusResponse
            {
                Enabled = true,
                AuthenticatedCount = snap.AuthenticatedCount,
                PendingCount = snap.PendingCount,
                TimeoutSeconds = _passwordProtectionService.AuthTimeoutSeconds,
                MaxAttempts = _passwordProtectionService.MaxFailedAttempts
            };
        }

        [ApiEndpoint("POST", "/auth/timeout", Summary = "Set auth timeout in seconds", Tag = "Auth")]
        [ApiResponse(typeof(AuthTimeoutResponse), 200, Description = "Auth timeout updated")]
        private AuthTimeoutResponse HandlePostAuthTimeout(string? value)
        {
            if (_passwordProtectionService == null || !_passwordProtectionService.IsEnabled)
            {
                return new AuthTimeoutResponse
                {
                    Success = false,
                    TimeoutSeconds = 0,
                    Error = "Password protection is not enabled"
                };
            }

            if (string.IsNullOrEmpty(value) || !int.TryParse(value, out var seconds) || seconds < 0)
            {
                return new AuthTimeoutResponse
                {
                    Success = false,
                    TimeoutSeconds = _passwordProtectionService.AuthTimeoutSeconds,
                    Error = "Missing or invalid 'value' parameter (expected non-negative integer)"
                };
            }

            var previous = _passwordProtectionService.AuthTimeoutSeconds;
            _passwordProtectionService.AuthTimeoutSeconds = seconds;
            Monitor.Log($"[API] Auth timeout changed: {previous}s → {seconds}s", LogLevel.Info);

            return new AuthTimeoutResponse
            {
                Success = true,
                TimeoutSeconds = seconds,
                PreviousTimeoutSeconds = previous
            };
        }

        [ApiEndpoint("POST", "/rendering", Summary = "Set server render rate", Tag = "Server")]
        [ApiResponse(typeof(RenderingSetResponse), 200, Description = "Render rate set")]
        private async Task<RenderingSetResponse> HandlePostRenderingAsync(string? fps)
        {
            if (string.IsNullOrEmpty(fps) || !int.TryParse(fps, out var newFps) || newFps < 0)
            {
                return new RenderingSetResponse
                {
                    Success = false,
                    Fps = ServerOptimizerOverrides.GetCurrentServerFps(),
                    Error = "Missing or invalid 'fps' parameter (expected non-negative integer; 0 disables rendering)"
                };
            }

            var previous = ServerOptimizerOverrides.GetCurrentServerFps();
            await RunOnGameThreadAsync(() =>
            {
                ServerOptimizerOverrides.SetServerFps(newFps, Monitor);
            });

            return new RenderingSetResponse
            {
                Success = true,
                Fps = newFps,
                PreviousFps = previous,
                Message = newFps == 0 ? "Rendering disabled" : $"Rendering enabled at {newFps} fps"
            };
        }

        [ApiEndpoint("POST", "/time", Summary = "Set game time of day", Tag = "Server")]
        [ApiResponse(typeof(TimeSetResponse), 200, Description = "Time set")]
        private async Task<TimeSetResponse> HandlePostTimeAsync(string? value)
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

            await RunOnGameThreadAsync(() =>
            {
                Game1.timeOfDay = time;
                Game1.gameTimeInterval = 0;
            });
            Monitor.Log($"Time set to {time} via API", LogLevel.Info);

            return new TimeSetResponse
            {
                Success = true,
                TimeOfDay = time,
                Message = $"Time set to {time}"
            };
        }

        [ApiEndpoint("POST", "/clock-speed", Summary = "Adjust game clock speed", Tag = "Server")]
        [ApiResponse(typeof(ClockSpeedResponse), 200, Description = "Clock speed adjusted")]
        private async Task<ClockSpeedResponse> HandlePostClockSpeedAsync(string? multiplierStr)
        {
            if (string.IsNullOrEmpty(multiplierStr))
            {
                return new ClockSpeedResponse
                {
                    Success = false,
                    Error = "Missing 'multiplier' query parameter (e.g., 10 for 10x speed, 1 to restore)"
                };
            }

            if (!double.TryParse(multiplierStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var multiplier))
            {
                return new ClockSpeedResponse
                {
                    Success = false,
                    Error = $"Invalid multiplier '{multiplierStr}' (expected a number like 10, 0.5, or 1)"
                };
            }

            if (multiplier <= 0)
            {
                return new ClockSpeedResponse
                {
                    Success = false,
                    Error = "Multiplier must be greater than 0"
                };
            }

            int effectiveMs = 0;
            await RunOnGameThreadAsync(() =>
            {
                // Capture default on first call
                if (_defaultRealMsPerGameMinute < 0)
                    _defaultRealMsPerGameMinute = Game1.realMilliSecondsPerGameMinute;

                var newMs = (int)(_defaultRealMsPerGameMinute / multiplier);
                if (newMs < 1) newMs = 1;
                Game1.realMilliSecondsPerGameMinute = newMs;
                Game1.realMilliSecondsPerGameTenMinutes = newMs * 10;
                effectiveMs = newMs;
            });

            Monitor.Log($"Clock speed set to {multiplier}x ({effectiveMs}ms/game-minute) via API", LogLevel.Info);

            return new ClockSpeedResponse
            {
                Success = true,
                Multiplier = multiplier,
                EffectiveMs = effectiveMs
            };
        }

        [ApiEndpoint("POST", "/roles/admin", Summary = "Grant admin role to a player", Tag = "Roles")]
        [ApiResponse(typeof(RoleGrantResponse), 200, Description = "Admin granted")]
        private async Task<RoleGrantResponse> HandlePostGrantAdminAsync(string? name, string? playerId)
        {
            var hasName = !string.IsNullOrEmpty(name);
            var hasId = !string.IsNullOrEmpty(playerId);

            if (!hasName && !hasId)
            {
                return new RoleGrantResponse
                {
                    Success = false,
                    Error = "Missing 'name' or 'playerId' query parameter"
                };
            }
            if (hasName && hasId)
            {
                return new RoleGrantResponse
                {
                    Success = false,
                    Error = "Provide either 'name' or 'playerId', not both"
                };
            }

            long parsedId = 0;
            if (hasId && !long.TryParse(playerId, out parsedId))
            {
                return new RoleGrantResponse
                {
                    Success = false,
                    Error = $"Invalid 'playerId' value: '{playerId}'"
                };
            }

            if (Game1.gameMode != 3 || !Game1.IsServer)
            {
                return new RoleGrantResponse { Success = false, Error = "Server not ready" };
            }

            RoleGrantResponse? result = null;
            await RunOnGameThreadAsync(() =>
            {
                var farmer = hasId
                    ? Game1.getAllFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == parsedId)
                    : Game1.getAllFarmers().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                if (farmer == null)
                {
                    var lookup = hasId ? $"id={parsedId}" : $"'{name}'";
                    result = new RoleGrantResponse
                    {
                        Success = false,
                        Error = $"Player {lookup} not found"
                    };
                    return;
                }

                _roleService.AssignAdmin(farmer.UniqueMultiplayerID);
                Monitor.Log($"Admin role granted to '{ChatRedaction.MaskValue(farmer.Name)}' (ID: {farmer.UniqueMultiplayerID}) via API", LogLevel.Info);

                result = new RoleGrantResponse
                {
                    Success = true,
                    PlayerId = farmer.UniqueMultiplayerID,
                    PlayerName = farmer.Name,
                    Message = $"Admin role granted to '{farmer.Name}'"
                };
            });

            return result!;
        }

        [ApiEndpoint("DELETE", "/farmhands", Summary = "Delete a farmhand by name or player ID", Tag = "Farmhands")]
        [ApiResponse(typeof(FarmhandResponse), 200, Description = "Farmhand deleted")]
        private async Task<FarmhandResponse> HandleDeleteFarmhandAsync(string? name, string? playerId)
        {
            var hasName = !string.IsNullOrEmpty(name);
            var hasId = !string.IsNullOrEmpty(playerId);

            if (!hasName && !hasId)
            {
                return new FarmhandResponse { Success = false, Error = "Missing 'name' or 'playerId' query parameter" };
            }
            if (hasName && hasId)
            {
                return new FarmhandResponse { Success = false, Error = "Provide either 'name' or 'playerId', not both" };
            }

            long parsedId = 0;
            if (hasId && !long.TryParse(playerId, out parsedId))
            {
                return new FarmhandResponse { Success = false, Error = $"Invalid 'playerId' value: '{playerId}'" };
            }

            if (Game1.gameMode != 3 || !Game1.IsServer)
            {
                return new FarmhandResponse { Success = false, Error = "Server not ready" };
            }

            if (Game1.game1.IsSaving)
            {
                return new FarmhandResponse { Success = false, Error = "Cannot delete farmhand while save is in progress" };
            }

            var lookupLabel = hasId ? $"id={parsedId}" : $"'{name}'";

            try
            {
                // Run the entire lookup + online check + deletion on the game thread.
                // Reading Game1.getAllFarmhands() / getOnlineFarmers() off-thread is racy:
                // the collections can be mid-update when a player is joining or leaving.
                FarmhandResponse? result = null;
                await RunOnGameThreadAsync(() =>
                {
                    Farmer? targetFarmhand = null;
                    foreach (var farmer in Game1.getAllFarmhands())
                    {
                        var matches = hasId
                            ? farmer.UniqueMultiplayerID == parsedId
                            : farmer.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true;
                        if (matches)
                        {
                            targetFarmhand = farmer;
                            break;
                        }
                    }

                    if (targetFarmhand == null)
                    {
                        result = new FarmhandResponse { Success = false, Error = $"Farmhand {lookupLabel} not found" };
                        return;
                    }

                    // Check if farmhand is online
                    if (Game1.getOnlineFarmers().Any(f => f.UniqueMultiplayerID == targetFarmhand.UniqueMultiplayerID))
                    {
                        result = new FarmhandResponse { Success = false, Error = $"Cannot delete farmhand {lookupLabel} - currently online" };
                        return;
                    }

                    var resolvedName = targetFarmhand.Name ?? string.Empty;
                    ExecuteFarmhandDeletion(targetFarmhand.UniqueMultiplayerID, resolvedName);
                    Monitor.Log($"Deleted farmhand '{ChatRedaction.MaskValue(resolvedName)}' (ID: {targetFarmhand.UniqueMultiplayerID})", LogLevel.Info);
                    result = new FarmhandResponse
                    {
                        Success = true,
                        Message = $"Farmhand '{resolvedName}' deleted successfully"
                    };
                }, timeoutMs: 15000);

                return result!;
            }
            catch (TaskCanceledException)
            {
                Monitor.Log($"Farmhand deletion timed out for {lookupLabel}", LogLevel.Error);
                return new FarmhandResponse { Success = false, Error = "Deletion timed out" };
            }
            catch (Exception ex)
            {
                Monitor.Log($"Error deleting farmhand: {ex}", LogLevel.Error);
                return new FarmhandResponse { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Executes the actual farmhand deletion on the main game thread.
        /// Called via RunOnGameThreadAsync from HandleDeleteFarmhandAsync.
        /// Exceptions are propagated back to the caller via the TaskCompletionSource.
        /// </summary>
        private void ExecuteFarmhandDeletion(long farmhandId, string farmhandName)
        {
            var farm = Game1.getFarm();

            // Find the cabin (farmhand may already be gone from farmhandData if deletion was queued multiple times)
            Cabin? targetCabin = null;
            Building? cabinBuilding = null;
            foreach (var building in farm.buildings)
            {
                if (!building.isCabin) continue;

                var cabin = building.GetIndoors<Cabin>();
                if (cabin?.owner?.UniqueMultiplayerID == farmhandId)
                {
                    targetCabin = cabin;
                    cabinBuilding = building;
                    break;
                }
            }

            // Centralized destruction: drops farmhand, removes building, fans out
            // stale homeLocation references in surviving farmhandData entries.
            if (targetCabin != null)
            {
                _cabinManager.DestroyCabin(cabinBuilding);
                Monitor.Log($"Destroyed cabin for farmhand '{ChatRedaction.MaskValue(farmhandName)}'", LogLevel.Debug);
            }
            else
            {
                // Fallback: farmhand exists but no cabin found.
                if (Game1.netWorldState.Value.farmhandData.FieldDict.ContainsKey(farmhandId))
                {
                    // Defense against netcode races: scan for any cabin still pointing
                    // at this farmhand via farmhandReference and null it before removal.
                    foreach (var building in farm.buildings)
                    {
                        if (!building.isCabin) continue;
                        var cabin = building.GetIndoors<Cabin>();
                        if (cabin != null && cabin.farmhandReference.defined.Value
                            && cabin.farmhandReference.uid.Value == farmhandId)
                        {
                            cabin.farmhandReference.Value = null;
                            Monitor.Log($"Cleared dangling farmhandReference on cabin '{cabin.NameOrUniqueName}' for farmhand '{ChatRedaction.MaskValue(farmhandName)}' (id={farmhandId})", LogLevel.Warn);
                        }
                    }

                    Monitor.Log($"No cabin found for farmhand '{ChatRedaction.MaskValue(farmhandName)}', removing from farmhandData directly", LogLevel.Warn);
                    Game1.netWorldState.Value.farmhandData.Remove(farmhandId);
                }
                else
                {
                    Monitor.Log($"Farmhand '{ChatRedaction.MaskValue(farmhandName)}' already deleted (no cabin or farmhandData found)", LogLevel.Debug);
                    return;
                }
            }

            // Remove from cabin manager tracking. PlayerCabinPositions shares the
            // farmhand's lifecycle, so clear its intent entry here too — otherwise a
            // deleted player's position record leaks into the save indefinitely.
            var removedEverJoined = _cabinManager.Data.AllPlayerIdsEverJoined.Remove(farmhandId);
            var removedPosition = _cabinManager.Data.PlayerCabinPositions.TryRemove(farmhandId, out _);
            if (removedEverJoined || removedPosition)
            {
                _cabinManager.Data.Write();
                Monitor.Log($"Removed farmhand from cabin tracking", LogLevel.Debug);
            }

            // Let cabin management create a fresh cabin with a new farmhand
            _cabinManager.EnsureAtLeastXCabins();
        }

        [ApiEndpoint("POST", "/newgame", Summary = "Create a new game with specified settings", Tag = "Server")]
        [ApiResponse(typeof(NewGameResponse), 200, Description = "New game created successfully")]
        private async Task HandlePostNewGameAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            // Parse the JSON request body
            NewGameRequest? body = null;
            try
            {
                using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
                var json = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    body = JsonConvert.DeserializeObject<NewGameRequest>(json);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to parse /newgame request body: {ex.Message}", LogLevel.Debug);
            }

            body ??= new NewGameRequest();

            // Validate no clients are connected
            int connectedClients = 0;
            try
            {
                await RunOnGameThreadAsync(() =>
                {
                    connectedClients = Game1.otherFarmers.Count;
                });
            }
            catch
            {
                // Game thread unavailable; allow the attempt anyway
            }

            if (connectedClients > 0)
            {
                response.StatusCode = 409;
                await WriteJsonAsync(response, new NewGameResponse
                {
                    Success = false,
                    Error = $"Cannot create new game while {connectedClients} client(s) are connected. Disconnect all players first."
                });
                return;
            }

            // Build the config from settings (defaults), overriding with any explicitly provided request values.
            var config = NewGameConfig.FromRequest(
                farmType: body.FarmType ?? _settings.FarmType,
                farmName: body.FarmName ?? _settings.FarmName,
                startingCabins: body.StartingCabins ?? _settings.StartingCabins,
                cabinStrategy: body.CabinStrategy ?? _settings.CabinStrategy.ToString(),
                maxPlayers: body.MaxPlayers ?? _settings.MaxPlayers,
                profitMargin: body.ProfitMargin ?? _settings.ProfitMargin,
                separateWallets: body.SeparateWallets ?? _settings.SeparateWallets
            );

            Monitor.Log($"[API] New game requested: {config}", LogLevel.Info);

            var gameManager = GameManagerService.Instance;
            if (gameManager == null)
            {
                response.StatusCode = 503;
                await WriteJsonAsync(response, new NewGameResponse
                {
                    Success = false,
                    Error = "Game manager not initialized yet"
                });
                return;
            }

            // Call RequestNewGame on the game thread. This sets flags and calls ExitToTitle().
            // Capture the returned Task which completes when the new game is fully created.
            Task? newGameTask = null;
            try
            {
                await RunOnGameThreadAsync(() =>
                {
                    newGameTask = gameManager.RequestNewGame(config);
                });
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                await WriteJsonAsync(response, new NewGameResponse
                {
                    Success = false,
                    Error = $"Failed to initiate new game: {ex.Message}"
                });
                return;
            }

            // Wait for the new game creation to complete (title screen → game creation → ready).
            // This spans the full ExitToTitle → CleanupReturningToTitle → TitleMenu → CreateNewGame cycle.
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                var completed = await Task.WhenAny(newGameTask!, timeoutTask);

                if (completed == newGameTask)
                {
                    await newGameTask!; // Propagate any exception from CreateNewGame
                    await WriteJsonAsync(response, new NewGameResponse
                    {
                        Success = true,
                        Message = $"New game created with farm type {config.WhichFarm}"
                    });
                    return;
                }

                // Timeout
                response.StatusCode = 504;
                await WriteJsonAsync(response, new NewGameResponse
                {
                    Success = false,
                    Error = "New game creation timed out (120s)"
                });
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                await WriteJsonAsync(response, new NewGameResponse
                {
                    Success = false,
                    Error = $"New game creation failed: {ex.Message}"
                });
            }
        }

        [ApiEndpoint("POST", "/reload", Summary = "Re-read server-settings.json and reload the active world (no restart)", Tag = "Server")]
        [ApiResponse(typeof(ReloadResponse), 200, Description = "World reloaded successfully")]
        private async Task HandlePostReloadAsync(HttpListenerResponse response)
        {
            // Validate no clients are connected — reload returns to title and would
            // disconnect everyone. Fail closed: if the count can't be read because the
            // game thread is busy, treat it as transient (503) rather than assuming 0
            // connected, which could silently disconnect active players if the thread
            // recovers before the reload fires.
            int connectedClients = 0;
            try
            {
                await RunOnGameThreadAsync(() =>
                {
                    connectedClients = Game1.otherFarmers.Count;
                });
            }
            catch (Exception ex)
            {
                Monitor.Log($"[API] Reload precheck could not read connected-client count: {ex.Message}", LogLevel.Warn);
                response.StatusCode = 503;
                await WriteJsonAsync(response, new ReloadResponse
                {
                    Success = false,
                    Error = "Cannot verify connected clients right now (game thread busy, likely a day transition or save sync). Retry after a few seconds."
                });
                return;
            }

            if (connectedClients > 0)
            {
                response.StatusCode = 409;
                await WriteJsonAsync(response, new ReloadResponse
                {
                    Success = false,
                    Error = $"Cannot reload while {connectedClients} client(s) are connected. Disconnect all players first."
                });
                return;
            }

            var gameManager = GameManagerService.Instance;
            if (gameManager == null)
            {
                response.StatusCode = 503;
                await WriteJsonAsync(response, new ReloadResponse
                {
                    Success = false,
                    Error = "Game manager not initialized yet"
                });
                return;
            }

            Monitor.Log("[API] World reload requested", LogLevel.Info);

            // Call RequestReloadSave on the game thread (sets flags + ExitToTitle).
            // The returned Task completes when the world has finished reloading.
            Task? reloadTask = null;
            try
            {
                await RunOnGameThreadAsync(() =>
                {
                    reloadTask = gameManager.RequestReloadSave();
                });
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                await WriteJsonAsync(response, new ReloadResponse
                {
                    Success = false,
                    Error = $"Failed to initiate reload: {ex.Message}"
                });
                return;
            }

            // Wait for the reload to complete (ExitToTitle → title screen → LoadSave → ready).
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                var completed = await Task.WhenAny(reloadTask!, timeoutTask);

                if (completed == reloadTask)
                {
                    await reloadTask!; // Propagate any exception from LoadSave
                    await WriteJsonAsync(response, new ReloadResponse
                    {
                        Success = true,
                        Message = "World reloaded"
                    });
                    return;
                }

                // Timeout
                response.StatusCode = 504;
                await WriteJsonAsync(response, new ReloadResponse
                {
                    Success = false,
                    Error = "World reload timed out (120s)"
                });
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                await WriteJsonAsync(response, new ReloadResponse
                {
                    Success = false,
                    Error = $"World reload failed: {ex.Message}"
                });
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
