using System.Text.Json;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Wait strategy that probes the container's <c>/health</c> endpoint and only
/// returns ready when the JSON response reports <c>tickCount &gt; 0</c> AND
/// <c>isFrozen == false</c> — proving the game loop is actually running, not
/// just that the HTTP listener is up.
///
/// <para>
/// Used by both <see cref="Containers.ServerContainer"/> and
/// <see cref="Containers.GameClientContainer"/>; the mod's <c>/health</c>
/// response shape (<see cref="JunimoServer.Services.Api.ApiService.HealthResponse"/>)
/// is identical for server and client so the same wait strategy works for both.
/// </para>
///
/// <para>
/// Probes from inside the container via <c>curl</c> so it works for local and
/// remote (SSH-tunneled) Docker daemons alike — coordinator-side
/// <c>localhost:&lt;mappedPort&gt;</c> would only resolve on the local daemon.
/// </para>
/// </summary>
internal sealed class WaitUntilGameReadyInContainer : IWaitUntil
{
    public const string StepName = "Waiting for game ready";

    private readonly int _port;
    private readonly string _label;
    private readonly string? _collectionName;
    private readonly bool _requireGalaxyResolved;
    private readonly bool _useLongPoll;
    private readonly Action<string?>? _onGalaxyStateResolved;
    private int _attemptCount;

    /// <summary>
    /// Server-side hard cap on the mod's <c>/wait/*</c> timeout, in seconds.
    /// Mirrors <c>ApiService.WaitMaxTimeout</c> — values larger than this are
    /// clamped server-side, so requesting longer is pointless.
    /// </summary>
    private const int WaitMaxTimeoutSec = 10;

    public int AttemptCount => _attemptCount;

    /// <summary>
    /// When <paramref name="requireGalaxyResolved"/> is true, readiness additionally
    /// requires that the test-client's Galaxy auth has produced a final state (success
    /// OR failure — both count as "ready"; the broker filters Galaxy-failed clients
    /// at lease time). When the final state is reached, <paramref name="onGalaxyStateResolved"/>
    /// is invoked with the value of <c>galaxyState</c> from <c>/health</c>.
    ///
    /// <para>
    /// When <paramref name="useLongPoll"/> is true, probes <c>/wait/health?ready=true</c>
    /// instead of <c>/health</c> — each in-container <c>curl</c> blocks server-side
    /// (up to <see cref="WaitMaxTimeoutSec"/>s) until the game thread is healthy,
    /// reducing per-cold-start round-trips from ~25 (1 Hz × 25 s ready time) to 1–3.
    /// On VPS hosts the saving is the per-call SSH overhead × the round-trip count.
    /// Only valid with <c>requireGalaxyResolved=false</c>: the server mod's <c>/health</c>
    /// response doesn't carry <c>galaxyReady</c>/<c>galaxyState</c> fields, so a
    /// long-poll match wouldn't satisfy the Galaxy-resolution gate.
    /// </para>
    /// </summary>
    public WaitUntilGameReadyInContainer(
        int port,
        string label,
        string? collectionName = null,
        bool requireGalaxyResolved = false,
        bool useLongPoll = false,
        Action<string?>? onGalaxyStateResolved = null)
    {
        if (useLongPoll && requireGalaxyResolved)
            throw new ArgumentException(
                "useLongPoll requires requireGalaxyResolved=false: server /health doesn't carry Galaxy fields.",
                nameof(useLongPoll));

        _port = port;
        _label = label;
        _collectionName = collectionName;
        _requireGalaxyResolved = requireGalaxyResolved;
        _useLongPoll = useLongPoll;
        _onGalaxyStateResolved = onGalaxyStateResolved;
    }

