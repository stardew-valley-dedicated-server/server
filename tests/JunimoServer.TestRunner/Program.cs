using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using JunimoServer.TestRunner;
using JunimoServer.TestRunner.Distribution;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;
using JunimoServer.TestRunner.IPC;
using JunimoServer.TestRunner.Rendering;
using JunimoServer.TestRunner.Rendering.Web;
using JunimoServer.TestRunner.Setup;
using JunimoServer.TestRunner.Sinks;
using System.Diagnostics;
using System.Reflection;
using Xunit.SimpleRunner;

// Linear startup: parse SDVD_DOCKER_HOSTS, build HostPool (preflights all
// hosts, opening per-host SSH daemon-socket forwards via TunnelManager during
// preflight), distribute images to remote hosts, hand off to xUnit.

// Parse command-line arguments
var mode = OutputModeDetector.Detect(args);
var verbose = OutputModeDetector.IsVerbose(args);
var generateReport = OutputModeDetector.ShouldGenerateReport(args);
var filter = ParseFilter(args);

// Hook ProcessExit for emergency cleanup of game client / Docker containers
// when the process is terminated mid-run (Ctrl+C timeout, terminal close)
JunimoServer.Tests.Helpers.EmergencyCleanup.EnsureRegistered();

// Drain the parent's infrastructure event log before destructive Docker cleanup
// runs. Without this, run_aborted / final phase events sitting in the async
// writer's channel are lost when EmergencyCleanup.RunAll proceeds to bulk
// container removal.
JunimoServer.Tests.Helpers.EmergencyCleanup.RegisterDrainable(
    "infrastructure-event-log",
    () => new ValueTask(JunimoServer.Tests.Helpers.InfrastructureEventLog.DrainAsync(TimeSpan.FromSeconds(2))));

// Establish the run directory in the parent process so the parent and the xUnit
// child both write to the same artifact root. We propagate via SDVD_RUN_DIR; the
// child's TestSummaryFixture.InitializeAsync calls BeginRun which honors that env.
RunMetadata.BeginRun();
Environment.SetEnvironmentVariable(RunArtifactNames.RunDirEnv, TestArtifacts.RunDir);

// Propagate --filter to the xUnit child (out-of-process) so the broker's
// prestart, the assembly fixture's expected-count seed, and any other
// in-child consumer scope themselves to the actually-dispatched tests.
// Without this the child scans the full assembly and pre-provisions
// containers for tests xUnit will never run.
if (!string.IsNullOrEmpty(filter))
    Environment.SetEnvironmentVariable("SDVD_TEST_FILTER", filter);

// Open the structured-event log so parent-process emits land in
// {runDir}/diagnostics/infrastructure.parent.jsonl. We use a parent-specific
// filename to avoid racing the test-child's truncating write to the canonical
// infrastructure.jsonl; the merger / summary writer concatenates the two at
// end of run.
InfrastructureEventLog.Initialize(RunArtifactNames.ParentInfrastructureJsonl);

// Acquire the process-wide TunnelManager. Each remote forward is a per-process
// `ssh -N -L` background process; preflight opens the per-host daemon-socket
// forward and tests open per-container forwards on demand.
await using var tunnelManager = TunnelManager.Default;
var hostPool = HostPool.Instance;

// Construct the process-scope RunRecorder that owns TestRunState, the artifact
// writer, and the abort/finished flags. Seed it eagerly with the run dir / id
// so an early Ctrl+C (during preflight, image build, or game-data distribution
// — minutes of work on a cold remote host) can still write summary.json. The
// renderer never owns this state — the writer fires from Program.cs's outer
// finally regardless of which renderer mode is active.
var recorder = new RunRecorder();
recorder.SeedRunIdentity(TestArtifacts.RunDir, RunMetadata.RunId!);

// Create renderer based on mode, then wrap in a fault-isolation guard so a
// buggy renderer (state-merge bug, OOM, race) can't abort the run. After
// three consecutive throws the guard goes null-mode; the failure count
// surfaces in summary.json's degradation block. The renderer is constructed
// BEFORE preflight + image distribution so the web UI is visible during the
// (potentially long) setup phase, giving the operator quick visual feedback.
ITestRenderer baseRenderer = mode switch
{
    OutputMode.LLM => new LLMRenderer(recorder, verbose),
    OutputMode.Web => new WebRenderer(recorder),
    _ => new CIRenderer(verbose)
};

// Web mode is the only consumer of the broadcast callback: it pushes JSON to
// connected WebSocket clients. CI/LLM modes pass null — they don't broadcast
// state-mutation events live.
Action<string?>? broadcast = baseRenderer is WebRenderer wrInit ? wrInit.EnqueueEventNullable : null;
ITestRenderer renderer = new RendererDispatchGuard(baseRenderer, recorder, broadcast);

// Track cancellation for Ctrl+C and UI Stop.
// First abort: signal graceful cancellation, write summary.json directly (so an
// aborted run produces consumable artifacts even if disposal hangs on a stuck
// test method), then wait up to 15s for graceful disposal before force-killing.
// Second abort (or timeout): force-kill the process.
var abortCount = 0;
AssemblyRunner? activeRunner = null;
SetupPipeServer? setupPipeRef = null;

