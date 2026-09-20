using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Containers;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Boot-time self-heal of corrupt game content: the steam-auth sidecar chunk-hash validates the
/// installed depot and re-downloads bad chunks, and the game container's entrypoint holds the
/// game back until that pass is over (<c>wait_for_content_validation</c> in startapp.sh).
///
/// The repair itself runs through the host's <em>shared</em> sidecar (<c>POST /game/validate</c>
/// on a scratch copy of the game volume): Steam allows one live session per account and the
/// shared sidecar holds them all, so a test-owned sidecar can never log in. The one test-owned
/// sidecar here is deliberately unable to log in (expired token) to prove the failure policy.
///
/// API-only. Leases one Steam client for its account index; never connects it.
/// </summary>
[TestServer(
    WithSteam = true,
    Isolation = IsolationMode.SharedAssembly,
    Clients = 1,
    Artifacts = false
)]
public class ContentValidationTests : TestBase
{
    /// <summary>The asset from the production incident; any English-content XNB would do.</summary>
    private const string IncidentAsset = "Content/Characters/Vincent.xnb";

    /// <summary>
    /// Runs the real entrypoint gate function (extracted verbatim from the shipped
    /// <c>/startapp.sh</c>) against <c>$STEAM_AUTH_URL</c> and reports how it exited.
    /// </summary>
    private const string GateScript = """
        set -u
        PHASE_FILE=/tmp/startup-phase
        eval "$(sed -n '/^wait_for_content_validation() {/,/^}/p' /startapp.sh)"
        start=$SECONDS
        wait_for_content_validation
        echo "GATE_RESULT rc=$? elapsed=$((SECONDS - start))"
        echo GATE_TEST_DONE
        sleep infinity
        """;

    /// <summary>
    /// Same gate function, fed by an in-container responder that answers the status endpoint
    /// with a fixed sequence (pending, running, running, then completed forever), then pointed
    /// at a closed port for the unreachable budget. Both phases in ONE container so the whole
    /// scenario costs one start.
    /// </summary>
    private const string SequencedGateScript = """
        set -u
        PHASE_FILE=/tmp/startup-phase
        eval "$(sed -n '/^wait_for_content_validation() {/,/^}/p' /startapp.sh)"
        # Serves each body once in order, then the last one forever. One nc per request; the
        # gate polls every 5s, so the respawn gap is never observed.
        serve_sequence() {
            local i=0 body
            while true; do
                [ "$i" -lt "$#" ] && i=$((i + 1))
                body="${!i}"
                nc -l -N -w 5 3001 < <(sleep 0.2; printf 'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n%s' "${#body}" "$body") > /dev/null 2>&1
            done
        }
        serve_sequence '{"status":"pending","detail":null}' '{"status":"running","detail":null}' '{"status":"running","detail":null}' '{"status":"completed","detail":"0 repaired, 0 downloaded, 3210 unchanged"}' &
        responder=$!
        ( while true; do cat "$PHASE_FILE" 2>/dev/null; echo; sleep 0.5; done ) > /tmp/phases &
        watcher=$!
        export STEAM_AUTH_URL=http://127.0.0.1:3001
        start=$SECONDS
        wait_for_content_validation
        echo "GATE_RESULT rc=$? elapsed=$((SECONDS - start))"
        kill "$watcher" "$responder" 2>/dev/null
        pkill -P "$responder" 2>/dev/null
        echo "PHASES_SEEN:$(sort -u /tmp/phases | tr '\n' ',')"
        echo "PHASE_FINAL:$(cat "$PHASE_FILE")"
        export STEAM_AUTH_URL=http://127.0.0.1:3002
        start=$SECONDS
        wait_for_content_validation
        echo "UNREACHABLE_RESULT rc=$? elapsed=$((SECONDS - start))"
        echo GATE_TEST_DONE
        sleep infinity
        """;

