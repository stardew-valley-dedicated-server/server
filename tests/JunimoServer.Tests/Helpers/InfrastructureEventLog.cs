using System.Text.Json;
using System.Text.Json.Nodes;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Structured event logger for infrastructure lifecycle events. Writes to
/// <c>{runDir}/diagnostics/infrastructure.jsonl</c>.
///
/// Envelope schema: see docs/developers/events-schema.md
///
/// Always on once <see cref="Initialize"/> is called. Overhead is negligible
/// (~1600-2000 events over a 6.5-minute run, microsecond writes per emit).
/// I/O errors propagate rather than being swallowed so a broken log surfaces
/// as an infra failure instead of silently losing diagnostics.
///
/// <para>
/// <b>Event catalog</b> (add to this list when introducing a new event type).
/// Forwarded events originate in one of three sidecar emitters and reach this
/// log via the <c>SDVD_EVENT </c> stdout prefix that
/// <see cref="Containers.SimpleContainerLogStreamer" /> forwards through
/// <see cref="ForwardRaw" />: <c>ModEventLog</c> in the server mod
/// (<c>service=server</c>), <c>ClientEventLog</c> in the test-client mod
/// (<c>service=test-client</c>), and <c>Logger.LogEvent</c> in the steam-auth
/// sidecar (<c>service=steam-auth</c>). Native emits from this class carry
/// <c>service=test-harness</c>.
/// <list type="bullet">
///
/// <item><b>HTTP traffic</b>:
/// <c>http_request</c> (client-side, TracingHandler: <c>clientKind, method, path,
/// status, durationMs, reqBytes?, respBytes?, snapshotAgeMs?,
/// predicateChangedMsAgo?, respSummary?, error?</c>) · <c>http_503_retry</c>
/// (ServerApiClient retried a 503) · <c>http_served</c> (mod, ApiService:
/// <c>method, path, status, durationMs</c>).</item>
///
/// <item><b>Polling &amp; failure context</b>:
/// <c>poll_completed</c> (<c>label, succeeded, iterations, durationMs, timeoutMs,
/// error?, ctCancelled, diagnostics?, onTimeoutError?</c>; emitted once per
/// outer wait in <c>finally</c> by <see cref="PollingHelper.WaitUntilAsync"/>;
/// observer-time — describes the polling loop's wall-clock duration) ·
/// <c>long_poll_completed</c> (<c>label, succeeded, iterations, durationMs,
/// timeoutMs, snapshotVersionAtMatch?, error?, ctCancelled, diagnostics?,
/// onTimeoutError?</c>; emitted once per outer wait by
/// <see cref="PollingHelper.LongPollAsync"/> and by bespoke long-poll outer
/// loops. <c>iterations</c> counts HTTP round-trips to <c>/wait/*</c> and
/// <c>durationMs</c> is wall-clock time across them — per-iteration semantics
/// differ from <c>poll_completed</c> because each round-trip blocks server-side
/// rather than firing tightly client-side. Observer-time, like
/// <c>poll_completed</c>) · <c>wait_matched</c> (<c>label,
/// predicateChangedMsAgo</c>; emitted alongside a successful
/// <c>poll_completed</c>/<c>long_poll_completed</c> when the matched HTTP
/// response carried <c>X-Predicate-Changed-At-Ms-Ago</c>. Envelope <c>ts</c>
/// and <c>runMs</c> are <i>producer-time</i>: the moment the predicate
/// first became satisfiable on the server's clock — sharper than the
/// snapshot's capture time, which is gated to the 1Hz publish cadence and
/// can lag the actual field-change tick by up to 1s. Use this — not the
/// observer-time <c>poll_completed</c> — to align log events with the
/// recorded video. <c>predicateChangedMsAgo</c> on the payload is the same
/// value subtracted from the observation time to produce the envelope's
/// <c>ts</c>/<c>runMs</c>, preserved for back-derivation. Absent for polls
/// whose matched response did not carry the header — predicate-shape
/// without an associated field-change time, or test-client-mod endpoints) ·
/// <c>failure_context</c> (<c>reason, extras?, serverState?, diagnosticsError?</c>).</item>
///
/// <item><b>Mod game-state snapshots</b>:
/// <c>peer_connected</c> (LobbyService) · <c>peer_disconnected</c> (LobbyService) ·
/// <c>peer_disconnected_engine</c> (mod, Harmony on Multiplayer.playerDisconnected) ·
/// <c>farmhand_request</c> (mod, Harmony on Multiplayer.addPlayer /
/// GameServer.rejectFarmhandRequest: <c>approved:bool, sourceFarmerId?, userId?,
/// connectionId?</c>) · <c>ready_check_transition</c> (mod, Harmony on
/// ServerReadyCheck.Update, state-diff only: <c>checkId, numberReady,
/// numberRequired, isReady</c>) · <c>otherfarmers_changed</c> (<c>added[], removed[],
/// total</c>) · <c>cabin_owner_changed</c> (mod, ApiService cabin-snapshot diff:
/// <c>tileX, tileY, oldOwnerId, newOwnerId, newOwnerName, newOwnerIsCustomized,
/// cabinType</c>) · <c>snapshot_error</c> (throttled 1/s per key) ·
/// <c>snapshot_skipped_newday</c> (latched once per Game1.newDay streak).</item>
///
/// <item><b>Mod lobby (LobbyService, auth-related)</b>:
/// <c>lobby_unauthenticated_registered</c> (<c>playerId, totalUnauthenticated</c>)
/// · <c>lobby_unauthenticated_unregistered</c> (<c>playerId,
/// remainingUnauthenticated</c>) · <c>lobby_warp_sent</c> (<c>playerId,
/// targetLocation, tileX, tileY</c>).</item>
///
/// <item><b>Mod cabin management (CabinManagerService)</b>:
/// <c>cabin_sync</c> (on SaveLoaded, <c>syncedCount, totalCabins, strategy</c>) ·
/// <c>cabin_peer_added</c> (on every peer-join; <c>firstTime</c> distinguishes
/// never-seen UMIs from returning ones: <c>playerId, firstTime,
/// totalEverJoined</c>) · <c>cabin_ensure_checked</c>
/// (per EnsureAtLeastXCabins call: <c>minRequired, availableCount, cabinsBuilt,
/// cabinsFailed, excludePeer, strategy</c>) · <c>cabin_build_failed</c>
/// (<c>hidden, tileX?, tileY?, reason</c>) · <c>cabin_destroyed</c> (<c>tileX,
/// tileY, indoorsName, ownerId, ownerName</c>) · <c>cabin_claim_abandoned</c>
/// (fired by <c>CabinManagerService</c> when an abandoned slot claim is released —
/// from the player-disconnect heal and from the save-load sweep:
/// <c>clearedUserId, ownerUniqueMultiplayerId</c>) ·
/// <c>cabin_claims_swept_on_load</c> (save-load sweep summary, emitted only when it
/// released at least one claim: <c>cleared</c>) · <c>cabin_strategy_migration</c>
/// (<c>fromStrategy, toStrategy, migrated, migrateFailed</c>) ·
/// <c>farmhand_references_cleaned</c> (save-load stale-ref cleanup:
/// <c>orphansRemoved, homeCleared, lastSleepCleared, validCabins</c>).</item>
///
/// <item><b>Mod auth flow (PasswordProtectionService)</b>:
/// <c>auth_login_attempted</c> (<c>playerId, result:"success"|"wrong_password"|
/// "already_authenticated"|"kicked_max_attempts"|"no_auth_data", failedAttempts</c>) ·
/// <c>auth_warped</c> (<c>playerId, location, tileX, tileY</c>) ·
/// <c>auth_warp_deferred</c> (<c>playerId, reason</c>; fires when auth
/// succeeds mid-day-transition) · <c>auth_warp_failed</c> (<c>playerId,
/// reason</c>; cabin-not-found etc.).</item>
///
/// <item><b>Mod Steam/Galaxy auth (AuthService)</b>:
/// <c>auth_server_steamid_received</c> (<c>steamId</c>) ·
/// <c>auth_steam_lobby_created</c> (<c>lobbyId, maxMembers</c>) ·
/// <c>auth_steam_lobby_create_failed</c> (<c>reason:"parse_failed"|
/// "empty_response"|"http_error", rawLobbyId?, exceptionType?, message?</c>) ·
/// <c>auth_steam_sidecar_ready</c> · <c>auth_steam_sidecar_unreachable</c>
/// (<c>exceptionType, message</c>) · <c>auth_galaxy_success</c> /
/// <c>auth_galaxy_failed</c> (<c>reason</c>) / <c>auth_galaxy_lost</c>
/// (<c>invocation, networkingSet</c>) · <c>auth_galaxy_state_change</c>
/// (<c>invocation, operationalState, signedIn, loggedOn, networkingSet</c>;
/// diagnostic — observes whether the Galaxy SDK re-fires after reconnect)
/// (each optionally carries <c>mode:"gameServer"</c>).</item>
///
/// <item><b>Mod role changes (RoleService)</b>:
/// <c>role_assigned</c> / <c>role_unassigned</c> (<c>playerId, role</c>).</item>
///
/// <item><b>Test-client game control</b>:
/// <c>client_chat_sent</c> (<c>message, length</c>; passwords in
/// <c>!login</c> are redacted as <c>f******t</c> — first and last character
/// kept, middle replaced with a fixed-size mask).</item>
///
/// <item><b>Mod watchdogs &amp; exception hygiene</b>:
/// <c>game_thread_stall_started</c> / <c>game_thread_stall_recovered</c> ·
/// <c>client_health_stall_started</c> / <c>client_health_stall_recovered</c> ·
/// <c>exception_swallowed</c> (<c>location, exceptionType, message</c>) ·
/// <c>console_command_invoked</c> (mod, AlwaysOn: <c>command, args, result,
/// error?, durationMs</c>).</item>
///
/// <item><b>Steam transport &amp; pool</b>:
/// <c>steam_p2p_connect_started</c> / <c>steam_p2p_connected</c> /
/// <c>steam_p2p_connect_failed</c> (mod, SteamGameServerNetServer:
/// <c>clientSteamId, reason?, relayedVia?, endReason?, debug?</c>) ·
/// <c>steam_callback_error</c> (mod, throttled: <c>callback, exceptionType,
/// message</c>) · <c>steam_session_lost</c> (mod, GameServer disconnect:
/// <c>result</c>) / <c>steam_session_connected</c> (first connect + every
/// auto-reconnect: <c>steamId, connectNumber, isReconnect</c>) ·
/// <c>steam_account_allocated</c> / <c>steam_account_released</c> /
/// <c>steam_account_pool_insufficient</c> (<c>kind:"server"|"client", index?,
/// remaining?, available?, totalSize?, inUse?</c>) ·
/// <c>steam_account_release_error</c> (ClientPool release path).</item>
///
/// <item><b>Docker / container lifecycle</b>:
/// <c>docker_preflight</c> (ContainerStatsCollector.InitializeAsync:
/// <c>daemonVersion, apiVersion, cpuCount, totalMemoryMb, operatingSystem,
/// kernelVersion, serverVersion</c>) · <c>docker_preflight_failed</c> ·
/// <c>container_started</c> (<c>role, name, image, index?|runId?, startupMs</c>) ·
/// <c>container_start_failed</c> (<c>role, name, image, index?|runId?,
/// elapsedMs, error</c>) · <c>container_stopped</c> (<c>role, name, index?|runId?,
/// preDisposeState, exitCode?, disposeDurationMs</c>) ·
/// <c>container_oom_killed</c> (<c>role, name, index?|runId?</c>) ·
/// <c>image_build_started</c> / <c>image_build_completed</c> /
/// <c>image_build_failed</c> (<c>image, durationMs?, reason?</c>).</item>
///
/// <item><b>Image distribution</b> (<c>ImageDistributor</c>; remote hosts only):
/// <c>image_transfer_started</c> (<c>host_id, attempt</c>) ·
/// <c>image_transfer_progress</c> (<c>host_id, attempt, bytesSent, elapsedMs</c>;
/// throttled to ~5 MB or 1 s) ·
/// <c>image_transfer_completed</c> (<c>host_id, attempt, bytesSent, elapsedMs</c>) ·
/// <c>image_transfer_failed</c> (<c>host_id, attempt, error</c>) ·
/// <c>image_skip_match</c> (<c>host_id</c>; emitted when every image's OCI
/// layer digests on the remote match the local source. Layer digests are
/// content-addressed and stable across daemon versions; ImageInspectResponse.ID
/// is daemon-local and not portable).</item>
///
/// <item><b>Game-data distribution</b> (<c>GameDataDistributor</c>; remote hosts only):
/// <c>game_data_transfer_started</c> (<c>host_id, attempt</c>) ·
/// <c>game_data_transfer_progress</c> (<c>host_id, attempt, bytesSent,
/// elapsedMs</c>; throttled to ~5 MB or 1 s) ·
/// <c>game_data_transfer_completed</c> (<c>host_id, bytesSent, elapsedMs</c>) ·
/// <c>game_data_transfer_failed</c> (<c>host_id, attempt, error</c>) ·
/// <c>game_data_skip_populated</c> (<c>host_id</c>; emitted when the remote
/// volume already contains the canonical StardewValley executable) ·
/// <c>helper_image_pull_started</c> (<c>host_id, image</c>) ·
/// <c>helper_image_pull_completed</c> (<c>host_id, image</c>) ·
/// <c>helper_image_pull_failed</c> (<c>host_id, image, error</c>).</item>
///
/// <item><b>SSH tunnel lifecycle</b> (<c>TunnelManager</c>; remote hosts only):
/// <c>ssh_preflight</c> (<c>sshPath, staleSocketsDeleted</c>; emitted once at
/// HostPool.PreflightAsync start when any host is remote) ·
/// <c>ssh_master_ready</c> (<c>host_id, controlPath, logPath, durationMs</c>;
/// <c>logPath</c> = the master's <c>-E</c> error log) ·
/// <c>ssh_master_spawn_failed</c> (<c>host_id, exitCode, stderr, durationMs</c>;
/// <c>stderr</c> falls back to the <c>-E</c> log tail when the parent pipe is
/// empty) · <c>ssh_master_check_failed</c> (<c>host_id, exitCode, stderr,
/// spawnStderr, durationMs</c>) · <c>ssh_master_exited</c> (<c>host_id, exitCode,
/// stderr?, durationMs</c>; per host during DrainAsync; <c>stderr</c> only when
/// <c>exitCode != 0</c>) · <c>ssh_master_log</c> (<c>host_id, logPath, byteLength,
/// tail</c>; emitted at teardown only when the <c>-E</c> log is non-empty —
/// carries the master's death line, e.g. "Timeout, server not responding.") ·
/// <c>tunnel_forward_opened</c> (<c>host_id, coordinator_port, mapped_port?,
/// remote_socket?, durationMs, attempts</c>) ·
/// <c>tunnel_forward_failed</c> (<c>host_id, coordinator_port?, mapped_port?,
/// remote_socket?, reason:"forward_failed"|"probe_timeout"|"cancelled"|
/// "port_collision_retry", message, attempt, attempts</c>; per attempt) ·
/// <c>tunnel_forward_closed</c> (<c>host_id, coordinator_port,
/// via:"dispose"|"drain", exitCode, stderr?, durationMs</c>; <c>stderr</c> only
/// when <c>exitCode != 0</c>) ·
/// <c>host_disconnected</c> (<c>host_id, reason, sshMasterLogTail?</c>; emitted by
/// <see cref="Infrastructure.DockerHost.Poison"/> — <c>sshMasterLogTail</c> present
/// only for transport-class poisons. <see cref="Infrastructure.HostPool.Place"/>
/// then filters the host and cascades a KeepConnected class).</item>
///
/// <item><b>Discovery &amp; setup</b>:
/// <c>config_discovery_completed</c> (once-per-process:
/// <c>configCount, configs[], durationMs</c>) · <c>run_aborted</c>
/// (<c>cause, exceptionType?, message?</c>) · <c>setup_ipc_read_deadline</c> /
/// <c>setup_ipc_oversized_line</c> (SetupPipeServer hardening) ·
/// <c>coordinator_address_resolved</c> (distributed runner only:
/// <c>ip, source:"loopback"|"auto-detected", probedHost?</c>).</item>
///
/// <item><b>Artifact capture failures</b>:
/// <c>screenshot_failed</c> (<c>source:"server"|"client", label, reason,
/// exceptionType?, message?, attempts?</c>).</item>
///
/// <item><b>Video recording lifecycle</b> (<see cref="ContainerRecorder"/>,
/// <see cref="RecordingOrchestrator"/>; every event carries <c>container</c> = recorder
/// display label). All time-valued fields (<c>*Sec</c>, <c>*Epoch</c>) are absolute
/// Unix-epoch seconds in container CLOCK_REALTIME — the recorder writes per-frame PTS
/// in this coordinate system, so the same scale flows through orchestrator marks,
/// ffmpeg seeks, and these diagnostic events. Inline <c>reason</c>/<c>stage</c>/
/// <c>via</c> strings are not enumerated here; grep the emitting class for the
/// definitive list.
/// <para>
/// <b>Recorder lifecycle</b> (<see cref="ContainerRecorder"/>):
/// <c>recording_started</c> (success path; payload fields are camelCase mirrors of
/// <see cref="ContainerRecorder"/>'s public properties — <c>startContainerEpoch</c>,
/// <c>firstFramePtsSource</c>, <c>hostToContainerOffsetMs</c> (= seconds × 1000),
/// <c>calibrationRttMs</c>, <c>calibrationSamples</c>, <c>clockOffsetFromCache</c> —
/// plus the phase-lock fields <c>phaseLockTargetEpoch</c> and
/// <c>phaseLockOvershootMs</c> documented at the emit site) ·
/// <c>recording_start_failed</c> (after 3 failed attempts) ·
/// <c>recording_stopped</c> (<c>via</c>, <c>remuxFixed?</c>) ·
/// <c>recording_container_dead</c> (one-shot when the underlying container is gone).
/// </para>
/// <para>
/// <b>Full-recording (per-container, at disposal)</b>:
/// <c>recording_full_converted</c> / <c>recording_full_convert_failed</c> ·
/// <c>recording_full_retrieved</c> / <c>recording_full_retrieve_failed</c>.
/// </para>
/// <para>
/// <b>Per-clip extraction</b>: <c>recording_clip_extracted</c> /
/// <c>recording_clip_failed</c> are emitted by the recorder for each extraction
/// attempt (success carries <c>actualFirstFramePts</c> = the seek-landing frame's
/// source wall-clock, which the orchestrator uses to compute content-aligned UI
/// offsets). <c>recording_per_test_clip</c> / <c>recording_per_test_clip_failed</c>
/// / <c>recording_per_test_clip_skipped</c> are the orchestrator-level summary
/// emitted once per (test, container) — see <see cref="RecordingOrchestrator.FinalizeAsync"/>
/// for the full field set. <c>recording_finalize_deferred_failed</c> is emitted
/// from the broker's deferred-finalize path (<c>TestArtifactCollector</c>) when
/// the orchestrator itself throws before reaching the per-clip loop.
/// Key fields: <c>timelineOffsetSec</c> drives UI clip
/// placement (content-aligned via <c>actualFirstFramePts</c> when available, else
/// mark-based fallback); <c>seekSnapMs</c> shows how far the realized landing frame
/// was from the requested mark (irreducible at <c>fps&gt;1</c>). The host↔container
/// clock offset that positions the seek is anchored per-clip via
/// <c>actualFirstFramePts</c> (read from segments.csv), so anchor regressions surface
/// as cross-clip burn-in misalignment in the clips themselves and via
/// <c>recording_started.phaseLockOvershootMs</c> — not via a per-clip finalize probe.
/// </para></item>
///
/// <item><b>Server health, poisoning, and lifecycle</b>:
/// <c>health.slow_tick</c> · <c>health.check_failed</c> · <c>health.check_error</c> ·
/// <c>health.poison</c> (<c>reason, reasonCode, consecutiveFailures</c>) ·
/// <c>server_created</c> · <c>server_creation_failed</c> ·
/// <c>server_released</c> (<c>server, instanceId, host_id, refCount,
/// remainingTests, exclusive</c>) ·
/// <c>server_evicted</c> · <c>server_replaced</c> · <c>server_replacement_failed</c> ·
/// <c>server_poisoned</c> (<c>server, instanceId, reason, reasonCode,
/// refCount</c>; reasonCode is one of
/// <see cref="Infrastructure.ManagedServer.PoisonReasonCode" />) ·
/// <c>server_disposed</c> (<c>server, instanceId, reason</c>; reason is free-form:
/// <c>no_demand_on_ready, poisoned_no_replacement, poisoned_replacing,
/// per_test_release, demand_exhausted, sibling_sweep, broker_shutdown</c>).</item>
///
/// <item><b>Capacity &amp; exclusivity (broker scheduler)</b>:
/// <c>capacity_acquired</c> · <c>capacity_released</c> · <c>pool_expansion</c> ·
/// <c>server_acquired</c> (<c>server, instanceId, host_id, refCount, exclusive</c>) ·
/// <c>exclusive_acquired</c> (<c>server, instanceId, test, refCount,
/// kind:"with_ref"|"gate_only", inheritedFromClass</c>) ·
/// <c>exclusive_released</c> (<c>server, instanceId,
/// kind:"ended"|"passed_to_same_class", ownerClass?, waiters?</c>) ·
/// <c>session_created</c> · <c>session_disposed</c> ·
/// <c>cancellation_detected</c> · <c>farmer_removal_waited</c>.</item>
///
/// <item><b>Client pool</b>:
/// <c>client_created</c> (<c>clientIndex, durationMs, reason:"prewarm"|"lease_demand"</c>) ·
/// <c>client_create_failed</c> ·
/// <c>client_acquired</c> (<c>clientIndex, instanceId, serverInstanceId?,
/// serverKey, steamAccountIndex</c>) ·
/// <c>client_returned</c> (<c>clientIndex, serverKey, poolAvailable</c>) ·
/// <c>client_discarded</c> · <c>client_marked_dead</c>.</item>
///
/// <item><b>Steam-auth sidecar (forwarded via SDVD_EVENT prefix)</b>:
/// <c>account_logged_in</c> · <c>account_login_failed</c> · <c>account_logged_off</c>.</item>
///
/// <item><b>Wait tracing</b> (<see cref="WaitTrace"/>, all blocking-wait
/// primitives in the test infrastructure):
/// <c>wait</c> (<c>name, phase:"started"|"completed"|"cancelled"|"failed",
/// durationMs?, snapshot?, snapshotError?, errorType?, errorMessage?</c>;
/// <c>name</c> is a <see cref="WaitName" /> variant).</item>
///
/// </list>
/// </para>
///
/// <para>
/// <b>Adding new events</b>: extend the catalog above, keep payload fields
/// stable once published, and prefer additive changes over renames. The wire
/// contract is documented in <c>docs/developers/events-schema.md</c>; the
/// failure runbook's <c>make test-events</c> target consumes this log and
/// relies on stable event names and field shapes.
/// </para>
/// </summary>
public static class InfrastructureEventLog
{
    private static AsyncJsonlWriter? _asyncWriter;
    private static string? _writerPath;
    private static readonly object _lock = new();