// Shared abort body. `cause` becomes the run_aborted event cause and the
// summary.json abortReason. Re-entrant: the first call starts the graceful
// teardown + 15s force-kill safety net; subsequent calls go straight to
// emergency cleanup + Environment.Exit(130). Used by Ctrl+C and UI Stop so
// both produce identical observable behavior.
void BeginAbort(string cause)
{
    var count = Interlocked.Increment(ref abortCount);
    if (count == 1)
    {
        JunimoServer.Tests.Fixtures.TestSummaryFixture.Instance?.SetAborted(cause);
        JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("run_aborted", new { cause });
        JunimoServer.Tests.Helpers.ShutdownCoordinator.SignalShutdown(); // Suppress infrastructure noise
        activeRunner?.Cancel(); // Signal xUnit to stop gracefully

        recorder.SetAbortReason(cause);

        // Drain the setup pipe with a tight cap so any messages already in
        // the kernel buffer (e.g. flaky_tests sitting unread when the child
        // hangs) land in TestRunState before we serialize. Bounded — a fully
        // hung child will return at the timeout, not block summary.json.
        try { setupPipeRef?.DrainAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult(); }
        catch { /* drain best-effort */ }

        // Write summary.json / ctrf-report.json / latest.txt directly here
        // rather than relying on the outer finally — runner.Run() may never
        // return if a test method is hung in a non-cooperative wait. The
        // writer's _written latch makes the duplicate call from the outer
        // finally a no-op on the graceful path.
        try { recorder.WriteRunArtifacts(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactWriter] abort-path write failed: {ex.Message}");
        }

        // Push a run_aborted event over the WebSocket so the browser UI flips
        // to the aborted state immediately. Without this, the UI only notices
        // the abort indirectly — after Kestrel tears down, the WS disconnects,
        // and the UI's reconnect retries all fail (`onReconnectFailed` →
        // `markAborted`). That takes several seconds while the user stares at
        // a spinning UI. The WS event lets the UI mark aborted in the same
        // tick the parent decided to abort.
        if (UnwrapRenderer(renderer) is WebRenderer webRenderer)
        {
            try
            {
                var abortEvt = DiagnosticEmitJson.Serialize(new
                {
                    Event = "run_aborted",
                    Timestamp = DateTime.UtcNow,
                    Cause = cause,
                });
                webRenderer.EnqueueEventNullable(abortEvt);
            }
            catch { /* best-effort; don't let a serialization slip block abort */ }

            // Signal WebRenderer to stop waiting for user review
            webRenderer.SignalShutdown();
        }

        // Wait for graceful disposal (recording extraction, log evacuation)
        // to complete, then force-kill as a safety net.
        //
        // ShutdownCoordinator.SignalGracefulComplete fires from the parent
        // process's finally block — it confirms the PARENT'S finally ran, NOT
        // that the xUnit child process has exited. The child is a separate
        // process spawned by AssemblyRunner; runner.Cancel() requests
        // cancellation but the child's exit is asynchronous to the parent.
        // Without an explicit KillTestChildren() here, the child survives the
        // parent's Environment.Exit and keeps streaming `docker logs` output
        // to the inherited stdout — visible as `[Client] ...` spam after the
        // shell prompt returns. (Same fix shape as ForceExitNow.)
        //
        // The bulk Docker sweep at the end of RunAll() is skipped when the
        // graceful drain succeeded — the parent's finally already ran
        // per-test EmergencyCleanup.Register cleanups, making the bulk sweep
        // the same redundant work the clean-exit path skips. Only when
        // WaitForGraceful TIMES OUT (drain stalled) do we need the
        // safety-net bulk sweep.
        var forceKillThread = new Thread(() =>
        {
            var drained = JunimoServer.Tests.Helpers.ShutdownCoordinator
                .WaitForGraceful(TimeSpan.FromSeconds(15));
            if (drained)
                JunimoServer.Tests.Helpers.EmergencyCleanup.SkipBulkSweepOnExit();
            // Kill the xUnit child (and its grandchildren: per-container
            // `ssh -N -L` forwards) so it cannot outlive the parent. Idempotent
            // on already-exited processes — safe to call even on the drained
            // path where the child should already be gone.
            KillTestChildren();
            JunimoServer.Tests.Helpers.EmergencyCleanup.RunAll();
            Environment.Exit(130);
        })
        {
            IsBackground = false,
            Name = "ForceKillTimeout"
        };
        forceKillThread.Start();
    }
    else
    {
        // Second abort signal: kill the xUnit child (and grandchildren),
        // then game client / containers, then force-exit immediately. Skips
        // the graceful window — the operator asked twice. KillTestChildren
        // must precede RunAll so the child can't recreate Docker resources
        // mid-sweep.
        JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit(
            "run_force_aborted", new { cause });
        KillTestChildren();
        JunimoServer.Tests.Helpers.EmergencyCleanup.RunAll();
        Environment.Exit(130);
    }
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Prevent immediate termination so cleanup runs
    BeginAbort("ctrl-c");
};

// Initialize renderer BEFORE accessing the test assembly type.
await renderer.InitializeAsync();

