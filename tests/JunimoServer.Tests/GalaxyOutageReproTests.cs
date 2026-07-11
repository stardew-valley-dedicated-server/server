using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Regression tests for the Galaxy-reinit-after-outage fix. A total connectivity loss on a
/// Steam-authenticated headless server drops Galaxy auth. The Galaxy SDK fires no AUTH callback
/// and its IUser liveness members (SignedIn/IsLoggedOn/GetGalaxyID) stay stale, so the lobby's
/// Galaxy session is left dead and vanilla clients can't rejoin even after Steam auto-reconnects.
/// The Steam reconnect is the recovery TRIGGER; the discriminator for whether to act is the Galaxy
/// LOBBY's own state — GalaxySocket.Connected (lobby != null), which DOES go false on a full outage
/// (the lobby-left callback fires) and stays true on a healthy server (verified 2026-06-19). So the
/// fix re-establishes Galaxy (re-login + lobby re-stamp) from AuthService.OnServerSteamIdReceived's
/// reconnect branch ONLY when the lobby is dead — re-login rebuilds the lobby and severs connected
/// clients, so it must be skipped on a Steam-CM-only flap where Galaxy was fine.
///
/// Two tests:
/// • TotalConnectivityLoss_RecordsGalaxyReauthSignal — induce the outage, assert the fix recovers by either
///   valid path (auth_galaxy_recovered from re-login, OR auth_galaxy_relogin_skipped when Galaxy's
///   own auto-recreate already rebuilt the lobby — a sub-second race) plus a fresh post-reconnect
///   auth_steam_lobby_created (the re-stamp's pointer refresh).
///   COVERAGE BOUNDARY: asserts the SERVER-side recovery, not a client rejoin. The pre-cut client is
///   only a witness that the initial code decoded to a live lobby; the outage severs it and vanilla
///   GalaxyNetClient does NOT auto-rejoin (no re-JoinLobby path — connectImpl runs once,
///   GalaxyNetClient.cs:73-78). End-to-end rejoin by a FRESH client (decoding the re-stamped code) is
///   covered by the manual outage repro.
/// • GalaxyReloginGate_WhileHealthy_SkipsAndKeepsClientConnected — drive the SAME gate on a healthy
///   connected server, assert it SKIPS (auth_galaxy_relogin_skipped, no recovered), the client stays
///   connected, and the invite code is unchanged.
///
/// Outage mechanism: disconnect the server container from its single test network (full cut →
/// Steam AND Galaxy drop together; distinct from #391's partial Steam-CM cut). The API is dark
/// during the cut AND stays unreachable after reconnect on remote Docker hosts (the daemon
/// doesn't restore the published-port forwarding), so the outage test reads everything from
/// infrastructure.jsonl (mod events stream over stdout) and leaves the health watchdog suspended
/// post-restore. See NetworkOutageHelper / InfraEventReader for the verified constraints.
///
/// Steam is required (LAN has no Galaxy). Exclusive so no reuse / concurrent method touches the
/// deliberately-cut server. Run with: make test FILTER=GalaxyOutageRepro
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, WithSteam = true, Exclusive = true)]
public class GalaxyOutageReproTests : TestBase
{
    private static readonly IReadOnlySet<string> SteamEvents = new HashSet<string>
    {
        "steam_session_lost",
        "steam_session_connected",
    };

    // The two terminal outcomes of the gated Galaxy reauth path
    // (AuthService.TryBeginGalaxyReSignInGated): recovered once a dead lobby is re-stamped after
    // re-login, skipped when the gate finds the lobby still connected (a Steam-CM-only flap) and
    // declines to rebuild it. The intermediate auth_galaxy_relogin_attempt is intentionally not
    // here — recovered is emitted only after a successful attempt, so it already implies one.
    private static readonly IReadOnlySet<string> RecoveryEvents = new HashSet<string>
    {
        "auth_galaxy_recovered",
        "auth_galaxy_relogin_skipped",
    };