    /// <summary>
    /// Buffer for events emitted before <see cref="Initialize"/> has opened the
    /// log file. Pre-init events are stashed as their already-serialized JSON
    /// lines (so ts / requestId capture the true emit-time values),
    /// then flushed in order when <see cref="Initialize"/> runs. Prevents
    /// observability gaps caused by static-field initializer ordering races
    /// or by callers that legitimately need to emit during construction (e.g.
    /// <c>DockerImageBuilder</c> called via a <c>Lazy&lt;Task&gt;</c> factory
    /// that may execute on any thread).
    /// </summary>
    private static readonly Queue<string> _preInitBuffer = new();
    private static bool _preInitOverflowWarned;

    /// <summary>
    /// Hard cap on pre-Initialize events. A stuck test that never calls
    /// <see cref="Initialize"/> must not grow memory unboundedly; at the cap
    /// we drop additional events (with one stderr warning) rather than leak.
    /// </summary>
    private const int PreInitBufferCap = 1000;

    /// <summary>Exception types already reported on stderr from
    /// <see cref="ForwardRaw"/>. Used to emit the first failure of each
    /// type and suppress the rest.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, byte> _reportedForwardFailures = new();

    /// <summary>
    /// Opens the infrastructure log file. Called from
    /// <see cref="Infrastructure.TestResourceBroker.StartPrestart"/> after
    /// <see cref="RunMetadata.BeginRun"/> has set <see cref="TestArtifacts.RunDir"/>.
    ///
    /// <para>
    /// Any events emitted before this call have been buffered in-memory; they
    /// are flushed to the log, in order, before this method returns. Callers
    /// therefore never need to worry about emit-vs-initialize ordering — an
    /// <see cref="Emit"/> call anywhere in the process graph reaches the log
    /// as long as <see cref="Initialize"/> runs before <see cref="Shutdown"/>.
    /// </para>
    /// </summary>
    public static void Initialize() => Initialize(RunArtifactNames.InfrastructureJsonl);