// UI Stop is a "nuke" button — bypasses xUnit's graceful cancel (which
// would wait for in-flight tests to finish their artifact + cleanup phases,
// 30+ s with several tests in flight) and the per-container EmergencyCleanup
// actions (sequential, ~3 s each). Goes straight to:
//   1. Emit run_aborted + run_force_aborted infrastructure events
//   2. Write summary.json (so post-mortem queries still work)
//   3. Bulk-remove all containers/networks/volumes labeled with this run-id
//      (one Docker API call per host, parallelized across hosts)
//   4. Environment.Exit(130)
// In-flight test recordings/screenshots are lost — accepted trade-off for
// snappy stop. The startup sweep on the next run picks up anything missed.
//
// Ctrl+C keeps its existing two-tier semantics (graceful + 15 s safety net,
// then force) — terminal users have a documented contract there.
// Force-stop entry point for UI clicks. The xUnit child runs as a separate
// process (`JunimoServer.Tests.exe` spawned by xunit.v3.runner.utility's
// LocalOutOfProcessTestProcessLauncher) — calling Environment.Exit on the
// parent alone leaves it orphaned. The child keeps running tests, keeps
// creating containers, keeps writing to the inherited stdout. Bulk-removing
// Docker resources from the parent fights an active producer: the child
// recreates them.
//
// CORRECT FIX:
//   1. Kill the xUnit child process(es) FIRST. Found via name match
//      ("JunimoServer.Tests") + start-time filter (started after us, so
//      definitely our descendant). Kill(entireProcessTree: true) on each
//      child cascades to any grandchildren (ssh -N -L per-container
//      forwards). Do NOT call Kill(true) on the current process — .NET
//      explicitly forbids self-tree kill (InvalidOperationException: "The
//      calling process is a member of the associated process's descendant
//      tree.") That was the silent bug in the previous attempt.
//   2. Now that there's no producer, bulk-remove Docker resources by
//      sdvd.run-id label across all hosts.
//   3. Drain parent's event log + write summary.json.
//   4. Environment.Exit(130).
void ForceExitNow(string cause)
{
    // Re-entrancy: if Ctrl+C / a prior Stop already started teardown, skip
    // straight to the kill — don't double-write summary / events.
    if (Interlocked.Increment(ref abortCount) != 1)
    {
        KillTestChildren();
        Environment.Exit(130);
        return;
    }

    try
    {
        // Kill the xUnit child first. This stops new container creation,
        // closes the child's inherited stdout (no more terminal log spam),
        // and frees the Docker resources to be removed by step below.
        KillTestChildren();

        try { JunimoServer.Tests.Fixtures.TestSummaryFixture.Instance?.SetAborted(cause); } catch { }
        try { JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("run_aborted", new { cause }); } catch { }
        try { JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("run_force_aborted", new { cause }); } catch { }
        try { JunimoServer.Tests.Helpers.ShutdownCoordinator.SignalShutdown(); } catch { }

        // Bulk-remove this run's Docker resources by sdvd.run-id label.
        // The child is dead; nothing recreates them. Bounded at 5 s overall.
        var bulkTask = Task.Run(() =>
        {
            try { JunimoServer.Tests.Helpers.EmergencyCleanup.BulkCleanupLabeledResources(); }
            catch { /* best effort */ }
        });
        try { bulkTask.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* best effort */ }

        // Drain the parent-side event log to disk (1 s) so run_aborted /
        // run_force_aborted survive the parent's exit.
        try { JunimoServer.Tests.Helpers.InfrastructureEventLog.DrainAsync(TimeSpan.FromSeconds(1)).Wait(TimeSpan.FromSeconds(1)); }
        catch { /* best effort */ }

        // summary.json — idempotent via the writer's _written latch.
        try { recorder.SetAbortReason(cause); } catch { }
        try { recorder.WriteRunArtifacts(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ArtifactWriter] force-exit write failed: {ex.Message}");
        }
    }
    finally
    {
        Environment.Exit(130);
    }
}

// Kill every JunimoServer.Tests process started after this parent (and
// therefore necessarily our descendant under xUnit's spawn pattern). Each
// kill cascades via Kill(entireProcessTree: true) so any per-container
// `ssh -N -L` grandchildren the child opened die with it.
//
// Filter rationale: process name is unique to this codebase. Start-time
// filter rules out the vanishingly rare case of an older sibling run on
// the same machine. We accept the residual risk of killing a concurrent
// sibling test run started after us — sequential dev runs are the norm,
// and the cost of false positives is far smaller than the cost of letting
// the orphan continue.
void KillTestChildren()
{
    DateTime ourStart;
    try { ourStart = Process.GetCurrentProcess().StartTime; }
    catch { return; }

    Process[] candidates;
    try { candidates = Process.GetProcessesByName("JunimoServer.Tests"); }
    catch { return; }

    foreach (var p in candidates)
    {
        try
        {
            if (p.HasExited) { p.Dispose(); continue; }
            if (p.StartTime < ourStart) { p.Dispose(); continue; }
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            p.Dispose();
        }
        catch { /* swallow per-candidate failures */ }
    }
}

// Wire WebRenderer's UI-Stop command into the nuke path. Off-loaded to a
// thread-pool task so the WS receive loop doesn't block on Docker cleanup.
if (UnwrapRenderer(renderer) is WebRenderer wr)
{
    wr.OnCommand(cmd =>
    {
        if (cmd == "stop")
            ForceExitNow("ui_stop");
    });
}