    // The reconnect handler mints a fresh Steam lobby on every reconnect; the re-stamp points the
    // (live) Galaxy lobby at it. A post-reconnect occurrence is the disk-observable proof the stale
    // pre-outage pointer was refreshed.
    private static readonly IReadOnlySet<string> SteamLobbyEvents = new HashSet<string>
    {
        "auth_steam_lobby_created",
    };

    [Fact]
    public async Task TotalConnectivityLoss_RecordsGalaxyReauthSignal()
    {
        var ct = TestContext.Current.CancellationToken;

        // ── Configurable outage dwell. This is timing margin held AFTER steam_session_lost is
        // already gate-confirmed below — it lets the Galaxy lobby genuinely drop before restore,
        // so recovery exercises a dead-lobby re-login rather than a too-fast flap. Override without
        // a rebuild via SDVD_OUTAGE_DWELL_MS (verify-documented-config-is-consumed.md).
        var dwell = TimeSpan.FromMilliseconds(
            int.TryParse(TestEnvLoader.Get("SDVD_OUTAGE_DWELL_MS"), out var ms) ? ms : 10_000
        );
        // Steam reconnect after a full outage can take minutes; bound it generously.
        var reconnectBudget = TimeSpan.FromMinutes(5);

        var serverSlug = $"server-{Lease!.Managed.Server.ServerIndex}";

        // ── 1. Bring up a Steam client and confirm the Galaxy lobby is live.
        // Lease/connect BEFORE the cut — a client lease during the outage could hit the
        // broker's transport-fault path. The client reaching the farmhand menu proves the
        // invite code decoded to a working Galaxy lobby.
        var connect = await Connect.WithRetryAsync(ct);
        Connect.AssertConnectionSuccess(connect);
        var inviteCode = InviteCode;
        Log($"Steam client connected; invite code = {inviteCode}");

        // ── 2. Suspend the health watchdog and capture the network id (the inspect
        // can't find it once the container is detached), then cut the network.
        var managed = Lease.Managed;
        var networkId = await NetworkOutageHelper.GetAttachedNetworkIdAsync(Lease, ct);
        managed.SuspendHealthChecks();
        Log($"Health checks suspended; cutting network {networkId[..12]}…");
        // Anchor every post-cut event wait to this instant so the server's own initial-boot
        // emissions (a first steam_session_connected, the pre-outage auth_steam_lobby_created)
        // can never satisfy an outage-recovery wait.
        var outageStart = DateTime.UtcNow;
        await NetworkOutageHelper.DisconnectAsync(Lease, ct);

        try
        {
            // ── 3. Confirm the outage landed — via STEAM, not Galaxy. SteamKit's keepalive
            // reliably reports the cut as steam_session_lost; Galaxy's auth callback does NOT fire on
            // a total connectivity loss, so it can't be the precondition. Gate on Steam only.
            var steamLost = await InfraEventReader.WaitForEventAsync(
                SteamEvents,
                e => e.Name == "steam_session_lost",
                TimeSpan.FromMinutes(2),
                forwardedVia: serverSlug,
                since: outageStart,
                ct: ct
            );
            Assert.True(
                steamLost != null,
                "Expected steam_session_lost after the network cut — Steam's keepalive must "
                    + "report the outage; its absence means the cut did not sever the container's "
                    + "only network connection, so the test setup is invalid."
            );
            Log("Outage confirmed via Steam: steam_session_lost observed.");

            // ── 4. Steam's loss is already confirmed above; hold the outage long enough for the
            // Galaxy lobby to drop before restore.
            Log($"Holding outage for {dwell.TotalSeconds:F0}s…");
            await Task.Delay(dwell, ct);
        }
        finally
        {
            // ── 5. Restore the container's network so stdout (and the Steam SDR relay)
            // flows again and the server can be torn down cleanly. In finally so a throw
            // or cancellation mid-outage still reconnects (otherwise the container is left
            // detached). Cleanup must NOT use the test's ct — if the body cancelled, ct is
            // already tripped and a ct-bound reconnect would be skipped, stranding the
            // container off the network. Use a fresh bounded token instead.
            //
            // The health watchdog stays SUSPENDED for the rest of the test: on a remote
            // Docker host, `docker network connect` gives the container a new IP and the
            // daemon does NOT restore the published-port forwarding the SSH tunnel relies
            // on, so the HTTP API stays unreachable from the coordinator even though the
            // container is healthy. Resuming the watchdog would poison the server on those
            // unreachable /health probes and cancel the verdict read below. We don't need
            // the API anyway — the verdict and the Steam-recovery check both read the
            // stdout-backed infrastructure.jsonl, which DOES flow once connectivity is back.
            using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await NetworkOutageHelper.ReconnectAsync(Lease, networkId, restoreCts.Token);
            Log("Network reconnected (health checks stay suspended; API unreachable on remote).");
        }

        // ── 6. Steam side must recover (the shipped #391 behavior). Harness invariant —
        // read from disk, NOT the API: steam_session_connected{isReconnect:true} streams
        // over stdout once connectivity is back, even though the HTTP API tunnel does not.
        var steamReconnect = await InfraEventReader.WaitForEventAsync(
            SteamEvents,
            e =>
                e.Name == "steam_session_connected"
                && e.Data.TryGetProperty("isReconnect", out var r)
                && r.GetBoolean(),
            reconnectBudget,
            forwardedVia: serverSlug,
            since: outageStart,
            ct: ct
        );
        Assert.True(
            steamReconnect != null,
            "Expected steam_session_connected{isReconnect:true} after restore — Steam auto-reconnect "
                + "is the already-shipped behavior; its absence means the restore failed."
        );
        Log("Steam recovery confirmed via disk: steam_session_connected{isReconnect:true}.");

        // ── 7. THE FIX REGRESSION GATE — path-agnostic. The Steam reconnect drives recovery, but the
        // Galaxy lobby can come back two ways and which one wins is a sub-second race (verified
        // 2026-06-19): (a) Galaxy's own vanilla auto-recreate rebuilds the lobby ~at restore, so the
        // gate sees it already connected and emits auth_galaxy_relogin_skipped; or (b) the lobby is
        // still down at reconnect, so the gate re-logs-in and emits auth_galaxy_recovered. In BOTH
        // cases the reconnect branch ALSO mints a fresh Steam lobby (auth_steam_lobby_created), which
        // the unconditional re-stamp pushes into whichever Galaxy lobby is live — that pointer refresh
        // is the actual fix. So assert the OUTCOME (Galaxy live via either path + fresh Steam lobby),
        // not the re-login mechanism. Asserting auth_galaxy_recovered specifically would flake on the
        // (common) auto-recreate-wins path.
        var recovery = await InfraEventReader.WaitForEventAsync(
            RecoveryEvents,
            e => e.Name == "auth_galaxy_recovered" || e.Name == "auth_galaxy_relogin_skipped",
            TimeSpan.FromMinutes(3),
            forwardedVia: serverSlug,
            since: steamReconnect!.Ts,
            ct: ct
        );
        Assert.True(
            recovery != null,
            "Expected the Steam reconnect to resolve Galaxy by one of its two valid paths — "
                + "auth_galaxy_recovered (re-login rebuilt the dead lobby) or auth_galaxy_relogin_skipped "
                + "(Galaxy auto-recreate already rebuilt it, so the gate skipped the disruptive re-login). "
                + "Neither appearing means the reconnect handler isn't running the Galaxy recovery at all."
        );
        var viaRelogin = recovery!.Name == "auth_galaxy_recovered";

        // The pointer refresh: a fresh Steam lobby must be created AFTER the reconnect, so the re-stamp
        // points the (live) Galaxy lobby at a current Steam lobby rather than the dead pre-outage one.
        var freshSteamLobby = await InfraEventReader.WaitForEventAsync(
            SteamLobbyEvents,
            e => e.Name == "auth_steam_lobby_created",
            TimeSpan.FromMinutes(2),
            forwardedVia: serverSlug,
            since: steamReconnect!.Ts,
            ct: ct
        );
        Assert.True(
            freshSteamLobby != null,
            "Expected auth_steam_lobby_created after the reconnect — the reconnect handler must mint a "
                + "fresh Steam lobby for the re-stamp to point the Galaxy lobby at. Its absence means the "
                + "stale pre-outage Steam-lobby pointer was never refreshed (the bug this fix targets)."
        );

        LogSection("GALAXY REAUTH-AFTER-OUTAGE — RECOVERY CONFIRMED");
        Log(
            viaRelogin
                ? "  Galaxy recovery path : RE-LOGIN (lobby was down at reconnect → auth_galaxy_recovered)"
                : "  Galaxy recovery path : AUTO-RECREATE (lobby already back → auth_galaxy_relogin_skipped)"
        );
        Log(
            "  auth_steam_lobby_created (post-reconnect) : PRESENT (fresh Steam lobby for the re-stamp)"
        );
        Log(
            "  ⇒ Galaxy session recovered after total connectivity loss; the Steam-lobby pointer was refreshed."
        );

        // Retire this server so it is NOT reused. The network cut left it damaged: the health watchdog
        // is suspended and, on remote Docker hosts, the daemon never restores the published-port
        // forwarding the API tunnel needs — so a later test reusing this shared server would hit a
        // ServerReady timeout. Poisoning removes it from the pool; the broker boots a fresh one for the
        // next demand (the freed Steam account allows it, sequentially). Done last, after all asserts:
        // PoisonServer cancels the ErrorToken, but the client is already disconnected and no client op
        // remains, so the passed verdict stands. Mirrors TestLifecycle.PoisonOnCleanupFailureIfNeeded.
        Lease!.Managed.PoisonServer(
            "Total connectivity loss left the server's API unreachable (suspended watchdog + unrestored port-forward)",
            ManagedServer.PoisonReasonCode.TestRetiredServer
        );
    }

