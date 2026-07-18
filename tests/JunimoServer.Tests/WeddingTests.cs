using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for the host's participation in wedding ceremonies (<c>AlwaysOnServer</c>'s wedding
/// handling).
///
/// <para>
/// <b>The bug (regression gate).</b> A farmhand↔NPC wedding plays as a cutscene for everyone in the
/// session and ends with a <c>waitForOtherPlayers weddingEnd&lt;farmerId&gt;</c> step: each player
/// readies and a "Waiting for players (N/M)" dialog holds the ceremony open until all have readied —
/// and that count includes the host. The mod's <c>HandleSkippableEvent</c> used to force-skip EVERY
/// non-festival event (the wedding is <c>skippable == false</c>, but it skipped anyway), jumping the
/// host past the wait step so it never readied. Time is frozen during a wedding, so the clients hung
/// at "Waiting for players" forever and the whole server appeared stuck. The fix stops skipping the
/// wedding and has the host ready its slot of the wait gate as soon as the other players are ready on
/// it (the festival "continue when others do" pattern), with a wall-clock backstop so it can never
/// permanently stall. The clients still run and watch their own ceremony copies.
/// </para>
///
/// <para>
/// <b>Two players, two weddings, one day.</b> The game does NOT space weddings across days — both
/// engagement paths schedule for <c>today + 3</c> and only skip forward past festival/green-rain days
/// (<c>NPC.cs</c>/<c>FarmerTeam.cs</c> → <c>canHaveWeddingOnDay</c>), with no collision check, and
/// <c>queueWeddingsForToday</c> adds every eligible farmer to the <c>weddingsToday</c> list. So two
/// players both getting married the same day is a real, supported state. The test models it faithfully:
/// <b>two clients joined concurrently from the start</b> (declared <c>Clients = 2</c>, both connects
/// kicked off together via <c>Farmers.ConnectBothConcurrentlyAsync</c> so they land back-to-back
/// rather than B waiting on A's whole join — neither a bolt-on), each engaged to its own NPC on the
/// same day, then a single sleep-through queues both weddings. The host then fires them
/// <b>one ceremony at a time, back-to-back</b>
/// (<c>getAvailableWeddingEvent</c> pops one per <c>checkForEvents</c>), and <b>everyone takes part in
/// each</b>: both clients watch and ready every ceremony's gate, and the host participates in each (its
/// <c>weddingEnd&lt;farmerId&gt;</c> gate counts all present players). This subsumes the single-wedding
/// case and guards two things that a naive fix breaks: the per-ceremony reset (a one-shot latch
/// completes only the first), and starting the second queued wedding (vanilla fires weddings only on
/// location entry, so the host must re-trigger <c>checkForEvents</c> after the first ends).
/// </para>
///
/// <para>
/// <b>Setup.</b> <see cref="ActionsClient.EngageToNpc"/> engages a farmhand to an NPC with a
/// WeddingDate of tomorrow (so <c>queueWeddingsForToday</c>'s <c>CountdownToWedding &lt; 1</c> passes
/// on the wedding day). The engagement is authored on each CLIENT because a farmhand's <c>Farmer</c>
/// root is client-authoritative — a host-side spouse write is wiped by the client's nightly full-root
/// resend before the wedding fires. Both farmhands sleeping triggers the day transition, which queues
/// and then fires both weddings once each farmhand and the host are in non-temporary locations.
/// </para>
///
/// <para>
/// <b>Client role — ceremonies must VISIBLY RENDER on each client.</b> The whole point of this E2E is
/// that each client plays its own copy of BOTH ceremonies as a real cutscene (so the recorded client
/// video shows them). The test client drives each ceremony through the honest player path — the
/// <c>WeddingCutscenePlayer</c> tweak clicks each <c>speak</c> dialogue box per tick, so the cutscene
/// advances and renders frame-by-frame to its <c>waitForOtherPlayers</c> gate (which auto-readies on
/// arrival), with deliberate ~1.5s pauses at the "couple assembled" and "marriage pronounced" beats so
/// it's obvious in the video. Each distinct ceremony a client renders is recorded in
/// <c>/status.weddingsRendered</c> (one entry per <c>weddingEnd&lt;farmerId&gt;</c> gate) — a client
/// that force-skipped the ceremony would render nothing, so this is the proof the cutscene actually ran.
/// </para>
///
/// <para>
/// <b>Observation.</b> The primary gate is each client's <c>/status.weddingsRendered</c> reaching 2 —
/// proof the client rendered both cutscenes. The host-side <see cref="ServerApiClient.GetWeddingState"/>
/// (<c>/test/wedding_state</c>) is a secondary completion gate: its spouse-warp signal is master-only
/// (so it proves the HOST finished, not the clients), and <c>IsWeddingActive</c> mirrors
/// <c>Game1.CurrentEvent.isWedding</c> — false once the last ceremony ends.
/// </para>
/// </summary>
// Two clients connected for the whole test (Clients = 2) — both players are present and getting married
// together, the scenario under test. Exclusive: this test mutates GLOBAL game state — the calendar
// (SetDate/SetTime) and Game1.weddingsToday — so it must not run concurrently with another method on the
// shared server. (Single method today, but Exclusive future-proofs the class and matches FestivalTests.)
[TestServer(Clients = 2, Isolation = IsolationMode.SharedClass, Exclusive = true)]
public class WeddingTests : TestBase
{
    // Land on a non-festival day that allows weddings (canHaveWeddingOnDay excludes festival days).
    // Spring 2 → weddings fire Spring 3, both ordinary days.
    private const string WeddingSeason = "spring";
    private const int EngageDay = 2;
    private const int WeddingDay = 3;