// Create the named pipe server now. The pipe slot is reserved for the xUnit
// child. Parent-side helpers (DockerImageBuilder via parentBuildProgress)
// route progress to the renderer in-process — they never touch SetupEventBus
// in this process, so there is no contention for the pipe connection.
// SDVD_SETUP_PIPE is exported just before xUnit dispatch.
await using var setupPipe = new SetupPipeServer(renderer);
// Expose the pipe to the Ctrl+C handler so the abort path can drain pending
// child-side messages (e.g. flaky_tests) into TestRunState before summary.json
// is written.
setupPipeRef = setupPipe;

// Now safe to access the test assembly
var testAssembly = typeof(JunimoServer.Tests.Infrastructure.TestBase).Assembly;
var testAssemblyPath = testAssembly.Location;

// Discover all tests via reflection and populate the tree upfront
var discoveredTests = DiscoverTestsViaReflection(testAssembly, filter);
renderer.PopulateTests(discoveredTests);

// Open browser AFTER the test tree is populated, so the initial WebSocket
// snapshot already contains all collections/classes/tests. The browser opens
// before preflight + image distribution so the operator gets quick visual
// feedback while a long remote-host transfer runs (~minutes for multi-GB
// images).
if (UnwrapRenderer(renderer) is WebRenderer webRendererReady)
    webRendererReady.OpenBrowser();

// Preflight + image distribution emit setup events directly to the renderer.
// (The parent process bypasses SetupEventBus's pipe channel — it owns the
// renderer in-process; SetupEventBus is for the child where IPC is needed.
// Existing pattern at RunnerCallbacks.cs:142-144.) The same events surface in
// the WebUI tree; the existing Console.Error lines stay for terminal users.
const string SetupCategory = "Runner";

renderer.OnSetupPhaseStarted(new SetupPhaseStartedEvent(SetupCategory, "Preflight"));
try
{
    using var preflightCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    foreach (var host in hostPool.Hosts)
    {
        // Detail is "remote"/"local" only — never host.SshDestination (the VPS
        // user@IP), which the CIRenderer prints to the public CI log.
        renderer.OnSetupStep(new SetupStepEvent(SetupCategory, $"Preflight {host.Id}",
            SetupStepStatus.Started, host.SshDestination != null ? "remote" : "local"));
    }
    await hostPool.PreflightAsync(tunnelManager, preflightCts.Token);
    foreach (var host in hostPool.Hosts)
    {
        renderer.OnSetupStep(new SetupStepEvent(SetupCategory, $"Preflight {host.Id}",
            SetupStepStatus.Completed));
    }
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Preflight", true));
}
catch (Exception ex)
{
    var preflightMsg = ScrubForLog(ex.Message);
    Console.Error.WriteLine($"[HostPool] Preflight failed: {preflightMsg}");
    InfrastructureEventLog.Emit("run_aborted", new { cause = "host_preflight", message = preflightMsg });
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Preflight", false, preflightMsg));
    recorder.SetAbortReason("preflight");
    recorder.WriteRunArtifacts();
    await renderer.DisposeAsync();
    return 2;
}

// Self-heal stray containers/networks/volumes from prior aborted runs across
// every configured host. Runs after preflight (so per-host API clients are up)
// and before image build (so a leftover named volume can't shadow a fresh one).
// Always-on, no flag — purely additive on a clean daemon.
renderer.OnSetupPhaseStarted(new SetupPhaseStartedEvent(SetupCategory, "Cleanup leftovers"));
try
{
    foreach (var host in hostPool.Hosts)
    {
        renderer.OnSetupStep(new SetupStepEvent(SetupCategory, $"Cleanup {host.Id}",
            SetupStepStatus.Started, host.SshDestination != null ? "remote" : "local"));
    }
    await foreach (var result in JunimoServer.Tests.Helpers.EmergencyCleanup
        .SweepStaleResourcesAsync(hostPool.Hosts))
    {
        if (result.Error != null)
        {
            renderer.OnSetupStep(new SetupStepEvent(SetupCategory, $"Cleanup {result.HostId}",
                SetupStepStatus.Warning, $"{result.Error.GetType().Name}: {ScrubForLog(result.Error.Message)}"));
        }
        else
        {
            var detail = result.TotalRemoved == 0
                ? "no leftovers"
                : $"removed {result.ContainersRemoved} containers, {result.NetworksRemoved} networks, {result.VolumesRemoved} volumes";
            renderer.OnSetupStep(new SetupStepEvent(SetupCategory, $"Cleanup {result.HostId}",
                SetupStepStatus.Completed, detail));
        }
    }
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Cleanup leftovers", true));
}
catch (Exception ex)
{
    var cleanupMsg = ScrubForLog(ex.Message);
    Console.Error.WriteLine($"[Cleanup] Stale-resource sweep failed: {cleanupMsg}");
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Cleanup leftovers", false, cleanupMsg));
    // Don't abort the run — sweep failure is recoverable; the run can still
    // execute against whatever leftover state exists, and process-exit cleanup
    // will mop up at the end.
}