    [Fact]
    public async Task Validate_RepairsCorruptXnb_WithoutUpgradingTheInstall()
    {
        var sidecar =
            TestResourceBroker.Instance.TryGetSteamAuth(Lease!.Host)
            ?? throw new InvalidOperationException(
                $"host {Lease.Host.Id} has no shared steam-auth sidecar"
            );

        // Borrow a client account's live session; the server account keeps serving lobby ops.
        await using var client = await LeaseClientAsync(TestCt);
        var account = client.Container.SteamAccountIndex;
        Assert.True(
            account >= 0,
            "leased client must carry a Steam account index on a Steam server"
        );

        var scratch = $"/tmp/sdvd-validate-{Guid.NewGuid().ToString("N")[..8]}";
        LogSection($"Scratch copy of the game volume at {scratch}, corrupting {IncidentAsset}");
        var setup = await sidecar.ExecAsync(
            $$"""
            set -e
            rm -rf {{scratch}}
            cp -a /data/game {{scratch}}
            test -f {{scratch}}/{{IncidentAsset}}
            before=$(sha256sum < /data/game/{{IncidentAsset}})
            dd if=/dev/urandom of={{scratch}}/{{IncidentAsset}} bs=1 seek=256 count=1024 conv=notrunc 2>/dev/null
            after=$(sha256sum < {{scratch}}/{{IncidentAsset}})
            if [ "$before" != "$after" ]; then echo CORRUPTED; else echo UNCHANGED; fi
            """,
            TestCt
        );
        Assert.True(setup.ExitCode == 0, $"scratch setup failed: {setup.Stderr}");
        Assert.Contains("CORRUPTED", setup.Stdout);

        try
        {
            LogSection("POST /game/validate on the scratch copy");
            var outcome = await sidecar.ValidateGameContentAsync(
                account,
                scratch,
                timeout: TimeSpan.FromMinutes(8),
                TestCt
            );
            Log($"validate outcome: {outcome.Status} — {outcome.Detail}");

            Assert.True(
                outcome.Status == "completed",
                $"validation must complete; got {outcome.Status}: {outcome.Detail}"
            );
            Assert.True(
                outcome.FilesRepaired == 1,
                $"exactly the corrupted file must be repaired; got files_repaired={outcome.FilesRepaired}"
            );
            // A healthy install must come through untouched: a nonzero count here means a depot
            // file some later step rewrites on purpose (see IsPostInstallOwned in the sidecar) is
            // being reverted on every boot, or the volume predates the current download filter.
            Assert.True(
                outcome.FilesDownloaded == 0,
                $"a repair pass must fetch nothing but the corrupt chunk; got files_downloaded={outcome.FilesDownloaded} (see the sidecar log's 'downloading' lines)"
            );

            var verify = await sidecar.ExecAsync(
                $$"""
                sha256sum < /data/game/{{IncidentAsset}}
                sha256sum < {{scratch}}/{{IncidentAsset}}
                sed -n 's/.*"manifestId": *\([0-9]*\).*/\1/p' /data/game/.download-manifest-413150
                sed -n 's/.*"manifestId": *\([0-9]*\).*/\1/p' {{scratch}}/.download-manifest-413150
                """,
                TestCt
            );
            var lines = verify.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(
                lines.Length == 4 && lines[0] == lines[1],
                $"repaired file must be byte-identical to the install: {verify.Stdout}"
            );
            Assert.True(
                lines[2] == lines[3] && lines[2].Length > 0,
                $"the repaired install must keep the installed manifest (install {lines[2]}, scratch {lines[3]})"
            );
        }
        finally
        {
            await sidecar.ExecAsync($"rm -rf {scratch}", CancellationToken.None);
        }
    }

    [Fact]
    public async Task BootValidation_WithoutUsableLogin_FailsTerminallyAndReleasesTheGate()
    {
        var host = Lease!.Host;
        var network = await TestNetworkManager.GetOrCreateNetworkAsync(host, TestCt);
        var options = new ServerContainerOptions();
        var id = Guid.NewGuid().ToString("N")[..8];
        var sessionVolume = $"sdvd-test-steam-session-{id}";
        await DockerOps.CreateVolumeAsync(
            host.ApiClient,
            sessionVolume,
            new Dictionary<string, string> { ["sdvd.test"] = "true", ["sdvd.run-id"] = id },
            TestCt
        );

        try
        {
            // An expired refresh token fails login before any logon reaches Steam, so this
            // sidecar never contends with the shared one for an account.
            var accounts =
                $$"""[{"user":"sdvd-e2e-expired-token","token":"{{ExpiredRefreshToken()}}"}]""";
            LogSection("Sidecar with an expired token, validation on");
            await using var sidecar = await SharedSteamAuth.CreateAndStartAsync(
                network,
                options.ImageTag,
                options.GameDataVolume,
                sessionVolume,
                TestCt,
                host,
                accounts,
                validateOnBoot: true,
                networkAlias: $"steam-auth-expired-{id}",
                containerSlug: $"steam-auth-expired-{id}"
            );

            var status = await sidecar.WaitForBootValidationTerminalAsync(
                TimeSpan.FromSeconds(60),
                TestCt
            );
            Log($"boot validation: {status.Status} — {status.Detail}");
            Assert.True(
                status.Status == "failed",
                $"a login failure must end the pass as failed, not {status.Status}"
            );
            Assert.True(
                status.Detail?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true,
                $"detail must carry the login error; got: {status.Detail}"
            );
            var health = await sidecar.GetDockerHealthStatusAsync(TestCt);
            Assert.True(
                health == "healthy",
                $"the sidecar healthcheck must stay healthy across a failed pass; got {health}"
            );

            LogSection("Real entrypoint gate against the failed sidecar");
            var log = await RunGateProbeAsync(
                host,
                options.ImageTag,
                GateScript,
                network,
                sidecar.GetUrlForServer(),
                TestCt
            );
            Assert.Contains("could not validate the game files; starting anyway", log);
            var result = ParseResult(log, "GATE_RESULT");
            Assert.True(result.rc == 0, $"gate must release on failed; log:\n{log}");
        }
        finally
        {
            await DockerOps.RemoveVolumeAsync(
                host.ApiClient,
                sessionVolume,
                ct: CancellationToken.None
            );
        }
    }