    /// <summary>
    /// FLAP-SAFETY GATE TEST. The Steam reconnect handler re-establishes Galaxy only when its lobby
    /// actually died; on a Steam-CM-only flap (Galaxy fine) it must SKIP the re-login, because
    /// re-login rebuilds the Galaxy lobby and severs every connected client. This drives the SAME gate
    /// the reconnect path uses (via /test/galaxy_relogin → TryBeginGalaxyReSignInGated) on a HEALTHY,
    /// client-connected server, and asserts the gate skips: the re-login is declined
    /// (auth_galaxy_relogin_skipped, no auth_galaxy_recovered), the connected player stays connected on
    /// BOTH sides, and the invite code is unchanged (no lobby rebuild). No outage is induced here — a
    /// healthy server is the flap stand-in, because GalaxySocket.Connected stays true exactly when the
    /// Galaxy lobby is undisturbed (verified 2026-06-19).
    /// </summary>
    [Fact]
    public async Task GalaxyReloginGate_WhileHealthy_SkipsAndKeepsClientConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverSlug = $"server-{Lease!.Managed.Server.ServerIndex}";

        // Fully join a client (with auth), so there's a live Galaxy lobby the gate must protect.
        var join = await Connect.JoinWithRetryAsync("ReloginProbe", ct: ct);
        Assert.True(join.Success, $"Join failed: {join.Error}");
        var uid = join.UniqueMultiplayerId;