// Build images locally BEFORE distribution so remote hosts receive a fresh
// build, not whatever stale image happened to be in the local daemon. The
// child's broker constructor would otherwise re-build after distribution,
// leaving local and remote with diverging image contents.
//
// DockerImageBuilder reports progress through the supplied sink. The
// renderer-direct sink delivers per-line build output to the parent's
// renderer immediately — no IPC hop, no nested phase wrapping. Category
// "Runner" matches the surrounding parent phases (Preflight, Cleanup
// leftovers, Image distribution, Game data distribution) and keeps the
// phase key namespaced separately from the child's "Setup" phases.
var parentBuildProgress = new RendererBuildProgressSink(renderer, SetupCategory);
try
{
    await JunimoServer.Tests.Helpers.DockerImageBuilder.EnsureImagesExistAsync(
        includeTestClient: true, parentBuildProgress);

    // Tell the child broker to skip its own build — the freshly-built images
    // we just produced are exactly what tests should run against. Without this,
    // the child would rebuild and could clobber the bytes we're about to push
    // to remote hosts.
    Environment.SetEnvironmentVariable("SDVD_SKIP_BUILD", "true");
}
catch (Exception ex)
{
    var buildMsg = ScrubForLog(ex.Message);
    Console.Error.WriteLine($"[ImageBuild] Parent-side build failed: {buildMsg}");
    InfrastructureEventLog.Emit("run_aborted", new { cause = "image_build", message = buildMsg });
    parentBuildProgress.PhaseCompleted("Docker Images", false, buildMsg);
    recorder.SetAbortReason("image_build");
    recorder.WriteRunArtifacts();
    await renderer.DisposeAsync();
    return 2;
}

// Distribute test images to remote hosts (no-op for local-only fleets).
// Runs after renderer is up so the UI is visible during the (potentially long)
// transfer. Failure exits cleanly via the renderer's Dispose path.
renderer.OnSetupPhaseStarted(new SetupPhaseStartedEvent(SetupCategory, "Image distribution"));
try
{
    var imageTag = TestEnvLoader.Get("SDVD_IMAGE_TAG") ?? "local";
    using var distributor = new ImageDistributor(imageTag, JunimoServer.Tests.Helpers.DockerImageBuilder.DistributableImageNames);
    var transferResults = await distributor.DistributeAsync(hostPool.Hosts, renderer);
    var transferFailures = transferResults.Where(r => !r.Success).ToList();
    if (transferFailures.Count > 0)
    {
        foreach (var f in transferFailures)
            Console.Error.WriteLine($"[ImageTransfer] host '{f.HostId}' failed: {f.Error ?? "unknown"}");
        InfrastructureEventLog.Emit("run_aborted", new { cause = "image_transfer", failures = transferFailures.Count });
        renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Image distribution",
            false, $"{transferFailures.Count} host(s) failed"));
        recorder.SetAbortReason("image_transfer");
        recorder.WriteRunArtifacts();
        await renderer.DisposeAsync();
        return 2;
    }
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Image distribution", true));
}
catch (Exception ex)
{
    var transferMsg = ScrubForLog(ex.Message);
    Console.Error.WriteLine($"[ImageTransfer] aborted: {transferMsg}");
    InfrastructureEventLog.Emit("run_aborted", new { cause = "image_transfer_exception", message = transferMsg });
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Image distribution", false, transferMsg));
    recorder.SetAbortReason("image_transfer_exception");
    recorder.WriteRunArtifacts();
    await renderer.DisposeAsync();
    return 2;
}

// Distribute the Stardew Valley game-data volume from the coordinator to each
// remote host. The server image's entrypoint blocks indefinitely on a missing
// game-data volume; on a fresh remote host the volume is empty until we
// populate it. Skips hosts where the volume already contains the canonical
// StardewValley executable, so re-runs on a provisioned remote are near-zero
// cost. Local hosts are no-ops — `make setup` populates them out-of-band.
renderer.OnSetupPhaseStarted(new SetupPhaseStartedEvent(SetupCategory, "Game data distribution"));
try
{
    var gameDataVolume = new JunimoServer.Tests.Containers.ServerContainerOptions().GameDataVolume;
    using var gameDataDistributor = new GameDataDistributor(gameDataVolume);
    var gdResults = await gameDataDistributor.DistributeAsync(hostPool.Hosts, renderer);
    var gdFailures = gdResults.Where(r => !r.Success).ToList();
    if (gdFailures.Count > 0)
    {
        foreach (var f in gdFailures)
            Console.Error.WriteLine($"[GameData] host '{f.HostId}' failed: {f.Error ?? "unknown"}");
        InfrastructureEventLog.Emit("run_aborted", new { cause = "game_data_transfer", failures = gdFailures.Count });
        renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Game data distribution",
            false, $"{gdFailures.Count} host(s) failed"));
        recorder.SetAbortReason("game_data_transfer");
        recorder.WriteRunArtifacts();
        await renderer.DisposeAsync();
        return 2;
    }
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Game data distribution", true));
}
catch (Exception ex)
{
    var gameDataMsg = ScrubForLog(ex.Message);
    Console.Error.WriteLine($"[GameData] aborted: {gameDataMsg}");
    InfrastructureEventLog.Emit("run_aborted", new { cause = "game_data_transfer_exception", message = gameDataMsg });
    renderer.OnSetupPhaseCompleted(new SetupPhaseCompletedEvent(SetupCategory, "Game data distribution", false, gameDataMsg));
    recorder.SetAbortReason("game_data_transfer_exception");
    recorder.WriteRunArtifacts();
    await renderer.DisposeAsync();
    return 2;
}