    // The two NPCs the farmhands marry. Any marriable villagers work; both must be ordinary (not
    // festival-gated) so the ceremony fires on WeddingDay. Distinct NPCs so the two weddings are
    // independent end-states to assert.
    private const string SpouseNpcA = "Abigail";
    private const string SpouseNpcB = "Penny";

    // How long for both clients to fully RENDER both ceremonies before we consider the server hung.
    // Each client plays each cutscene through at CLIENT_TPS=5 (scripted pauses, dialogue clicks, and two
    // 1.5s "make it visible" beats per ceremony), so two ceremonies × two clients takes tens of seconds
    // even with the pace speedups. Deliberately oversized headroom: a genuine hang (a ceremony that never
    // starts/renders, or a host stall) still blows past it.
    private static readonly TimeSpan CeremoniesResolveTimeout = TimeSpan.FromSeconds(240);

    /// <summary>
    /// The regression gate: two farmhands marry two different NPCs on the same day with both clients
    /// connected, the host fires each ceremony in turn and — because it now participates in each
    /// "wait for players" step instead of skipping past it, resetting between them — both ceremonies
    /// complete, the day proceeds, and both couples end up married with their spouses moved in.
    /// Pre-fix the host force-skipped the weddings and the server hung on the first one.
    /// </summary>
    [Fact]
    public async Task TwoFarmhandNpcWeddings_SameDay_BothCompleteWithoutHangingHost()
    {
        var ct = TestCt;

        // Land on the day before the weddings.
        var setDate = await ServerApi.SetDate(WeddingSeason, EngageDay, year: 1, ct);
        Assert.NotNull(setDate);
        Assert.True(
            setDate.Success,
            $"SetDate({WeddingSeason} {EngageDay}) failed: {setDate.Error}"
        );

        // Connect BOTH farmhands CONCURRENTLY — both present from the start, not A then B (the scenario
        // under test). Neither is privileged: the primary is the shared GameClient, the second a
        // SecondFarmer. await using → farmhandB disconnects before the class's /newgame reset.
        var (farmhandA, farmhandBConn) = await Farmers.ConnectBothConcurrentlyAsync(
            primaryPrefix: "WeddingA",
            secondaryPrefix: "WeddingB",
            ct: ct
        );
        await using var farmhandB = farmhandBConn;
        var farmhandAId = farmhandA.JoinResult.UniqueMultiplayerId;
        var farmhandBId = farmhandB.Uid;

        // Both must be present together before any wedding setup — this is the scenario under test
        // (two players married the same day), so neither engagement/sleep step should begin until the
        // server confirms both joined. The join sequences already gate on per-player visibility; this
        // asserts the joint precondition explicitly so a regression to staggered joins fails here, loud.
        Assert.True(
            await ServerApi.WaitForPlayerByIdAsync(farmhandAId, ct: ct),
            $"Farmhand A (uid={farmhandAId}) must be present server-side after the concurrent join."
        );
        Assert.True(
            await ServerApi.WaitForPlayerByIdAsync(farmhandBId, ct: ct),
            $"Farmhand B (uid={farmhandBId}) must be present server-side after the concurrent join."
        );

        // Engage BOTH farmhands to their NPCs the same day, each on its OWN client. Author on the
        // CLIENT: a farmhand's Farmer root is client-authoritative, so the client resends its full root
        // each night and would overwrite a host-side spouse write before the wedding fires.
        var engageA = await GameClient.Actions.EngageToNpc(SpouseNpcA, daysUntilWedding: 1);
        Assert.NotNull(engageA);
        Assert.True(engageA.Success, $"EngageToNpc({SpouseNpcA}) failed: {engageA.Error}");
        Assert.True(
            engageA.IsEngaged,
            $"Farmhand A should read as engaged to {SpouseNpcA} (spouse={engageA.Spouse})."
        );

        var engageB = await farmhandB.Client.Actions.EngageToNpc(SpouseNpcB, daysUntilWedding: 1);
        Assert.NotNull(engageB);
        Assert.True(engageB.Success, $"EngageToNpc({SpouseNpcB}) failed: {engageB.Error}");
        Assert.True(
            engageB.IsEngaged,
            $"Farmhand B should read as engaged to {SpouseNpcB} (spouse={engageB.Spouse})."
        );

        // Wait for BOTH engagements to replicate client→host before sleeping. queueWeddingsForToday
        // runs on the host and reads the host's view of each farmhand's spouse, so the host must see
        // both before the day flips — and once it does, each client's nightly full-root resend carries
        // the same spouse, so it can't wipe it.
        var hostSeesBothEngagements = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Wedding_EngagementReplicated,
            async () =>
            {
                var a = await ServerApi.GetWeddingState(farmhandAId, SpouseNpcA, ct);
                var b = await ServerApi.GetWeddingState(farmhandBId, SpouseNpcB, ct);
                return a?.Success == true
                    && a.FarmhandSpouse == SpouseNpcA
                    && b?.Success == true
                    && b.FarmhandSpouse == SpouseNpcB;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            hostSeesBothEngagements,
            "Host never saw both farmhands engaged — both engagements must replicate client→host "
                + "before the day transition so queueWeddingsForToday can see them."
        );

        // Sleep-through to the wedding day. BOTH farmhands must sleep (each is a connected required
        // player in the day-end ready check) so the day transitions and runs queueWeddingsForToday.
        // SetClockSpeed(20) accelerates any remaining in-game time so the transition completes fast.
        var sleepA = await GameClient.Actions.Sleep();
        Assert.NotNull(sleepA);
        Assert.True(sleepA.Success, $"Farmhand A sleep failed: {sleepA.Error}");
        var sleepB = await farmhandB.Client.Actions.Sleep();
        Assert.NotNull(sleepB);
        Assert.True(sleepB.Success, $"Farmhand B sleep failed: {sleepB.Error}");

        await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        await ServerApi.SetClockSpeed(20, ct);
        try
        {
            var (dayChanged, disconnected) = await DayChange.WaitAsync(
                EngageDay,
                WeddingSeason,
                year: 1,
                checkConnection: true,
                ct
            );
            Assert.False(
                disconnected,
                "A farmhand disconnected during the sleep-through to the wedding day"
            );
            Assert.True(dayChanged, $"Day did not advance to {WeddingSeason} {WeddingDay}");
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }

        // The weddings fire once the farmhands/host are in non-temporary locations on the new day, ONE
        // ceremony at a time on the host: getAvailableWeddingEvent pops a single farmer per
        // checkForEvents, so the two couples get two SEPARATE ceremonies, fired back-to-back. Every
        // instance — host AND both clients — runs its own copy of each from the synced weddingsToday.
        //
        // PROOF that BOTH ceremonies VISIBLY RENDERED ON EACH CLIENT. The host-side spouse warp alone
        // is NOT sufficient: the ceremony's endBehaviors "wedding" spouse warp is master-only (Event.cs
        // `if (!Game1.IsMasterGame) break;`), so "spouse on Farm" only proves the HOST ran the ceremony
        // — it's blind to whether the clients rendered anything. To actually trust this E2E, each client
        // must play its OWN copy of BOTH ceremonies as a cutscene. The test client drives each ceremony
        // through the honest player path (WeddingCutscenePlayer clicks the dialogue boxes so the cutscene
        // renders frame-by-frame) and records each distinct ceremony it played in /status.weddingsRendered
        // (keyed by the per-wedding weddingEnd<farmerId> gate). So the real gate is: BOTH clients have
        // rendered BOTH ceremonies. A regression that skips a ceremony, runs only one, or fails to start
        // the 2nd leaves a client short of 2 and times out here.
        var bothClientsRenderedBoth = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Wedding_BothClientsRenderedBoth,
            async () =>
            {
                var aState = await GameClient.GetState();
                var bState = await farmhandB.Client.GetState();
                return aState?.WeddingsRendered.Count >= 2 && bState?.WeddingsRendered.Count >= 2;
            },
            timeout: CeremoniesResolveTimeout,
            cancellationToken: ct
        );
        if (!bothClientsRenderedBoth)
        {
            var aState = await GameClient.GetState();
            var bState = await farmhandB.Client.GetState();
            string Describe(System.Collections.Generic.List<RenderedCeremonyInfo>? r) =>
                r == null || r.Count == 0
                    ? "none"
                    : string.Join(
                        ", ",
                        r.ConvertAll(c => $"{c.Spouse}(gate={c.Gate},local={c.IsLocalPlayer})")
                    );
            Assert.Fail(
                "Both clients did not each render BOTH same-day wedding ceremonies. Each client must play "
                    + "its own copy of both couples' ceremonies as a visible cutscene "
                    + "(/status.weddingsRendered, one entry per weddingEnd<farmerId> gate). A client short "
                    + "of 2 means a ceremony was skipped, never started, or the 2nd queued wedding never "
                    + "fired on that client.\n"
                    + $"  client A (farmhand {SpouseNpcA}): rendered {aState?.WeddingsRendered.Count ?? 0} → {Describe(aState?.WeddingsRendered)}\n"
                    + $"  client B (farmhand {SpouseNpcB}): rendered {bState?.WeddingsRendered.Count ?? 0} → {Describe(bState?.WeddingsRendered)}"
            );
        }