    [Fact]
    public async Task Gate_HoldsWhilePendingOrRunning_ReportsValidatingPhase_ThenBudgetsUnreachable()
    {
        var host = Lease!.Host;
        var log = await RunGateProbeAsync(
            host,
            new ServerContainerOptions().ImageTag,
            SequencedGateScript,
            network: null,
            steamAuthUrl: null,
            TestCt
        );
        Log(log);

        Assert.Contains("validating the game files (pending)", log);
        Assert.Contains("validating the game files (running)", log);
        var held = ParseResult(log, "GATE_RESULT");
        Assert.True(held.rc == 0, $"gate must return 0 on completed; log:\n{log}");
        Assert.True(
            held.elapsed >= 15,
            $"three non-terminal polls at 5s must hold ≥15s; held {held.elapsed}s"
        );
        Assert.Contains("validating", Regex.Match(log, @"PHASES_SEEN:(.*)").Groups[1].Value);
        Assert.Contains("PHASE_FINAL:starting", log);

        Assert.Contains("did not answer", log);
        var unreachable = ParseResult(log, "UNREACHABLE_RESULT");
        Assert.True(
            unreachable.rc == 0,
            $"gate must release after the unreachable budget; log:\n{log}"
        );
        Assert.True(
            unreachable.elapsed is >= 55 and <= 90,
            $"unreachable budget is 12 polls at 5s; took {unreachable.elapsed}s"
        );
    }

    /// <summary>
    /// Starts the server image with the entrypoint replaced by <paramref name="script"/> and
    /// returns its output once the script logs <c>GATE_TEST_DONE</c>. The image's real
    /// <c>/startapp.sh</c>, curl and sed are what run, so the function under test is the shipped
    /// one.
    /// </summary>
    private static async Task<string> RunGateProbeAsync(
        DockerHost host,
        string imageTag,
        string script,
        INetwork? network,
        string? steamAuthUrl,
        CancellationToken ct
    )
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var builder = new ContainerBuilder($"sdvd/server:{imageTag}")
            .WithDockerEndpoint(host.EndpointConfig)
            .WithLogger(NullLogger.Instance)
            .WithImagePullPolicy(imageTag == "local" ? PullPolicy.Never : PullPolicy.Missing)
            .WithName($"sdvd-gate-probe-{id}")
            .WithEntrypoint("bash")
            .WithCommand("-c", script)
            .WithCreateParameterModifier(p =>
            {
                p.Labels ??= new Dictionary<string, string>();
                p.Labels["sdvd.test"] = "true";
                p.Labels["sdvd.run-id"] = id;
            })
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilMessageIsLogged(
                        "GATE_TEST_DONE",
                        o => o.WithTimeout(TimeSpan.FromMinutes(3))
                    )
            );
        if (network != null)
        {
            builder = builder.WithNetwork(network);
        }
        if (steamAuthUrl != null)
        {
            builder = builder.WithEnvironment("STEAM_AUTH_URL", steamAuthUrl);
        }

        await using var container = builder.Build();
        await container.StartAsync(ct);
        var (stdout, stderr) = await container.GetLogsAsync(timestampsEnabled: false, ct: ct);
        return stdout + stderr;
    }

    private static (int rc, int elapsed) ParseResult(string log, string marker)
    {
        var m = Regex.Match(log, marker + @" rc=(\d+) elapsed=(\d+)");
        Assert.True(m.Success, $"{marker} line missing from probe output:\n{log}");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    /// <summary>
    /// Three-segment token whose base64url payload carries an <c>exp</c> in the past; the sidecar
    /// reads only that claim (SteamAuthService.GetTokenExpiry), so no signature is needed.
    /// </summary>
    private static string ExpiredRefreshToken()
    {
        static string B64Url(string s) =>
            Convert
                .ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        return $"{B64Url("{\"alg\":\"none\"}")}.{B64Url("{\"exp\":1000000000}")}.sig";
    }
}