// Now that the parent-side build + distribution are complete, expose the
// setup pipe to the xUnit child so its SetupEventBus connects to this
// parent's renderer.
Environment.SetEnvironmentVariable("SDVD_SETUP_PIPE", setupPipe.PipeName);

int exitCode = 0;

try
{
    var callbacks = new RunnerCallbacks(renderer);

    // Enable collection-level parallelism (default: true) unless:
    // - SDVD_HOST_CLIENT=true (single shared host process, can't parallelize)
    // - SDVD_PARALLEL=false (explicit opt-out)
    var hostClient = string.Equals(
        Environment.GetEnvironmentVariable("SDVD_HOST_CLIENT"),
        "true", StringComparison.OrdinalIgnoreCase);
    var parallel = !hostClient && !string.Equals(
        Environment.GetEnvironmentVariable("SDVD_PARALLEL"),
        "false", StringComparison.OrdinalIgnoreCase);

    // SDVD_STOP_ON_FAIL: defaults to true (stop on first failure).
    var stopOnFail = !string.Equals(
        Environment.GetEnvironmentVariable("SDVD_STOP_ON_FAIL"),
        "false", StringComparison.OrdinalIgnoreCase);

    var options = new AssemblyRunnerOptions(testAssemblyPath)
    {
        ParallelizeTestCollections = parallel,
        StopOnFail = stopOnFail,
        OnDiagnosticMessage = callbacks.OnDiagnosticMessage,
        OnDiscoveryComplete = callbacks.OnDiscoveryComplete,
        OnExecutionComplete = info =>
        {
            callbacks.OnExecutionComplete(info);
            exitCode = info.TestsFailed > 0 ? 1 : (info.TotalErrors > 0 ? 2 : 0);
        },
        OnErrorMessage = callbacks.OnErrorMessage,
        OnTestStarting = callbacks.OnTestStarting,
        OnTestPassed = callbacks.OnTestPassed,
        OnTestFailed = callbacks.OnTestFailed,
        OnTestSkipped = callbacks.OnTestSkipped,
        // xUnit "not run" tests (filter mismatch and similar) are recorded as
        // skipped in artifacts. See RunnerCallbacks.OnTestNotRun.
        OnTestNotRun = callbacks.OnTestNotRun,
        // Generic per-test completion callback that fires for every test
        // regardless of outcome. Acts as a safety net when xUnit's typed
        // callback (Passed/Failed/Skipped/NotRun) silently fails to fire —
        // observed for ~3 tests/run under heavy queue contention. The
        // generic handler runs AFTER any specific handler per xunit docs;
        // RunnerCallbacks dedupes via TestDisplayName so a typed callback
        // followed by the generic doesn't double-dispatch.
        OnTestFinished = callbacks.OnTestFinished,
    };

    // Apply filter if specified
    if (!string.IsNullOrEmpty(filter))
    {
        // Use method filter which matches against the fully-qualified test name
        options.Filters.AddIncludedMethodFilter($"*{filter}*");
    }

    // Notify run started
    renderer.OnRunStarted(new RunStartedEvent(testAssemblyPath, discoveredTests.Count));

    // Run tests
    await using (var runner = new AssemblyRunner(options))
    {
        activeRunner = runner;
        await runner.Run();
    }
}
catch (Exception ex)
{
    var runMsg = ScrubForLog(ex.Message);
    JunimoServer.Tests.Fixtures.TestSummaryFixture.Instance?.SetAborted(runMsg);
    JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("run_aborted", new
    {
        cause = "exception",
        exceptionType = ex.GetType().Name,
        message = runMsg
    });
    recorder.SetAbortReason("exception");
    throw;
}
finally
{
    // Drain the setup pipe before writing artifacts. The child process exits
    // after TestSummaryFixture.DisposeAsync emits flaky_tests; that message
    // may still be in the kernel pipe buffer when runner.Run() returns. Drain
    // awaits the natural EOF (child closes the write end on exit), so all
    // dispatched events are folded into TestRunState before the artifact
    // writer projects it. (drain-before-consume-disposal.md)
    try { await setupPipe.DrainAsync(TimeSpan.FromSeconds(5)); }
    catch { /* never block disposal on a pipe drain failure */ }

    // Drain SSH tunnels before the renderer disposes — every per-container
    // forward closed in parallel with bounded concurrency, then masters
    // exited. drain-before-consume-disposal.md applied to TunnelManager.
    try { await tunnelManager.DrainAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)); }
    catch { /* drain best effort */ }

    // Write the durable run artifacts BEFORE renderer disposal so a throw
    // from the renderer's Dispose can't poison the artifact write. Idempotent
    // via the writer's _written latch — a setup-phase failure / Ctrl+C /
    // ForceExitNow that already wrote skips this call as a no-op.
    try { recorder.WriteRunArtifacts(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ArtifactWriter] finally-path write failed: {ex.Message}");
    }

    // Assemble the offline test-web bundle (SPA + run snapshot + copied media)
    // inside the run dir, so it rides along in the uploaded artifact tree and
    // opens over file://. One site, every renderer mode — driven by --report or
    // CI. No-ops with a warning when the SPA hasn't been built. Requires the
    // final state, so it runs after WriteRunArtifacts.
    if (generateReport || IsCIEnvironment())
        ReportGenerator.TryGenerate(recorder.State, TestArtifacts.RunDir, CollectKnownSecrets());

    // Dev-mode Web runs that completed normally: hold the browser open
    // until the operator presses a key (or signals shutdown). Skipped on
    // CI, on aborted runs, and on non-Web modes. The wait is here — after
    // artifact write, before renderer disposal — so the browser stays live
    // and /api/state continues to serve while waiting.
    if (baseRenderer is WebRenderer webRendererFinish
        && recorder.IsRunFinished
        && !IsCIEnvironment())
    {
        try { await webRendererFinish.WaitForKeypressOrShutdownAsync(); }
        catch { /* keypress wait is best-effort */ }
    }

    // Wrap renderer disposal so a thrown DisposeAsync can't skip the
    // remaining shutdown steps (log drain, graceful signal).
    try { await renderer.DisposeAsync(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Renderer] dispose failed: {ex.Message}");
    }

    // Close the parent-process log AFTER the renderer disposes (which may have
    // emitted late events). The async variant awaits the writer's channel drain
    // without blocking a thread-pool thread.
    await InfrastructureEventLog.ShutdownAsync();

    JunimoServer.Tests.Helpers.ShutdownCoordinator.SignalGracefulComplete();

    // Clean exit only: skip the EmergencyCleanup bulk Docker sweep on
    // ProcessExit. Per-test DisposeAsyncs already cleaned everything; a
    // second list+remove pass per host adds 5–10s for no gain. Abort paths
    // (Ctrl+C, UI Stop, force-exit) intentionally never set this — they
    // need the safety net because in-flight DisposeAsyncs were cancelled.
    if (abortCount == 0)
        JunimoServer.Tests.Helpers.EmergencyCleanup.SkipBulkSweepOnExit();

    if (abortCount > 0)
        exitCode = 130; // Standard exit code for Ctrl+C / UI Stop
}