        var present = await ServerApi.WaitForPlayerByIdAsync(uid, ct: ct);
        Assert.True(present, $"Player uid={uid} not present before re-login");

        var statusBefore = await ServerApi.GetStatus(ct);
        var inviteBefore = statusBefore?.InviteCode;
        Assert.False(string.IsNullOrEmpty(inviteBefore), "No invite code before re-login");
        Log($"Connected uid={uid}; invite code before = {inviteBefore}");

        // Drive the gated re-login path with NO outage. The gate sees the lobby is still connected and
        // declines — Triggered just means the endpoint reached the gate, not that a re-login ran.
        // Anchor the verdict waits to just before the trigger so the absent-recovered check can't be
        // fooled by any earlier-in-this-server's-life recovery, and the skipped match is this trigger's.
        var gateProbeStart = DateTime.UtcNow;
        var relogin = await ServerApi.TriggerGalaxyRelogin(ct);
        Assert.True(relogin?.Triggered == true, $"Gate path was not reached: {relogin?.Error}");
        Log(
            "Gated re-login driven (no outage). Expecting the gate to SKIP (Galaxy lobby still live)…"
        );

        // The gate must emit skipped, and must NOT recover (rebuild) the lobby. Wait for skipped to
        // land, then confirm recovered is ABSENT over a short settle window (an unflushed-buffer guard,
        // per InfraEventReader). A recovered here would mean the gate fired re-login on a healthy lobby.
        var skipped = await InfraEventReader.WaitForEventAsync(
            RecoveryEvents,
            e =>
                e.Name == "auth_galaxy_relogin_skipped"
                && e.Data.TryGetProperty("trigger", out var t)
                && t.GetString() == "test",
            TimeSpan.FromSeconds(20),
            forwardedVia: serverSlug,
            since: gateProbeStart,
            ct: ct
        );
        Assert.True(
            skipped != null,
            "Expected auth_galaxy_relogin_skipped{trigger:test} — the gate must decline re-login while "
                + "the Galaxy lobby is still connected. Its absence means the gate fired re-login on a "
                + "healthy lobby, which severs connected clients on a Steam-CM-only flap."
        );
        var recovered = await InfraEventReader.WaitForEventAsync(
            RecoveryEvents,
            e => e.Name == "auth_galaxy_recovered",
            TimeSpan.FromSeconds(5),
            forwardedVia: serverSlug,
            since: gateProbeStart,
            ct: ct
        );
        Assert.True(
            recovered == null,
            "Unexpected auth_galaxy_recovered on a healthy server — the gate rebuilt the Galaxy lobby "
                + "when it should have skipped, which would have dropped the connected client."
        );