    /// <summary>
    /// Initialize with an explicit filename. Used by parent processes
    /// (TestRunner / DistributedRunner) to write to <see cref="RunArtifactNames.ParentInfrastructureJsonl"/>
    /// so they don't race the test-child's truncating write to the canonical log.
    /// The merger concatenates the parent log into the canonical log at end of run.
    /// </summary>
    public static void Initialize(string fileName)
    {
        lock (_lock)
        {
            if (_asyncWriter != null) return;
            var path = Path.Combine(TestArtifacts.GetDiagnosticsDir(), fileName);
            // Disk preflight + consumer thread spawn happen inside Open. A failure
            // here (disk full, permission denied) throws synchronously and aborts
            // the run before any test starts, where the failure mode is loud.
            _asyncWriter = AsyncJsonlWriter.Open(path, $"infra:{fileName}", OnWriterFault);
            _writerPath = path;

            // Flush any events that were emitted before we opened the file. They
            // were already serialized on their own producer thread, so enqueueing
            // them now preserves the original arrival order.
            while (_preInitBuffer.Count > 0)
            {
                _asyncWriter.Enqueue(_preInitBuffer.Dequeue());
            }
        }
    }

    /// <summary>
    /// Hook fired when the async writer's consumer task faults (disk full,
    /// permission denied, IO error mid-run). Diagnostics aren't best-effort —
    /// a writer fault means we've lost failure context for whatever follows,
    /// exactly when we need it most. Signal shutdown so the runner translates
    /// this into a non-zero exit code.
    /// </summary>
    private static void OnWriterFault(Exception ex)
    {
        try
        {
            Console.Error.WriteLine(
                $"[InfrastructureEventLog] FATAL: writer fault ({ex.GetType().Name}: {ex.Message}); " +
                "aborting run.");
        }
        catch { /* stderr unavailable */ }
        try { ShutdownCoordinator.SignalShutdown(); } catch { /* already shutting down */ }
    }