        // With both clients having rendered both ceremonies, the host must also have finished both and
        // warped each spouse home (the master-only endBehaviors "wedding" warp), with no ceremony still
        // active. This is the host-side completion gate (kept from the original regression: it catches a
        // host that skipped/stalled even if clients rendered).
        var hostFinishedBoth = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Wedding_BothCeremoniesRan,
            async () =>
            {
                var a = await ServerApi.GetWeddingState(farmhandAId, SpouseNpcA, ct);
                var b = await ServerApi.GetWeddingState(farmhandBId, SpouseNpcB, ct);
                var host = await ServerApi.GetWeddingState(ct: ct);
                return a?.SpouseCurrentLocation == "Farm"
                    && b?.SpouseCurrentLocation == "Farm"
                    && host?.IsWeddingActive == false;
            },
            timeout: CeremoniesResolveTimeout,
            cancellationToken: ct
        );
        if (!hostFinishedBoth)
        {
            var a = await ServerApi.GetWeddingState(farmhandAId, SpouseNpcA, ct);
            var b = await ServerApi.GetWeddingState(farmhandBId, SpouseNpcB, ct);
            var host = await ServerApi.GetWeddingState(ct: ct);
            Assert.Fail(
                "Both same-day weddings did not both end host-side. Each spouse should be warped to the "
                    + "Farm by its ceremony's endBehaviors (\"wedding\" case, master-only); a couple whose "
                    + "spouse never reached the Farm means that ceremony never completed on the host. A "
                    + "still-active ceremony is the frozen-server symptom from the report.\n"
                    + $"  {SpouseNpcA}: currentLocation={a?.SpouseCurrentLocation ?? "null"} (expected Farm)\n"
                    + $"  {SpouseNpcB}: currentLocation={b?.SpouseCurrentLocation ?? "null"} (expected Farm)\n"
                    + $"  weddingActive={host?.IsWeddingActive}"
            );
        }

        // The host must not be left STUCK after the ceremonies — the reported symptom was the host frozen
        // in the black wedding fadeout with a dangling after-wedding dialogue, on the ceremony's
        // temporary Town map. "Both spouses on Farm + no active wedding" (above) does NOT catch that: the
        // spouses warp home and IsWeddingActive clears even while the host is stuck, because endBehaviors
        // runs the spouse warp before the host's own teardown stalls. So assert the host's recovery
        // directly: no event up, no fade-to-black, no dialogue up, and off any temporary event map. These
        // are exactly the four signals that were stuck pre-fix. Poll briefly — the host warps off the
        // temp map and clears the fade within a tick or two of the last ceremony ending.
        var hostRecovered = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Wedding_HostRecoveredAfterCeremonies,
            async () =>
            {
                var hs = await ServerApi.GetWeddingState(ct: ct);
                return hs?.Success == true
                    && !hs.EventUp
                    && !hs.FadeToBlack
                    && !hs.DialogueUp
                    && !hs.HostLocationIsTemporary;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        if (!hostRecovered)
        {
            var hs = await ServerApi.GetWeddingState(ct: ct);
            Assert.Fail(
                "Host did not recover after the same-day weddings — it is stuck in the post-ceremony "
                    + "state (the reported black-fadeout-with-dialogue freeze). After the last ceremony "
                    + "the host must end its event, clear the fade, dismiss the after-wedding dialogue, "
                    + "and warp off the temporary ceremony map.\n"
                    + $"  eventUp={hs?.EventUp} (expected false)\n"
                    + $"  fadeToBlack={hs?.FadeToBlack} (expected false)\n"
                    + $"  dialogueUp={hs?.DialogueUp} (expected false)\n"
                    + $"  hostLocationIsTemporary={hs?.HostLocationIsTemporary} (expected false)"
            );
        }

        // The host must be returned to its FarmHouse idle spot, not left standing on the open Farm map.
        // The wedding's endBehaviors ("wedding" case) sets the exit warp from
        // getHomeOfFarmer(Game1.player).getPorchStandingSpot(), which on the host resolves to the main
        // farmhouse porch — so eventFinished() drops the host onto the Farm. The mod warps it home after
        // the day's last ceremony; assert that here (separate from hostRecovered: "not stuck" is distinct
        // from "back home", and the home-warp lands a tick or two after the temp map clears).
        var hostHome = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Wedding_HostReturnedHomeAfterCeremonies,
            async () =>
            {
                var hs = await ServerApi.GetWeddingState(ct: ct);
                return hs?.Success == true
                    && hs.HostCurrentLocation?.StartsWith(
                        "FarmHouse",
                        System.StringComparison.Ordinal
                    ) == true;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        if (!hostHome)
        {
            var hs = await ServerApi.GetWeddingState(ct: ct);
            Assert.Fail(
                "Host was not returned to its FarmHouse after the same-day weddings — it is left on the "
                    + "open Farm map where the wedding exit warp drops it (the exit targets the host's "
                    + "farmhouse porch via getHomeOfFarmer(Game1.player)). After the last ceremony the host "
                    + "must warp back to its FarmHouse idle spot.\n"
                    + $"  hostCurrentLocation={hs?.HostCurrentLocation ?? "null"} (expected to start with \"FarmHouse\")"
            );
        }

        // Both MARRIAGES proven before anyone disconnects: the host's view of each farmhand's spouse is
        // the chosen NPC (the engagement became a marriage). Combined with both spouses warped to the
        // Farm (above), this is the durable married-state both couples reached. Disconnect happens only
        // after this — the await using on farmhandB runs at scope exit, after every gate here.
        var marriedA = await ServerApi.GetWeddingState(farmhandAId, SpouseNpcA, ct);
        var marriedB = await ServerApi.GetWeddingState(farmhandBId, SpouseNpcB, ct);
        Assert.True(
            marriedA?.FarmhandSpouse == SpouseNpcA,
            $"Farmhand A is not married to {SpouseNpcA} (host sees spouse={marriedA?.FarmhandSpouse ?? "null"})."
        );
        Assert.True(
            marriedB?.FarmhandSpouse == SpouseNpcB,
            $"Farmhand B is not married to {SpouseNpcB} (host sees spouse={marriedB?.FarmhandSpouse ?? "null"})."
        );

        // Both farmhands must still be connected — proving the day genuinely proceeded rather than a
        // client dropping. (Farmhand B is disposed by the await using at scope exit, after this.)
        var farmhandBState = await farmhandB.Client.GetState();
        Assert.True(
            farmhandBState?.IsConnected == true,
            "Farmhand B disconnected before the weddings completed — the day did not proceed cleanly."
        );

        LogSuccess(
            $"Both same-day weddings rendered on BOTH clients as separate cutscenes: farmhand A↔{SpouseNpcA} "
                + $"and B↔{SpouseNpcB}. Each client played its own copy of both ceremonies "
                + "(/status.weddingsRendered == 2 each), the host finished both wait gates in turn and "
                + "recovered, both spouses warped to the Farm, and both marriages are confirmed — no hang. "
                + "Neither client disconnected until both marriages were proven."
        );
    }
}