return exitCode;

static bool IsCIEnvironment()
    => Environment.GetEnvironmentVariable("CI") != null
       || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null
       || Environment.GetEnvironmentVariable("TF_BUILD") != null;

/// <summary>
/// Masks secrets/infra out of a free-text setup error before it reaches the CI log —
/// preflight/transfer exceptions carry SSH stderr that can embed the VPS host/IP. Delegates
/// to the same <see cref="ReportRedactor"/> + <see cref="CollectKnownSecrets"/> used for the
/// published report, so masking is consistent everywhere (e.g. <c>***.***.***.188</c>).
/// </summary>
static string ScrubForLog(string message)
    => ReportRedactor.Scrub(message, CollectKnownSecrets());

/// <summary>
/// Collects the sensitive values the runner knows, for the report redactor to mask out
/// of the published (public) snapshot: Steam credentials from <c>STEAM_ACCOUNTS</c> and
/// each remote host's <c>user@host</c> + bare host from <see cref="HostPool"/>. Best-effort
/// — any failure yields an empty set (the redactor's regex pass still runs).
/// </summary>
static IReadOnlyCollection<string> CollectKnownSecrets()
{
    var secrets = new HashSet<string>(StringComparer.Ordinal);

    try
    {
        var accountsJson = Environment.GetEnvironmentVariable("STEAM_ACCOUNTS");
        foreach (var account in JunimoServer.Tests.Schema.Json.UserConfigJson.ParseArrayStrict("STEAM_ACCOUNTS", accountsJson))
        {
            foreach (var field in new[] { "user", "pass", "refreshToken" })
                if (account?[field]?.GetValue<string>() is { Length: > 0 } v)
                    secrets.Add(v);
        }
    }
    catch { /* malformed STEAM_ACCOUNTS — rely on regex + host values */ }

    try
    {
        foreach (var host in HostPool.Instance.Hosts)
        {
            if (string.IsNullOrEmpty(host.SshDestination)) continue;
            secrets.Add(host.SshDestination);                       // user@host
            var at = host.SshDestination.IndexOf('@');
            if (at >= 0 && at < host.SshDestination.Length - 1)
                secrets.Add(host.SshDestination[(at + 1)..]);       // bare host / IP
        }
    }
    catch { /* host pool unavailable — rely on regex + steam values */ }

    return secrets;
}

/// <summary>
/// Unwrap a <see cref="RendererDispatchGuard"/> to its inner renderer for type
/// checks (e.g. casting to <see cref="WebRenderer"/>). Returns the input as-is
/// when not wrapped.
/// </summary>
static ITestRenderer UnwrapRenderer(ITestRenderer renderer)
    => renderer is RendererDispatchGuard guard ? guard.Inner : renderer;