    /// <summary>
    /// Awaits a write barrier — all events enqueued before this call are
    /// guaranteed to be on disk when the returned task completes. Callers that
    /// need their failure artifacts visible before announcing a test result
    /// (e.g. <c>TestBase.DisposeAsync</c> after a <c>failure_context</c> emit)
    /// use this rather than the heavier <see cref="Shutdown"/>.
    /// </summary>
    public static Task FlushAsync()
    {
        AsyncJsonlWriter? writer;
        lock (_lock) { writer = _asyncWriter; }
        return writer?.FlushAsync() ?? Task.CompletedTask;
    }

    /// <summary>
    /// Drains the writer's channel and flushes the underlying file. Used by
    /// the broker / runner shutdown paths where the next steps will tear down
    /// containers and we want all in-flight events on disk first.
    /// </summary>
    public static Task DrainAsync(TimeSpan timeout)
    {
        AsyncJsonlWriter? writer;
        lock (_lock) { writer = _asyncWriter; }
        return writer?.DrainAsync(timeout) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Producer-time override for the envelope's <c>ts</c>/<c>runMs</c>.
    /// Pass when the fact being emitted happened earlier than this call —
    /// e.g. when emitting that a server snapshot satisfied a predicate, the
    /// fact's time is the snapshot's capture instant, not the harness's
    /// observation instant. Both fields are paired so consumers reading
    /// either field see the same time.
    /// </summary>
    public readonly record struct EventTime(DateTime Ts, long RunMs);

    /// <summary>
    /// Writes a structured event line. Thread-safe via lock.
    /// <c>requestId</c> is attached from <see cref="CorrelationContext.Current"/>
    /// if set, so a logical operation (e.g. <c>JoinWorld(...)</c>) and all of
    /// its inner HTTP calls share one id for cross-log correlation.
    ///
    /// <para>
    /// <paramref name="eventTime"/>, when supplied, overrides the envelope's
    /// <c>ts</c> and <c>runMs</c>. Callers use this to attribute events to
    /// the producer's clock (e.g. the server snapshot's capture time) rather
    /// than the observation clock. When omitted, both fields are stamped at
    /// emit time — the correct choice for events the harness itself produces
    /// (<c>test_started</c>, <c>client_acquired</c>, <c>http_request</c>, …).
    /// </para>
    ///
    /// <para>
    /// If <see cref="Initialize"/> has not yet opened the log file, the event
    /// is buffered in-memory and flushed when <see cref="Initialize"/> runs.
    /// The buffer is capped at <see cref="PreInitBufferCap"/>; overflow drops
    /// events with a single stderr warning.
    /// </para>
    /// </summary>
    public static void Emit(string eventType, object? data = null, EventTime? eventTime = null)
    {
        var entry = new
        {
            ts = eventTime?.Ts ?? DateTime.UtcNow,
            runMs = eventTime?.RunMs ?? RunMetadata.GetRunMs(),
            requestId = CorrelationContext.Current,
            service = "test-harness",
            test = TestIdentityContext.Current,
            phase = TestIdentityContext.Phase,
            @event = eventType,
            data
        };
        var json = DiagnosticEmitJson.Serialize(entry);
        WriteSerialized(json, eventType);
    }

    /// <summary>
    /// Emits a structured <c>wait</c> event for an instrumented blocking-wait
    /// primitive. The <c>data.phase</c> field discriminates lifecycle
    /// (<c>started</c> / <c>completed</c> / <c>cancelled</c> / <c>failed</c>);
    /// envelope-level <c>phase</c> attribution comes from
    /// <see cref="TestIdentityContext.Phase"/>.
    /// </summary>
    public static void EmitWait(
        WaitName name, WaitPhase phase, long? durationMs,
        object? snapshot, string? snapshotError = null,
        string? errorType = null, string? errorMessage = null)
    {
        var data = new
        {
            name = name.ToString(),
            phase = phase.ToString().ToLowerInvariant(),
            durationMs,
            snapshot,
            snapshotError,
            errorType,
            errorMessage
        };
        Emit("wait", data);
    }

    /// <summary>
    /// Appends a forwarded <c>SDVD_EVENT</c> payload to the log, adding a
    /// top-level <c>forwardedVia</c> field that names the container of
    /// origin (e.g. <c>server-0</c>, <c>client-2</c>, <c>steam-auth-shared</c>).
    /// Origin envelope fields are preserved byte-for-byte. Malformed
    /// payloads are dropped; the first failure of each exception type is
    /// reported on <c>Console.Error</c>.
    /// </summary>
    public static void ForwardRaw(string jsonLine, string forwardedVia)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(jsonLine);
        }
        catch (JsonException ex)
        {
            ReportForwardFailure(forwardedVia, "parse", ex);
            return;
        }
        if (node is not JsonObject obj)
        {
            ReportForwardFailure(forwardedVia, "shape", new InvalidOperationException(
                $"SDVD_EVENT payload was valid JSON but not an object (got {node?.GetType().Name ?? "null"})"));
            return;
        }