        // The connected client must still be up on BOTH sides (a one-sided /players read lingers stale
        // after a peer is severed — see test-broker-invariants.md). Since the gate skipped, the lobby
        // was never rebuilt, so the peer is undisturbed.
        var serverSeesPlayer = await ServerApi.WaitForPlayerByIdAsync(uid, ct: ct);
        var clientState = await GameClient.GetState();
        var clientStillConnected = clientState?.IsConnected == true;
        Log(
            $"Post-gate liveness — server sees uid={uid}: {serverSeesPlayer}; "
                + $"client IsConnected: {clientStillConnected}, WorldReady: {clientState?.IsInGame}"
        );
        Assert.True(
            serverSeesPlayer && clientStillConnected,
            $"Player uid={uid} not connected on BOTH sides after the gated re-login "
                + $"(server={serverSeesPlayer}, client={clientStillConnected}). The gate should have "
                + "skipped re-login on the healthy lobby, leaving the peer connected."
        );

        // The invite code must be UNCHANGED: the gate skipped, so the Galaxy lobby (and its id, which
        // the code encodes as Base36) was never rebuilt. A changed code would mean the lobby was torn
        // down and recreated — the exact disruption the gate exists to prevent on a flap.
        var statusAfter = await ServerApi.GetStatus(ct);
        var inviteAfter = statusAfter?.InviteCode;
        Assert.False(string.IsNullOrEmpty(inviteAfter), "No invite code after re-login");
        Assert.Equal(inviteBefore, inviteAfter);

        LogSection("GALAXY RE-LOGIN GATE — SKIPS ON HEALTHY LOBBY, CLIENT UNAFFECTED");
        Log($"  auth_galaxy_relogin_skipped : PRESENT (gate declined re-login)");
        Log($"  auth_galaxy_recovered       : ABSENT  (lobby not rebuilt)");
        Log($"  connected uid={uid} stayed connected : yes");
        Log($"  invite code unchanged ({inviteAfter}) : yes");
        Log("  ⇒ a Steam-CM-only flap will not kick connected players.");
    }
}