/// <summary>
/// Parse --filter argument from command line. Returns null if no filter specified.
/// </summary>
static string? ParseFilter(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--filter", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    foreach (var arg in args)
    {
        if (arg.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            return arg[9..];
    }
    return null;
}

/// <summary>
/// Discover all test methods in the assembly using reflection.
/// Returns (Collection, ClassName, MethodName, DisplayName) tuples.
///
/// [Theory] methods with [InlineData] or [MemberData] are expanded into individual
/// entries matching xUnit's runtime display names. If expansion fails, the method
/// falls back to a single entry per method.
/// </summary>
static List<(string Collection, string ClassName, string MethodName, string DisplayName)> DiscoverTestsViaReflection(Assembly assembly, string? filter)
{
    var tests = new List<(string, string, string, string)>();

    var factType = Type.GetType("Xunit.FactAttribute, xunit.v3.core")
                   ?? Type.GetType("Xunit.FactAttribute, xunit.core");
    var theoryType = Type.GetType("Xunit.TheoryAttribute, xunit.v3.core")
                     ?? Type.GetType("Xunit.TheoryAttribute, xunit.core");
    var collectionType = Type.GetType("Xunit.CollectionAttribute, xunit.v3.core")
                         ?? Type.GetType("Xunit.CollectionAttribute, xunit.core");

    if (factType == null && theoryType == null)
        return tests;

    foreach (var type in assembly.GetTypes())
    {
        if (!type.IsClass || type.IsAbstract || !type.IsPublic)
            continue;

        var collectionAttr = type.GetCustomAttribute(collectionType!);
        var collectionName = collectionAttr?.GetType().GetProperty("Name")?.GetValue(collectionAttr)?.ToString()
                             ?? $"Test collection for {type.Name}";

        var className = type.Name;
        var classFullName = type.FullName ?? type.Name;

        var classMatchesFilter = string.IsNullOrEmpty(filter) ||
            classFullName.Contains(filter, StringComparison.OrdinalIgnoreCase);

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var hasFact = factType != null && method.GetCustomAttribute(factType) != null;
            var hasTheory = theoryType != null && method.GetCustomAttribute(theoryType) != null;

            if (hasFact || hasTheory)
            {
                var methodName = method.Name;

                if (hasTheory)
                {
                    var expanded = ExpandTheoryDataRows(method, classFullName);
                    if (expanded.Count > 0)
                    {
                        foreach (var theoryDisplayName in expanded)
                        {
                            if (!classMatchesFilter &&
                                !theoryDisplayName.Contains(filter!, StringComparison.OrdinalIgnoreCase))
                                continue;
                            tests.Add((collectionName, className, methodName, theoryDisplayName));
                        }
                        continue;
                    }
                }

                var displayName = $"{classFullName}.{methodName}";
                if (!classMatchesFilter && !displayName.Contains(filter!, StringComparison.OrdinalIgnoreCase))
                    continue;
                tests.Add((collectionName, className, methodName, displayName));
            }
        }
    }

    return tests;
}

/// <summary>
/// Expand [InlineData] and [MemberData] attributes on a Theory method into individual
/// display names matching xUnit's runtime format.
/// Returns empty list on failure; caller falls back to single method entry.
/// </summary>
static List<string> ExpandTheoryDataRows(MethodInfo method, string classFullName)
{
    var results = new List<string>();
    var parameters = method.GetParameters();

    try
    {
        foreach (var attrData in method.GetCustomAttributesData())
        {
            if (attrData.AttributeType.Name == "InlineDataAttribute")
            {
                if (attrData.ConstructorArguments.Count > 0 &&
                    attrData.ConstructorArguments[0].Value is IReadOnlyCollection<CustomAttributeTypedArgument> items)
                {
                    var values = items.Select(a => a.Value).ToArray();
                    results.Add(FormatTheoryDisplayName(classFullName, method.Name, parameters, values));
                }
            }
        }

        var memberDataType = Type.GetType("Xunit.MemberDataAttribute, xunit.v3.core")
                             ?? Type.GetType("Xunit.MemberDataAttribute, xunit.core");
        if (memberDataType != null)
        {
            foreach (var attr in method.GetCustomAttributes(memberDataType))
            {
                var memberName = (string?)attr.GetType().GetProperty("MemberName")?.GetValue(attr);
                if (string.IsNullOrEmpty(memberName)) continue;

                var declaringType = method.DeclaringType!;
                var prop = declaringType.GetProperty(memberName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (prop?.GetValue(null) is IEnumerable<object[]> dataRows)
                {
                    foreach (var row in dataRows)
                        results.Add(FormatTheoryDisplayName(classFullName, method.Name, parameters, row));
                }
            }
        }
    }
    catch
    {
        return new List<string>();
    }

    return results;
}

/// <summary>
/// Format a Theory display name matching xUnit's runtime format:
/// "Namespace.Class.Method(paramName: value1, paramName: value2)"
/// </summary>
static string FormatTheoryDisplayName(string classFullName, string methodName,
    ParameterInfo[] parameters, object?[] data)
{
    var args = new List<string>();
    for (var i = 0; i < parameters.Length && i < data.Length; i++)
    {
        var value = data[i];
        var formatted = value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            _ => value.ToString() ?? "null"
        };
        args.Add($"{parameters[i].Name}: {formatted}");
    }
    return $"{classFullName}.{methodName}({string.Join(", ", args)})";
}
