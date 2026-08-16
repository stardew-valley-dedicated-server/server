using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for festival handling (<c>AlwaysOnServerFestivals</c>).
///
/// <para>
/// The headline test is the regression gate for the leave-only auto-end bug: the two
/// no-main-event festivals (Spirit's Eve, Feast of Winter Star) used to host-end almost
/// immediately after loading (<c>RunLeaveOnly</c> firing its end dialogue). The fix removed
/// that auto-end so the festival only ends when players leave or the wall-clock timeout
/// elapses. Nothing automated caught the bug before; these tests are that gate.
/// </para>
///
/// <para>
/// <b>Entry mechanic (verified against the decompiled source).</b> A festival is entered
/// by warping a connected client to the festival map (Spirit's Eve = "Town") <i>during the
/// festival's time window</i> (fall27 conditions "Town/2200 2350"). The day-transition
/// lands at 06:00, so each test <see cref="ServerApiClient.SetTime"/>s into the window
/// before warping. <c>Game1.warpFarmer</c> then sets the client's <c>festivalStart</c>
/// ready flag and opens an auto-confirming <c>ReadyCheckDialog</c>; the host's
/// <c>HandleFestivalStart</c> sees <c>CheckOthersReady("festivalStart")</c>, warps in and
/// sets its own ready, the dialog auto-confirms, and the client warps into the festival.
/// No manual menu click is needed.
/// </para>
///
/// <para>
/// <b>Observation.</b> During an active festival the client's <c>CurrentLocation</c> reads
/// "Temp" (the festival temp location <c>new Town("Maps\\Town", "Temp")</c>), not "Town",
/// so location is a poor proxy. Tests assert via the test-only <c>/test/festival_state</c>
/// endpoint (<see cref="ServerApiClient.GetFestivalState"/>), whose <c>IsFestivalActive</c>
/// mirrors <c>Game1.CurrentEvent.isFestival</c> — true while the festival runs, false once
/// it ends.
/// </para>
/// </summary>
// Exclusive (not just SharedClass): these tests mutate GLOBAL game state — the calendar
// (SetDate/SetTime), Game1.whereIsTodaysFest, and the single AlwaysOnServerFestivals
// instance. xUnit v3 starts a class's methods concurrently and SharedClass does NOT
// serialize them, so without Exclusive all four would race the one game clock (Test 1's
// Fall 27 vs Test 4's Spring 13, etc.). Exclusive serializes the methods via the
// per-class turn lock and holds the gate between them so another class can't /newgame the
// shared server mid-test. (Not KeepConnected, so the Exclusive+KeepConnected broker
// prohibition doesn't apply.)
[TestServer(Isolation = IsolationMode.SharedClass, Exclusive = true)]
public class FestivalTests : TestBase
{
    // Festival time windows (Data/Festivals "conditions"). A player can only enter between
    // Open and Close; warpFarmer bounces them before Open and ignores the festival after Close.
    // The test enters at Open to maximise in-window runway (the clock runs at 1x during entry).
    private static readonly (int Open, int Close) SpiritsEveWindow = (2200, 2350);
    private static readonly (int Open, int Close) IceWindow = (900, 1400);
    private static readonly (int Open, int Close) EggWindow = (900, 1400);

    // Spirit's Eve: Fall 27, leave-only (HasMainEvent = false), in Town.
    private const string SpiritsEveSeason = "fall";
    private const int SpiritsEveDay = 27;
    private const int SpiritsEveBeforeDay = 26;

    // Festival of Ice: Winter 8, main-event (HasMainEvent = true), in Forest.
    // Test 3's "next festival" — a different season so it can't collide with the Spirit's Eve
    // calendar state on the shared-class server.
    private const string IceSeason = "winter";
    private const int IceDay = 8;
    private const int IceBeforeDay = 7;
    private const string IceLocation = "Forest";

    // Egg Festival: Spring 13, main-event, in Town. Used by Test 4.
    private const string EggSeason = "spring";
    private const int EggDay = 13;
    private const int EggBeforeDay = 12;

    // Stardew Valley Fair: Fall 16, main-event, in Town. Test A (the #537 gate). Window Town/900 1500.
    private const string FairSeason = "fall";
    private const int FairDay = 16;
    private const int FairBeforeDay = 15;
    private static readonly (int Open, int Close) FairWindow = (900, 1500);