    public async Task<bool> UntilAsync(IContainer container)
    {
        _attemptCount++;

        if (_attemptCount == 1)
        {
            SetupEventBus.EmitStep("Setup", $"{StepName} ({_label})", SetupStepStatus.Started,
                collectionName: _collectionName);
        }

        try
        {
            // Long-poll path: --max-time covers both the in-container HTTP wait
            // (server clamps to WaitMaxTimeoutSec) and a small slack for response
            // serialization. -f is dropped: with /wait/health, a 408 means
            // "no match within the server-side window" — Testcontainers calls
            // UntilAsync again so we just want to return false in that case,
            // not have curl exit non-zero. We inspect the HTTP status via -w.
            var url = _useLongPoll
                ? $"http://localhost:{_port}/wait/health?ready=true&timeout={WaitMaxTimeoutSec * 1000}"
                : $"http://localhost:{_port}/health";
            var maxTimeSec = _useLongPoll ? (WaitMaxTimeoutSec + 2) : 5;
            var exec = _useLongPoll
                ? await container.ExecAsync(new[]
                {
                    "curl", "-sS", "--max-time", maxTimeSec.ToString(),
                    "-o", "/tmp/health.json",
                    "-w", "%{http_code}",
                    url
                })
                : await container.ExecAsync(new[]
                {
                    "curl", "-fsS", "--max-time", maxTimeSec.ToString(),
                    url
                });

            if (exec.ExitCode != DockerExitCodes.Success)
            {
                SetupEventBus.EmitStep("Setup", $"{StepName} ({_label})",
                    SetupStepStatus.InProgress, $"attempt #{_attemptCount}: curl exit={exec.ExitCode}",
                    collectionName: _collectionName);
                return false;
            }

            string body;
            if (_useLongPoll)
            {
                // exec.Stdout is the http_code (from -w); 408 means "retry".
                var statusCode = exec.Stdout.Trim();
                if (statusCode != "200")
                {
                    SetupEventBus.EmitStep("Setup", $"{StepName} ({_label})",
                        SetupStepStatus.InProgress, $"attempt #{_attemptCount}: /wait/health status={statusCode}",
                        collectionName: _collectionName);
                    return false;
                }
                var bodyExec = await container.ExecAsync(new[] { "cat", "/tmp/health.json" });
                if (bodyExec.ExitCode != DockerExitCodes.Success)
                {
                    return false;
                }
                body = bodyExec.Stdout;
            }
            else
            {
                body = exec.Stdout;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tickCount = root.TryGetProperty("tickCount", out var tc) ? tc.GetInt64() : 0;
            var isFrozen = root.TryGetProperty("isFrozen", out var frozen) && frozen.GetBoolean();

            // Galaxy fitness gate. Tri-state: null = pending, true = signed in or
            // disabled (LAN), false = failed/lost. Both true and false satisfy the
            // gate — Galaxy-failed clients still enter the pool; ClientPool filters
            // them out for requireSteam=true leases. Only null (pending) blocks.
            bool? galaxyReady = null;
            string? galaxyState = null;
            if (root.TryGetProperty("galaxyReady", out var gr) && gr.ValueKind != JsonValueKind.Null)
            {
                galaxyReady = gr.GetBoolean();
            }
            if (root.TryGetProperty("galaxyState", out var gs) && gs.ValueKind != JsonValueKind.Null)
            {
                galaxyState = gs.GetString();
            }

            var detail = _requireGalaxyResolved
                ? $"attempt #{_attemptCount}: tickCount={tickCount}, isFrozen={isFrozen}, galaxyReady={(galaxyReady?.ToString() ?? "null")}, galaxyState={galaxyState ?? "null"}"
                : $"attempt #{_attemptCount}: tickCount={tickCount}, isFrozen={isFrozen}";

            SetupEventBus.EmitStep("Setup", $"{StepName} ({_label})",
                SetupStepStatus.InProgress, detail,
                collectionName: _collectionName);

            var galaxyResolved = !_requireGalaxyResolved || galaxyReady != null;

            if (tickCount > 0 && !isFrozen && galaxyResolved)
            {
                if (_requireGalaxyResolved)
                    _onGalaxyStateResolved?.Invoke(galaxyState);

                SetupEventBus.EmitStep("Setup", $"{StepName} ({_label})",
                    SetupStepStatus.Completed,
                    $"{_label} online and ticking after {_attemptCount} attempt(s)",
                    collectionName: _collectionName);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            SetupEventBus.EmitStep("Setup", $"{StepName} ({_label})",
                SetupStepStatus.InProgress,
                $"attempt #{_attemptCount}: {ex.GetType().Name}: {ex.Message}",
                collectionName: _collectionName);
            return false;
        }
    }
}