        obj["forwardedVia"] = forwardedVia;
        var serialized = DiagnosticEmitJson.Serialize(obj);

        // Event name is only used for the pre-init overflow diagnostic;
        // a failure here must not block forwarding.
        string eventType = "<unknown>";
        try
        {
            if (obj["event"] is JsonValue ev && ev.TryGetValue(out string? name) && !string.IsNullOrEmpty(name))
                eventType = name;
        }
        catch (Exception ex)
        {
            ReportForwardFailure(forwardedVia, "event-name", ex);
        }

        WriteSerialized(serialized, eventType);
    }

    /// <summary>
    /// Reports a forwarding failure once per exception type on <c>Console.Error</c>.
    /// Must not re-enter the emitter — a broken sink would loop.
    /// </summary>
    private static void ReportForwardFailure(string forwardedVia, string stage, Exception ex)
    {
        try
        {
            if (_reportedForwardFailures.TryAdd(ex.GetType(), 0))
            {
                Console.Error.WriteLine(
                    $"[InfrastructureEventLog] ForwardRaw {stage} failed " +
                    $"({ex.GetType().Name}: {ex.Message}) from '{forwardedVia}'. " +
                    $"Further '{ex.GetType().Name}' failures will be silent.");
            }
        }
        catch
        {
            // stderr unavailable; drop the report.
        }
    }

    /// <summary>
    /// Single write path for both native <see cref="Emit"/> and forwarded
    /// <see cref="ForwardRaw"/> entries. The producer serializes on its own
    /// thread (preserving emit-time AsyncLocal context) and posts the rendered
    /// JSON line into the async writer's channel. Pre-Initialize, events are
    /// queued in-memory under the lock; the lock is contended only briefly per
    /// emit — the writer's drain runs on its own thread.
    /// </summary>
    private static void WriteSerialized(string json, string eventType)
    {
        // Fast path: writer is open. Take the lock just long enough to read
        // the writer reference. Enqueue is non-blocking (unbounded channel).
        AsyncJsonlWriter? writer;
        lock (_lock)
        {
            writer = _asyncWriter;
            if (writer == null)
            {
                if (_preInitBuffer.Count >= PreInitBufferCap)
                {
                    if (!_preInitOverflowWarned)
                    {
                        _preInitOverflowWarned = true;
                        Console.Error.WriteLine(
                            $"[InfrastructureEventLog] WARNING: pre-Initialize buffer full " +
                            $"({PreInitBufferCap} events); dropping further events until Initialize() runs. " +
                            $"First event type dropped: '{eventType}'. " +
                            "This usually means Initialize() was never called or an emit loop fired before the broker started.");
                    }
                    return;
                }
                _preInitBuffer.Enqueue(json);
                return;
            }
        }
        writer.Enqueue(json);
    }

    /// <summary>
    /// Flushes and closes the log. Called from
    /// <see cref="Infrastructure.TestResourceBroker.DisposeAsync"/>.
    ///
    /// <para>
    /// Post-close, samples the finalized file's size and prints a stderr warning
    /// if it exceeds <see cref="SoftSizeLimitBytes"/>. The limit is advisory —
    /// past the limit indicates a new event source has broken its dedup /
    /// throttle contract and needs investigation before landing. CI can fail
    /// on the presence of the warning line without parsing the log itself.
    /// </para>
    ///
    /// <para>
    /// If <see cref="Initialize"/> was never called (crash before pre-start),
    /// any events that were buffered in memory are dumped to stderr so they
    /// aren't silently lost.
    /// </para>
    /// </summary>
    public static void Shutdown() => ShutdownAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Async shutdown — drains the channel, flushes the underlying file, and
    /// emits the post-close size advisory. Preferred over <see cref="Shutdown"/>
    /// from async callers (test runner finally blocks) because it doesn't
    /// block a thread-pool thread on the consumer drain.
    /// </summary>
    public static async Task ShutdownAsync()
    {
        AsyncJsonlWriter? toDispose;
        lock (_lock)
        {
            if (_asyncWriter == null)
            {
                // Initialize was never called. Dump any buffered events to
                // stderr so they aren't silently lost — better than nothing
                // for post-mortem analysis of a run that failed before the
                // broker started.
                if (_preInitBuffer.Count > 0)
                {
                    Console.Error.WriteLine(
                        $"[InfrastructureEventLog] WARNING: Shutdown called without Initialize. " +
                        $"Dumping {_preInitBuffer.Count} buffered event(s) to stderr:");
                    while (_preInitBuffer.Count > 0)
                    {
                        Console.Error.WriteLine(_preInitBuffer.Dequeue());
                    }
                }
                return;
            }
            toDispose = _asyncWriter;
            _asyncWriter = null;
        }

        // DisposeAsync drains the channel (5s cap inside the writer), flushes
        // the underlying file, and disposes the stream.
        try { await toDispose.DisposeAsync(); }
        catch { /* best effort on shutdown */ }

        // Post-close: sample the file size and emit a warning if it exceeds
        // the advisory budget. Done outside the lock since we no longer hold
        // the writer, and the log is closed — no new events race with us.
        var sampledPath = _writerPath;
        _writerPath = null;
        try
        {
            var path = sampledPath ?? Path.Combine(TestArtifacts.GetDiagnosticsDir(), RunArtifactNames.InfrastructureJsonl);
            if (File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                if (size > SoftSizeLimitBytes)
                {
                    Console.Error.WriteLine(
                        $"[InfrastructureEventLog] WARNING: infrastructure.jsonl is {size / (1024 * 1024)} MB, " +
                        $"exceeds advisory limit of {SoftSizeLimitBytes / (1024 * 1024)} MB. " +
                        "A new emitter is likely missing dedup/throttle — investigate before shipping.");
                }
            }
        }
        catch
        {
            // Size check is advisory; failure to inspect is not a test failure.
        }
    }

    /// <summary>
    /// Advisory byte budget for <c>infrastructure.jsonl</c>. Current runs produce
    /// ~1–2 MB over 6.5 minutes; 20 MB leaves a 10× safety margin for richer
    /// correlation payloads. Exceeding this is a loud signal that a new emit
    /// category has broken its dedup/throttle contract.
    /// </summary>
    public const long SoftSizeLimitBytes = 20L * 1024 * 1024;
}

/// <summary>
/// Lifecycle phase of an instrumented wait. Serialized as
/// <c>phase.ToString().ToLowerInvariant()</c> in the <c>data.phase</c> field
/// of the <c>wait</c> event.
/// </summary>
public enum WaitPhase
{
    Started,
    Completed,
    Cancelled,
    Failed
}
