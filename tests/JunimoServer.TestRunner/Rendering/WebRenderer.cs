using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using JunimoServer.TestRunner.Rendering.Web;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace JunimoServer.TestRunner.Rendering;

/// <summary>
/// Web-based test renderer. Serves a Vue SPA via embedded Kestrel with WebSocket push
/// for live event streaming. State mutation, abort-reason, and run-level artifact
/// writing live on <see cref="RunRecorder"/>; this renderer reads the shared state
/// for HTTP/WS snapshot endpoints and receives broadcast JSON via
/// <see cref="EnqueueEventNullable"/>, wired in by <see cref="RendererDispatchGuard"/>.
/// </summary>
public sealed class WebRenderer : RendererBase
{
    private readonly RunRecorder _recorder;
    private readonly TestRunState _state;
    private readonly Channel<string> _eventChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }
    );

    private readonly ConcurrentDictionary<string, WebSocket> _wsClients = new();
    private readonly TaskCompletionSource _shutdownSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly CancellationTokenSource _connectionsCts = new();

    private WebApplication? _app;
    private Task? _broadcastTask;
    private string? _serverUrl;

    // Single-consumer command callback. "stop" needs runner cancellation,
    // which only Program.cs holds a handle to, so it registers via OnCommand.
    // A second registration replaces the first — switch to event semantics if
    // a chaining use case appears.
    private Action<string>? _onCommand;

    public void OnCommand(Action<string> handler) => _onCommand = handler;

    public WebRenderer(RunRecorder recorder)
        : base(verbose: true)
    {
        _recorder = recorder;
        _state = recorder.State;
    }

    /// <summary>
    /// Callback target wired into <see cref="RendererDispatchGuard"/>'s
    /// broadcast slot. Receives the JSON produced by each <c>State.ApplyX</c>
    /// call and enqueues it for the live WebSocket broadcast loop. Null
    /// arguments (some Apply methods filter the event out) are dropped.
    /// </summary>
    public void EnqueueEventNullable(string? json)
    {
        if (json != null)
        {
            _eventChannel.Writer.TryWrite(json);
        }
    }

    public override async ValueTask InitializeAsync()
    {
        var spaDistPath = ReportGenerator.FindSpaProjectPath() is { } proj
            ? Path.Combine(proj, "dist")
            : null;

        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { Args = Array.Empty<string>() }
        );

        // Suppress noisy Kestrel logs
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        // Use port 0 for auto-assignment
        builder.Configuration["Urls"] = "http://127.0.0.1:0";

        _app = builder.Build();

        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // Serve SPA static files if dist directory exists
        if (spaDistPath != null && Directory.Exists(spaDistPath))
        {
            var fileProvider = new PhysicalFileProvider(spaDistPath);
            _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
            _app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
        }

        // API endpoints
        _app.MapGet(
            "/api/state",
            () => Results.Content(_state.ToSnapshotJson(), "application/json")
        );

        // Command channel — REST fallback when the WebSocket is mid-reconnect.
        // Accepts the same {"cmd":"stop"} payload as the WS path.
        _app.MapPost(
            "/api/command",
            async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                TryHandleCommand(body);
                return Results.Ok();
            }
        );

        // Artifact serving with directory traversal protection
        _app.MapGet(
            "/artifacts/{**path}",
            (string path) =>
            {
                var testResultsDir = Path.GetFullPath("TestResults");
                var resolvedPath = Path.GetFullPath(Path.Combine(testResultsDir, path));

                if (
                    !resolvedPath.StartsWith(testResultsDir + Path.DirectorySeparatorChar)
                    && resolvedPath != testResultsDir
                )
                {
                    return Results.NotFound();
                }

                if (!File.Exists(resolvedPath))
                {
                    return Results.NotFound();
                }

                var contentType = Path.GetExtension(resolvedPath).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".mp4" => "video/mp4",
                    ".mkv" => "video/x-matroska",
                    ".webm" => "video/webm",
                    ".json" => "application/json",
                    ".txt" or ".log" => "text/plain",
                    _ => "application/octet-stream",
                };

                return Results.File(resolvedPath, contentType);
            }
        );

        // WebSocket endpoint
        _app.Map(
            "/ws",
            async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var ws = await context.WebSockets.AcceptWebSocketAsync();
                var connectionId = Guid.NewGuid().ToString("N");
                Console.Error.WriteLine($"[WebUI] Client connected: {connectionId}");

                try
                {
                    // Send full state snapshot BEFORE registering with BroadcastLoop.
                    // This prevents a race where BroadcastLoop sends a regular event
                    // before the snapshot. The client treats the first message as the
                    // snapshot, so receiving an event first would corrupt hydration.
                    var snapshot = _state.ToSnapshotJson();
                    await SendToSocket(ws, snapshot);

                    // Now register for live event broadcast
                    _wsClients[connectionId] = ws;

                    // Read until close. Dead-connection detection is handled at the
                    // protocol layer by KeepAliveInterval (server→client pings); a
                    // broken transport surfaces as WebSocketException from ReceiveAsync.
                    var buffer = new byte[1024];
                    while (ws.State == WebSocketState.Open)
                    {
                        var result = await ws.ReceiveAsync(buffer, _connectionsCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        {
                            // Commands are tiny ({"cmd":"stop"} ≈ 15 bytes). Drop
                            // fragmented frames — the only client (sendCommand)
                            // sends single-frame text.
                            if (result.EndOfMessage)
                            {
                                var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                TryHandleCommand(msg);
                            }
                        }
                    }
                }
                catch (WebSocketException)
                {
                    // Client disconnected
                }
                catch (OperationCanceledException)
                {
                    // Server shutting down (_connectionsCts canceled)
                }
                finally
                {
                    _wsClients.TryRemove(connectionId, out _);
                    Console.Error.WriteLine($"[WebUI] Client disconnected: {connectionId}");
                    if (ws.State != WebSocketState.Closed && ws.State != WebSocketState.Aborted)
                    {
                        try
                        {
                            await ws.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                null,
                                CancellationToken.None
                            );
                        }
                        catch
                        { /* ignore */
                        }
                    }
                    ws.Dispose();
                }
            }
        );

        // SPA fallback: serve index.html for client-side routes
        if (spaDistPath != null && Directory.Exists(spaDistPath))
        {
            var indexPath = Path.Combine(spaDistPath, "index.html");
            if (File.Exists(indexPath))
            {
                _app.MapFallback(async context =>
                {
                    context.Response.ContentType = "text/html";
                    await context.Response.SendFileAsync(indexPath);
                });
            }
        }

        await _app.StartAsync();

        // Extract bound port
        var serverAddresses = _app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _serverUrl = serverAddresses?.Addresses.FirstOrDefault() ?? "http://127.0.0.1:0";

        Console.Error.WriteLine($"[WebUI] Server started at {_serverUrl}");

        // Start broadcast task
        using (ExecutionContext.SuppressFlow())
        {
            _broadcastTask = Task.Run(BroadcastLoop);
        }

        // NOTE: Browser is NOT opened here. Call OpenBrowser() after PopulateTests()
        // so the first snapshot the client receives already contains the test tree.
    }

    /// <summary>
    /// Opens the browser to the WebUI. Call this after PopulateTests() so the
    /// initial snapshot is fully populated when the client connects.
    /// </summary>
    public void OpenBrowser()
    {
        if (_serverUrl == null || IsCI())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_serverUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebUI] Could not open browser: {ex.Message}");
            Console.Error.WriteLine($"[WebUI] Open manually: {_serverUrl}");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        // Only write mock data if the run didn't finish normally; the
        // OnRunFinished path below already wrote it with instances still
        // present.
        if (!_recorder.IsRunFinished)
        {
            WriteMockData();
        }

        // The static report bundle is assembled from Program.cs's finally (one
        // site, runs in every mode incl. CIRenderer) — not here, which only ran
        // when the renderer was a WebRenderer.

        // Drain queued events so post-run events (recordings, late artifacts)
        // reach the browser before we stop accepting new sends.
        _eventChannel.Writer.TryComplete();

        if (_broadcastTask != null)
        {
            try
            {
                await _broadcastTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            { /* ignore timeout */
            }
        }

        // Break WebSocket handler loops. The CTS stays cancelled, so any
        // reconnect after this point also fails immediately on its first read.
        _connectionsCts.Cancel();
        _connectionsCts.Dispose();

        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Block until the operator presses a key or the shutdown signal fires
    /// (Ctrl+C, UI Stop). Called from <c>Program.cs</c>'s outer finally on
    /// dev-mode Web runs that completed normally, before
    /// <see cref="DisposeAsync"/> tears Kestrel down — so the operator can
    /// inspect the run in their browser. Static files and <c>/api/state</c>
    /// continue to serve until the caller progresses past this method.
    /// </summary>
    public async Task WaitForKeypressOrShutdownAsync()
    {
        Console.Error.WriteLine("[WebUI] Tests finished. Press any key to exit.");

        var keyTask = Task.Run(() =>
        {
            try
            {
                Console.ReadKey(intercept: true);
            }
            catch
            { /* stdin not available */
            }
        });
        await Task.WhenAny(keyTask, _shutdownSignal.Task);
    }

    /// <summary>
    /// Signal the server to shut down (called by Ctrl+C handler).
    /// </summary>
    public void SignalShutdown() => _shutdownSignal.TrySetResult();

    private void TryHandleCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl))
            {
                return;
            }

            var cmd = cmdEl.GetString();
            if (cmd is null)
            {
                return;
            }

            Dispatch(cmd);
        }
        catch (JsonException)
        {
            // Malformed payload from a misbehaving client — drop silently.
        }
    }

    private void Dispatch(string command)
    {
        switch (command)
        {
            case "stop":
                // Fire-and-forget so the WS receive thread doesn't block on
                // the handler (Program.cs's ForceExitNow), which kills the
                // xUnit child + bulk-removes Docker resources before exit.
                var handler = _onCommand;
                if (handler != null)
                {
                    Task.Run(() => handler("stop"));
                }

                break;
        }
    }

    #region ITestRenderer implementation

    public override void OnDiscoveryComplete(DiscoveryCompleteEvent e) { }

    public override void OnRunStarted(RunStartedEvent e) { }

    public override void OnRunFinished(RunFinishedEvent e)
    {
        // Write mock data for frontend development. State has already been
        // mutated by the guard before this dispatch, so the snapshot is
        // complete.
        //
        // Disk artifacts (summary.json, ctrf-report.json, latest.txt) are
        // written from Program.cs's outer finally once the setup pipe has
        // drained — flaky_tests in particular is emitted from
        // TestSummaryFixture.DisposeAsync (after OnExecutionComplete) and
        // would be missing if we wrote here.
        WriteMockData();
    }

    public override void OnRunMetadata(RunMetadataEvent e) { }

    public override void OnFlakyTests(FlakyTestsEvent e) { }

    public override void OnTestStarted(TestStartedEvent e) { }

    public override void OnTestRunning(TestRunningEvent e) { }

    public override void OnTestOutput(TestOutputEvent e) => base.OnTestOutput(e);

    public override void OnTestAnnotation(TestAnnotationEvent e) { }

    public override void OnTestEnrichment(TestEnrichmentEvent e)
    {
        // The guard already called ApplyTestEnrichment; we need the
        // reclassified flag to sync RendererBase's canceled/failed counter so
        // FailedCount / CanceledCount agree with the artifact view. Re-derive
        // by reading the stored EnrichmentOutcome would require a state probe;
        // simpler: call the dedicated counter sync, gated by RendererBase's
        // _classifiedAsCanceled set (idempotent — no-op when the test was
        // never classified as canceled).
        if (e.Outcome == "failed")
        {
            ReclassifyCanceledAsFailed(e.DisplayName);
        }
    }

    protected override void OnTestPassedCore(TestPassedEvent e) { }

    protected override void OnTestFailedCore(TestFailedEvent e) { }

    protected override void OnTestSkippedCore(TestSkippedEvent e) { }

    public override void OnScreenshotCaptured(ScreenshotCapturedEvent e) { }

    public override void OnRecordingCaptured(RecordingCapturedEvent e) { }

    public override void OnRecordingSkipped(RecordingSkippedEvent e) { }

    public override void OnDiagnostic(DiagnosticEvent e)
    {
        // Parse VNC URLs from diagnostic messages. The state Apply happened in
        // the guard; this is web-mode-specific intra-handler enrichment that
        // calls _state.AddVncUrl directly (see EmitVncUrl). The asymmetry is
        // contained — CIRenderer / LLMRenderer don't extract VNC URLs from
        // diagnostics.
        ExtractAndEmitVncUrl(e.Message);
    }

    public override void OnError(ErrorEvent e) { }

    public override void OnSetupPhaseStarted(SetupPhaseStartedEvent e) { }

    public override void OnSetupPhaseCompleted(SetupPhaseCompletedEvent e) { }

    public override void OnSetupStep(SetupStepEvent e) { }

    public override void OnVncUrl(VncUrlEvent e) => EmitVncUrl(e.Label, e.Url, e.CollectionName);

    // ── Instance lifecycle events ──
    // State is mutated by the guard; the renderer has no live presentation
    // work to do for these. Default RendererBase no-op behavior is sufficient.

    #endregion

    #region Private helpers

    private async Task BroadcastLoop()
    {
        try
        {
            await foreach (var json in _eventChannel.Reader.ReadAllAsync())
            {
                if (_wsClients.IsEmpty)
                {
                    continue;
                }

                var toRemove = new List<string>();
                foreach (var (id, ws) in _wsClients)
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            await SendToSocket(ws, json);
                        }
                        else
                        {
                            toRemove.Add(id);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[WebUI] Send failed for client {id}: {ex.Message}"
                        );
                        toRemove.Add(id);
                    }
                }

                foreach (var id in toRemove)
                {
                    if (_wsClients.TryRemove(id, out var ws))
                    {
                        Console.Error.WriteLine($"[WebUI] Removed stale client: {id}");
                        ws.Dispose();
                    }
                }
            }
        }
        catch (ChannelClosedException)
        {
            // Normal shutdown
        }
    }

    private static async Task SendToSocket(WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private void WriteMockData()
    {
        try
        {
            // Write snapshot and artifacts to public/mock-artifacts/ for frontend development.
            // Vite serves public/ as static files, so the dev server can load mock data directly.
            var projectDir = ReportGenerator.FindSpaProjectPath();
            if (projectDir == null)
            {
                return;
            }

            var mockArtifactsDir = Path.Combine(projectDir, "public", "mock-artifacts");
            var mockPath = Path.Combine(mockArtifactsDir, "mock-data.json");
            var testResultsPath = Path.GetFullPath("TestResults");

            var snapshot = _state.ToSnapshotJson();
            snapshot = ReportGenerator.ExportMockArtifacts(
                snapshot,
                mockArtifactsDir,
                testResultsPath
            );
            File.WriteAllText(mockPath, snapshot);
            Console.Error.WriteLine(
                $"[WebUI] Mock data written to {PathDisplay.ScrubMessage(mockPath)}"
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebUI] Failed to write mock data: {ex.Message}");
        }
    }

    private static bool IsCI()
    {
        return Environment.GetEnvironmentVariable("CI") != null
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null
            || Environment.GetEnvironmentVariable("TF_BUILD") != null;
    }

    // Matches "Label VNC: http://..." patterns in log messages
    private static readonly Regex VncUrlPattern = new(
        @"(?<label>[\w\s]+?)\s*VNC:\s*(?<url>https?://\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Adds a VNC endpoint URL to the state and broadcasts to clients.
    /// </summary>
    public void EmitVncUrl(string label, string url, string? collection = null) =>
        _eventChannel.Writer.TryWrite(_state.AddVncUrl(label, url, collection));

    private void ExtractAndEmitVncUrl(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var match = VncUrlPattern.Match(message);
        if (match.Success)
        {
            var label = match.Groups["label"].Value.Trim();
            var url = match.Groups["url"].Value.Trim();
            EmitVncUrl(label, url);
        }
    }

    #endregion
}