    // Luau: Summer 11, main-event (adds an iridium starfruit to the soup), at the Beach. Test B
    // (folds in #372). Window Beach/900 1400 — the Luau is on the Beach, not in Town.
    private const string LuauSeason = "summer";
    private const int LuauDay = 11;
    private const int LuauBeforeDay = 10;
    private static readonly (int Open, int Close) LuauWindow = (900, 1400);
    private const string LuauLocation = "Beach";

    // Per-map warp-in tiles (the engine's own area-warp tiles, Game1.cs:13204/13212/13216).
    // warpFarmer's festival-entry guard keys on the location NAME matching whereIsTodaysFest,
    // not the tile, so any in-bounds tile on the festival map triggers entry.
    private static readonly (int X, int Y) TownEntryTile = (35, 35);
    private static readonly (int X, int Y) ForestEntryTile = (34, 13);
    private static readonly (int X, int Y) BeachEntryTile = (34, 10);

    // How long a festival must stay active to prove the immediate auto-end is gone. Comfortably
    // larger than the old window, far below the SpiritsEveTimeOutSeconds wall-clock backstop.
    private static readonly TimeSpan FestivalSettleWindow = TimeSpan.FromSeconds(6);

    // The Fair's leave can be briefly deferred: HandleFestivalLeave holds off ending while the grange
    // results DialogueBox is open on the host, and HandleDialogueBox clears that box only on its
    // once-per-second pass (~12s at SERVER_TPS=5). Allow comfortably more than that cadence for the
    // end to land — NetworkSyncTimeout (10s) would flake if the box happens to be open when the leave
    // vote arrives. Only a ceiling: the common (box-already-clear) case ends within ~1s.
    private static readonly TimeSpan FairLeaveEndTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Test 1 (the regression gate): Spirit's Eve does NOT auto-end immediately.
    ///
    /// Pre-fix, <c>RunLeaveOnly</c> fired <c>TryStartEndFestivalDialogue</c> almost immediately
    /// after the festival loaded, ending it before the player could do anything. With the fix the
    /// festival stays active until a player leaves or the wall-clock timeout elapses, so it
    /// must still be active several seconds after entry.
    /// </summary>
    [Fact]
    public async Task SpiritsEve_DoesNotAutoEndImmediately()
    {
        var ct = TestCt;

        await EnterFestivalAsync(
            SpiritsEveSeason,
            SpiritsEveBeforeDay,
            SpiritsEveDay,
            SpiritsEveWindow,
            "Town",
            TownEntryTile,
            namePrefix: "FestEve",
            ct
        );

        // The fix is the only thing keeping the festival alive here: pre-fix it would have
        // ended almost immediately. Poll for the whole settle window and fail the moment the
        // festival is observed inactive.
        var endedEarly = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_StillActiveAfterSettle,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive != true;
            },
            timeout: FestivalSettleWindow,
            cancellationToken: ct
        );

        Assert.False(
            endedEarly,
            $"Spirit's Eve ended within {FestivalSettleWindow.TotalSeconds:0}s of entry. "
                + "Pre-fix, RunLeaveOnly's immediate auto-end did exactly this; "
                + "the leave-only fix removed it so the festival must stay active until players leave."
        );

        // Sanity: confirm it is genuinely still active (not merely "not yet ended" due to a
        // read race) and that the festival event is the one we entered.
        var finalState = await ServerApi.GetFestivalState(ct);
        Assert.NotNull(finalState);
        Assert.True(finalState.Success, $"festival_state read failed: {finalState.Error}");
        Assert.True(
            finalState.IsFestivalActive,
            "Festival should still be active after the settle window"
        );
        LogSuccess("Spirit's Eve stayed active through the settle window (no insta-end)");
    }

    /// <summary>
    /// Test 2: the festival ends when the connected player votes to leave.
    ///
    /// Exercises the intended end path: the client's <c>festivalEnd</c> ready (via the new
    /// <c>leave_festival</c> action → <c>TryStartEndFestivalDialogue</c>) satisfies the host's
    /// <c>HandleFestivalLeave</c> <c>CheckOthersReady("festivalEnd")</c>, which ends it.
    /// </summary>
    [Fact]
    public async Task SpiritsEve_EndsWhenPlayerLeaves()
    {
        var ct = TestCt;

        await EnterFestivalAsync(
            SpiritsEveSeason,
            SpiritsEveBeforeDay,
            SpiritsEveDay,
            SpiritsEveWindow,
            "Town",
            TownEntryTile,
            namePrefix: "FestLeave",
            ct
        );

        // Vote to leave (real-player exit path). The host votes too once it sees us ready,
        // satisfying festivalEnd and ending the festival for everyone.
        var leave = await GameClient.Actions.LeaveFestival();
        Assert.NotNull(leave);
        Assert.True(leave.Success, $"leave_festival action failed: {leave.Error}");

        var ended = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_EndedAfterLeave,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive == false;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );

        Assert.True(
            ended,
            "Festival should end after the connected player votes to leave "
                + "(host's HandleFestivalLeave -> CheckOthersReady(\"festivalEnd\"))."
        );
        LogSuccess("Spirit's Eve ended after the player voted to leave");
    }

    /// <summary>
    /// Test 3: an empty festival ends on its own (no online players left), and the NEXT festival
    /// is still leavable — guarding both the no-online-players end and the per-festival state reset.
    ///
    /// Disconnecting the only client leaves nobody online at the festival; the host's
    /// <c>HandleFestivalLeave</c> no-online-players branch (<c>OnlineFarmers.CountOthers() == 0</c>)
    /// ends it gracefully via the host's own festivalEnd ready. Then a fresh client enters a later
    /// festival (Festival of Ice) and votes to leave: that leave must be honoured, which it isn't
    /// if a stale <c>_startedFestivalEnd</c> survives the empty festival (the engine sets
    /// timeOfDay to 2400 on the Spirit's Eve fade, which fires <c>UpdateFestivalStatus</c>'s reset).
    /// </summary>
    [Fact]
    public async Task EmptyFestivalEnds_AndNextFestivalIsLeavable()
    {
        var ct = TestCt;

        var farmhand = await EnterFestivalAsync(
            SpiritsEveSeason,
            SpiritsEveBeforeDay,
            SpiritsEveDay,
            SpiritsEveWindow,
            "Town",
            TownEntryTile,
            namePrefix: "FestEmpty",
            ct
        );

        // Disconnect the only player — nobody is left at the festival.
        await Farmers.DisconnectAndWaitForSlotAsync(
            farmhand.JoinResult.UniqueMultiplayerId,
            farmhand.FarmerName,
            ct
        );

        // The host ends the now-empty festival via HandleFestivalLeave's no-online-players branch.
        var emptyEnded = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_EndedNoPlayers,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive == false;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            emptyEnded,
            "An empty festival (no online players left) should end via HandleFestivalLeave's "
                + "OnlineFarmers.CountOthers() == 0 branch — otherFarmers.Count can't reach 0 during a "
                + "festival, so the end must key off the online (non-disconnecting) count."
        );
        LogSuccess("Empty Spirit's Eve ended with no online players present");

        // Connect a fresh client and enter the next festival, the Festival of Ice (Winter 8).
        // EnterFestivalAsync already asserts it becomes active.
        var nextFarmhand = await EnterFestivalAsync(
            IceSeason,
            IceBeforeDay,
            IceDay,
            IceWindow,
            IceLocation,
            ForestEntryTile,
            namePrefix: "FestNext",
            ct
        );
        Assert.NotNull(nextFarmhand);

        // The real gate for the per-festival reset: the next festival must be able to END VIA LEAVE.
        // A stale _startedFestivalEnd (carried over from the empty Spirit's Eve) would make
        // HandleFestivalLeave early-return on its _startedFestivalEnd guard, so the player's leave
        // vote would be ignored and the festival could only end via the ~33-min wall-clock timeout.
        // UpdateFestivalStatus clears _startedFestivalEnd once timeOfDay passes the festival's reset
        // cutoff while _activeFestival is set — and the engine sets timeOfDay to 2400 on the Spirit's
        // Eve end fade (Event.cs:4780), which deterministically trips that reset.
        var leaveNext = await GameClient.Actions.LeaveFestival();
        Assert.NotNull(leaveNext);
        Assert.True(
            leaveNext.Success,
            $"leave_festival on the next festival failed: {leaveNext.Error}"
        );

        var nextEnded = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_NextFestivalEndedOnLeave,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive == false;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            nextEnded,
            "The Festival of Ice should end when the player leaves — a stale _startedFestivalEnd "
                + "from the empty Spirit's Eve would make HandleFestivalLeave early-return and ignore "
                + "the leave vote. This is the regression gate for the UpdateFestivalStatus reset."
        );
        LogSuccess("Festival of Ice ran cleanly and ended on leave after the empty-festival end");
    }

    /// <summary>
    /// Test 4: a main-event festival (Egg Festival) skips its countdown via <c>!event</c> and
    /// proceeds. Lower priority — main-event festivals weren't the leave-only bug — but it
    /// covers the <c>!event</c> path so the main-event branch has a runtime gate too.
    /// </summary>
    [Fact]
    public async Task EggFestival_MainEventStartsWithCountdownSkip()
    {
        var ct = TestCt;

        await EnterFestivalAsync(
            EggSeason,
            EggBeforeDay,
            EggDay,
            EggWindow,
            "Town",
            TownEntryTile,
            namePrefix: "FestEgg",
            ct
        );

        // On a main-event festival the host announces the countdown within ~1s of the festival
        // becoming active (RunMainEventCountdown's announce block). Wait for it from chat history
        // — this confirms we entered a real main-event festival and the countdown is running.
        var announce = await GameClient.Chat.WaitForMessageContainingAsync(
            new[] { "Egg Hunt", "!event" },
            timeout: TestTimings.ChatCommandTimeout
        );
        Assert.NotNull(announce);

        // Skip the 5-minute wall-clock countdown via !event (otherwise the main event waits 5 real
        // minutes). The success path sends no reply, so assert on the festival state, not a chat
        // response: !event back-dates the countdown so the host immediately answers the festival
        // host's "start the event?" dialogue — the festival stays active while the main event runs.
        var sent = await GameClient.Chat.Send("!event");
        Assert.True(sent?.Success == true, $"Sending !event failed: {sent?.Error}");

        // Give the once-per-second HandleFestivalEvents a moment to consume the command and kick off
        // the main event, then confirm the festival is still active (the main event runs inside it —
        // !event must not have broken or prematurely ended it).
        var brokeOrEnded = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_MainEventStillActive,
            async () =>
            {
                var s = await ServerApi.GetFestivalState(ct);
                return s?.IsFestivalActive != true;
            },
            timeout: FestivalSettleWindow,
            cancellationToken: ct
        );
        Assert.False(
            brokeOrEnded,
            "Egg Festival should still be active while its main event runs after the !event skip — "
                + "!event must start the main event in place, not end the festival."
        );
        LogSuccess("Egg Festival accepted the !event countdown skip and kept running");
    }

    /// <summary>
    /// Test A (the #537 gate): the Stardew Valley Fair does not strand the festival after grange
    /// judging, and a connected player can still leave it.
    ///
    /// <para>
    /// Pre-fix, the Fair carried <c>AutoEndAfterCountdown</c>: when the grange countdown elapsed the
    /// host both started judging AND called <c>EndFestival(force: false)</c>. With a player present
    /// that marked only the host <c>festivalEnd</c>-ready and force-opened a ready-check no one could
    /// satisfy, while latching <c>_startedFestivalEnd</c> so no other exit could ever fire — the
    /// festival hung forever ("Grange display finished, triggering festival end", then silence).
    /// Step 1 removes the auto-end so the Fair ends like every other main-event festival; Step 2's
    /// menu-gated leave keeps the leave working even if the grange results dialogue is briefly open
    /// on the host when the player votes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Fair_DoesNotStrandFestival_AndLeavesCleanly()
    {
        var ct = TestCt;

        await EnterFestivalAsync(
            FairSeason,
            FairBeforeDay,
            FairDay,
            FairWindow,
            "Town",
            TownEntryTile,
            namePrefix: "FestFair",
            ct
        );

        // The host announces the grange countdown within ~1s of the festival becoming active.
        // Waiting for it confirms we entered the real main-event festival before skipping the
        // countdown (and, per the runtime gate, the container log is checked for this announce with
        // NO "Grange display finished, triggering festival end" line following it).
        var announce = await GameClient.Chat.WaitForMessageContainingAsync(
            new[] { "Grange Judging", "!event" },
            timeout: TestTimings.ChatCommandTimeout
        );
        Assert.NotNull(announce);

        // Skip the 5-minute countdown so grange judging fires immediately (mirrors the Egg test).
        var sent = await GameClient.Chat.Send("!event");
        Assert.True(sent?.Success == true, $"Sending !event failed: {sent?.Error}");

        // Primary regression assertion: after judging fires, the festival must stay active with the
        // host's festivalEnd ready NEVER set. Pre-fix the auto-end set FestivalEndReady >= 1 and
        // force-opened the host ready-check — the exact deadlock. Poll the settle window and fail the
        // moment the festival ends early or a host festivalEnd ready appears.
        var stranded = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_FairNoAutoEnd,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive != true || state.FestivalEndReady >= 1;
            },
            timeout: FestivalSettleWindow,
            cancellationToken: ct
        );
        Assert.False(
            stranded,
            "The Fair auto-ended after the grange countdown (festival inactive or FestivalEndReady "
                + ">= 1 within the settle window). Pre-fix, AutoEndAfterCountdown did exactly this — "
                + "marking only the host festivalEnd-ready and latching _startedFestivalEnd — which "
                + "deadlocked the festival. Step 1 removed the auto-end."
        );

        var midState = await ServerApi.GetFestivalState(ct);
        Assert.NotNull(midState);
        Assert.True(
            midState.IsFestivalActive,
            "The Fair should still be active after grange judging (it ends only on player-leave/timeout)."
        );
        Assert.True(
            midState.FestivalEndReady == 0,
            $"No festivalEnd ready should be set after judging; got {midState.FestivalEndReady} "
                + "(pre-fix the auto-end set the host's own festivalEnd ready and opened the ready-check)."
        );

        // End-to-end gate: a connected player can leave normally, proving _startedFestivalEnd was
        // never prematurely latched. Step 2 makes this deterministic even if the grange results
        // dialogue is transiently open on the host when the leave vote lands.
        var leave = await GameClient.Actions.LeaveFestival();
        Assert.NotNull(leave);
        Assert.True(leave.Success, $"leave_festival action failed: {leave.Error}");

        var ended = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_FairEndedOnLeave,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive == false;
            },
            timeout: FairLeaveEndTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ended,
            "The Fair should end after the connected player votes to leave — a latched "
                + "_startedFestivalEnd (the pre-fix auto-end, or an unguarded force:false end while the "
                + "grange results dialogue was open) would make HandleFestivalLeave early-return and "
                + "ignore the leave vote."
        );
        LogSuccess("Fair stayed active after judging (no auto-end) and ended cleanly on leave");
    }

    /// <summary>
    /// Test C (the Step 2 gate): a leave vote that lands while a host menu is open must be deferred —
    /// not latch <c>_startedFestivalEnd</c> — and must fire once the menu clears.
    ///
    /// <para>
    /// Removing the auto-end (Step 1) newly exposes the grange results <c>DialogueBox</c> on the host
    /// after judging. <c>TryStartEndFestivalDialogue</c> no-ops while any menu is open, but
    /// <c>EndFestival</c> latches <c>_startedFestivalEnd</c> unconditionally — so an unguarded
    /// <c>force:false</c> end during that window would re-strand the festival with no exit (the #537
    /// deadlock, now reachable via the results box). Step 2 gates both graceful ends on no open host
    /// menu.
    /// </para>
    ///
    /// <para>
    /// The gate keys on <c>Game1.activeClickableMenu is null</c>, not the menu's type, so this holds an
    /// inert host menu open across the leave vote — a deterministic stand-in for the grange box, whose
    /// real open/close timing on a render-suppressed host is nondeterministic. Test A covers the
    /// judging/no-auto-end path; this isolates the menu-open deferral. The last assertion is the
    /// discriminator: pre-fix, the vote latches on the open menu and the festival never ends.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Fair_DefersLeaveWhileHostMenuOpen_ThenEndsWhenCleared()
    {
        var ct = TestCt;

        await EnterFestivalAsync(
            FairSeason,
            FairBeforeDay,
            FairDay,
            FairWindow,
            "Town",
            TownEntryTile,
            namePrefix: "FestFairMenu",
            ct
        );

        // Open a host menu on the active (idle-countdown) Fair, reproducing the state the grange
        // results DialogueBox creates — a non-null Game1.activeClickableMenu. No host handler clears an
        // inert menu, so it stays open until we close it below, holding the leave-end gate closed
        // deterministically across the vote. (No !event: on the idle countdown no grange event runs to
        // open a dialogue over it, and the leave path runs every tick regardless of the countdown.)
        var opened = await ServerApi.SetHostMenu(open: true, ct);
        Assert.True(
            opened?.Success == true && opened.MenuOpen,
            $"Failed to open the host menu for the deferral scenario: {opened?.Error}"
        );

        // The connected player votes to leave WHILE the host menu is open.
        var leave = await GameClient.Actions.LeaveFestival();
        Assert.NotNull(leave);
        Assert.True(leave.Success, $"leave_festival action failed: {leave.Error}");

        // Confirm the vote replicated to the host, so the deferral asserted next is genuinely "vote
        // present but held", not "vote not yet arrived".
        var voteSynced = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_FairLeaveVoteSynced,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.FestivalEndReady >= 1;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(voteSynced, "The client's festivalEnd vote should replicate to the host.");

        // Deferral: with the vote present but a host menu open, the Fair must NOT end across the settle
        // window — HandleFestivalLeave holds off, never latching _startedFestivalEnd. (The menu stays
        // open the whole window: nothing clears an inert menu, and we only close it below.)
        var endedWhileMenuOpen = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_FairDeferredHold,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive != true;
            },
            timeout: FestivalSettleWindow,
            cancellationToken: ct
        );
        Assert.False(
            endedWhileMenuOpen,
            "While a host menu was open, the Fair must not end — the leave vote must be deferred, not "
                + "force-ended or latched."
        );

        // Release: close the host menu. The deferred leave must now fire and end the Fair. Pre-fix, the
        // force:false end latched _startedFestivalEnd while the menu was open, so HandleFestivalLeave
        // early-returns forever and the festival never ends here — the #537 strand via the results box.
        var closed = await ServerApi.SetHostMenu(open: false, ct);
        Assert.True(
            closed?.Success == true && !closed.MenuOpen,
            $"Failed to close the host menu: {closed?.Error}"
        );

        var ended = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_FairEndedAfterMenuCleared,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive == false;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ended,
            "After the host menu cleared, the deferred leave should end the Fair. A latched "
                + "_startedFestivalEnd (the pre-fix unconditional latch on the force:false end while a "
                + "host menu was open) would make HandleFestivalLeave early-return and strand it."
        );
        LogSuccess(
            "Fair deferred the leave while a host menu was open, then ended cleanly once it cleared"
        );
    }

    /// <summary>
    /// Test B (folds in the #372 verification): the Luau adds the iridium starfruit to the soup
    /// exactly once, even when <c>!event</c> fast-forwards the countdown after the on-entry announce.
    ///
    /// <para>
    /// #372: the iridium starfruit was added twice when <c>!event</c> was used mid-countdown. It is
    /// already fixed in the tree — the <c>!event</c> fast-forward guards its <c>OnAnnounce</c> with
    /// <c>if (!_announced)</c> — but nothing locked it in. This test is that lock: enter the Luau (the
    /// on-entry announce adds the starfruit once), then <c>!event</c> (the announce-then-!event
    /// ordering #372 reproduces under), and assert the soup still holds exactly one iridium starfruit.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Luau_AddsIridiumStarfruitExactlyOnce()
    {
        var ct = TestCt;

        await EnterFestivalAsync(
            LuauSeason,
            LuauBeforeDay,
            LuauDay,
            LuauWindow,
            LuauLocation,
            BeachEntryTile,
            namePrefix: "FestLuau",
            ct
        );

        // The on-entry announce adds the starfruit once (OnAnnounce = AddIridiumStarfruitToSoup) and
        // is broadcast as the "Soup Tasting" countdown message. Waiting for it guarantees the single
        // on-entry add has run before we send !event — the exact announce-then-!event ordering #372
        // reproduces under.
        var announce = await GameClient.Chat.WaitForMessageContainingAsync(
            new[] { "Soup Tasting", "!event" },
            timeout: TestTimings.ChatCommandTimeout
        );
        Assert.NotNull(announce);

        // Confirm the on-entry add is observable as exactly one before fast-forwarding.
        var addedOnce = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_LuauStarfruitAdded,
            async () =>
            {
                var s = await ServerApi.GetFestivalState(ct);
                return s?.LuauIridiumStarfruitCount == 1;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            addedOnce,
            "The Luau on-entry announce should add exactly one iridium starfruit to the soup."
        );

        // Fast-forward the countdown. Pre-#363 this re-ran OnAnnounce and added a SECOND starfruit;
        // the if (!_announced) guard now skips it. The main event fires in the same pass.
        var sent = await GameClient.Chat.Send("!event");
        Assert.True(sent?.Success == true, $"Sending !event failed: {sent?.Error}");

        // Assert the count never exceeds one across the settle window. A regressed guard adds the
        // second starfruit in the same tick !event is consumed, so it is caught on the first poll.
        var doubleAdded = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_LuauStarfruitStaysOne,
            async () =>
            {
                var s = await ServerApi.GetFestivalState(ct);
                return s?.LuauIridiumStarfruitCount is > 1;
            },
            timeout: FestivalSettleWindow,
            cancellationToken: ct
        );
        Assert.False(
            doubleAdded,
            "The Luau soup held more than one iridium starfruit after !event — the #372 double-add. "
                + "The !event fast-forward must guard OnAnnounce with if (!_announced) so the starfruit "
                + "is added exactly once per festival."
        );

        var finalState = await ServerApi.GetFestivalState(ct);
        Assert.NotNull(finalState);
        Assert.True(finalState.Success, $"festival_state read failed: {finalState.Error}");
        Assert.True(
            finalState.LuauIridiumStarfruitCount == 1,
            "Expected exactly one iridium starfruit in the Luau soup after !event; got "
                + $"{finalState.LuauIridiumStarfruitCount}."
        );
        LogSuccess("Luau added the iridium starfruit exactly once across the !event fast-forward");
    }

    /// <summary>
    /// Connects a client, sleeps through to the given festival day, moves the clock into the
    /// festival window, waits for the client to sync into that window, warps it into the festival
    /// map, and waits until the host has warped in and the festival is active. Returns the client.
    ///
    /// <para>
    /// Order matters: <see cref="ServerApiClient.SetTime"/> into the window happens AFTER the
    /// day-transition (which lands at 06:00); the warp happens only AFTER the client's own clock
    /// has caught up to the window. warpFarmer's festival-entry guard reads the warping player's
    /// LOCAL time, so warping before the client syncs bounces it with "festival is being set up".
    /// </para>
    /// </summary>
    private async Task<Infrastructure.Fixture.FarmerTestHelper.ClientConnection> EnterFestivalAsync(
        string season,
        int dayBefore,
        int festivalDay,
        (int Open, int Close) window,
        string festivalLocation,
        (int X, int Y) entryTile,
        string namePrefix,
        CancellationToken ct
    )
    {
        // Land on the day before the festival, then connect a client.
        var setDate = await ServerApi.SetDate(season, dayBefore, year: 1, ct);
        Assert.NotNull(setDate);
        Assert.True(setDate.Success, $"SetDate({season} {dayBefore}) failed: {setDate.Error}");

        var farmhand = await Farmers.ConnectFastAsync(namePrefix: namePrefix, ct: ct);

        // Sleep-through to the festival day. The client sleeps, the host auto-sleeps, and the
        // day transitions — the only path that populates Game1.whereIsTodaysFest (a /test/set_date
        // jump does not; see host-automation.md item 3). SetClockSpeed(20) accelerates any
        // remaining in-game time so the transition completes in seconds.
        var sleep = await GameClient.Actions.Sleep();
        Assert.NotNull(sleep);
        Assert.True(sleep.Success, $"Sleep action failed: {sleep.Error}");

        await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        await ServerApi.SetClockSpeed(20, ct);
        try
        {
            var (dayChanged, disconnected) = await DayChange.WaitAsync(
                dayBefore,
                season,
                year: 1,
                checkConnection: true,
                ct
            );
            Assert.False(
                disconnected,
                "Farmhand disconnected during the sleep-through to the festival day"
            );
            Assert.True(dayChanged, $"Day did not advance to {season} {festivalDay}");
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }

        // Confirm we landed on the festival day with whereIsTodaysFest populated before trying to
        // enter — a clearer failure than a downstream warp/entry timeout. whereIsTodaysFest is set
        // by the time-update tick, not the day flip / IsReady gate, so poll rather than read once.
        var onFestivalDay = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_DayConfirmed,
            async () =>
            {
                var s = await ServerApi.GetFestivalState(ct);
                return s?.IsFestivalDay == true && s.WhereIsTodaysFest == festivalLocation;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            onFestivalDay,
            $"Expected festival day {season} {festivalDay} with whereIsTodaysFest == \"{festivalLocation}\"."
        );

        // Move the server clock to the festival window's open time. The window is real game
        // behavior: warpFarmer only arms festivalStart when the warping player's LOCAL time is
        // inside it (fall27 = 2200-2350); before it, the engine bounces the player with
        // "Today's festival is being set up. Come back later." (Game1.cs:9961-9967). Entering at
        // Open maximises runway since the clock runs at 1x while we sync and warp.
        var setTime = await ServerApi.SetTime(window.Open, ct);
        Assert.NotNull(setTime);
        Assert.True(setTime.Success, $"SetTime({window.Open}) failed: {setTime.Error}");

        // Wait for the CLIENT to actually be festival-ready before warping it in. SetTime mutates
        // the server clock; the client only learns the new time/festival-day state when the host's
        // world-state broadcast reaches it. Warping before that sync lands warps the client at its
        // stale pre-window local time (e.g. 0630) → the engine bounces it home, festivalStart never
        // arms, and the host never warps in. Gate on the client's own /status: its local time must
        // be in the window with whereIsTodaysFest/weatherIcon synced, mirroring a real player
        // standing in Town as the clock ticks into the window. The window check is [Open, Close):
        // the engine permits entry through Close inclusive (Game1.cs:9959), but we stop one tick
        // short so the clock can't tick past Close during the async warp and bounce the client.
        var clientReady = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_ClientWindowSynced,
            async () =>
            {
                var f = await GameClient.GetFestivalDebug();
                return f is { WeatherIcon: 1 }
                    && f.WhereIsTodaysFest == festivalLocation
                    && f.TimeOfDay >= window.Open
                    && f.TimeOfDay < window.Close;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            clientReady,
            $"Client never synced into the festival window (expected whereIsTodaysFest == "
                + $"\"{festivalLocation}\", weatherIcon == 1, {window.Open} <= timeOfDay < {window.Close})."
        );

        // Warp the client into the festival map. Now that the client's local time is in-window, the
        // warp arms festivalStart + opens an auto-confirming ReadyCheckDialog; the host's reactive
        // CheckOthersReady("festivalStart") then warps it in and the festival becomes active.
        var warp = await GameClient.Actions.Warp(festivalLocation, entryTile.X, entryTile.Y);
        Assert.True(warp?.Success, $"Warp to {festivalLocation} failed: {warp?.Error}");

        var active = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Festival_BecameActive,
            async () =>
            {
                var state = await ServerApi.GetFestivalState(ct);
                return state?.IsFestivalActive == true;
            },
            timeout: TestTimings.GameReadyTimeout,
            cancellationToken: ct
        );
        if (!active)
        {
            // Dump both sides of the festivalStart handshake so the failure names which guard
            // is unmet (client whereIsTodaysFest/weatherIcon vs the ready counts vs host state).
            var clientDbg = await GameClient.GetFestivalDebug();
            var serverDbg = await ServerApi.GetFestivalState(ct);
            Assert.Fail(
                $"Festival ({festivalLocation}) did not become active after warping the client in.\n"
                    + $"  client: loc={clientDbg?.CurrentLocation}, weatherIcon={clientDbg?.WeatherIcon}, "
                    + $"whereIsTodaysFest={clientDbg?.WhereIsTodaysFest ?? "null"}, "
                    + $"isFestival={clientDbg?.IsFestival}, timeOfDay={clientDbg?.TimeOfDay}, "
                    + $"festivalStart={clientDbg?.FestivalStartReady}/{clientDbg?.FestivalStartRequired}\n"
                    + $"  server: whereIsTodaysFest={serverDbg?.WhereIsTodaysFest ?? "null"}, "
                    + $"isFestivalActive={serverDbg?.IsFestivalActive}, timeOfDay={serverDbg?.TimeOfDay}, "
                    + $"festivalStart={serverDbg?.FestivalStartReady}/{serverDbg?.FestivalStartRequired}"
            );
        }
        LogSuccess($"Festival active at {festivalLocation} ({season} {festivalDay})");

        return farmhand;
    }
}
