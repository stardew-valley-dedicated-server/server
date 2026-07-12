using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E regression gates for the lobby-homed-spouses bug: with password protection enabled, the
/// lobby redirect used to write the lobby cabin into each joining farmhand's durable
/// <c>homeLocation</c>. The client is authoritative for its own Farmer root, so its nightly
/// full-root resend cloned the lobby home back into <c>farmhandData</c>, and
/// <c>NPC.marriageDuties</c> — which reads the spouse-farmer's <c>homeLocation</c> raw every
/// day-start, for offline farmhands too — re-homed NPC spouses into the ownerless lobby interior,
/// whose level-0 bed spot renders as the reported <c>(-999,-999)</c>. Nothing healed the persisted
/// state, and disabling the password disabled the only join-time repair.
///
/// <para>
/// The fix is two-layered: <b>prevention</b> (the redirect no longer writes <c>homeLocation</c>;
/// the transient spawn hints alone land the client in the lobby) and <b>reconciliation</b>
/// (CabinManagerService heals lobby-poisoned farmhand homes and re-homes stranded NPCs at
/// SaveLoaded — the poisoned-save migration — and at DayStarted — a tripwire that should find
/// nothing once prevention is in place).
/// </para>
///
/// <para>
/// This class covers the password-ON steady state: two farmhands join through the lobby, marry
/// NPCs, and their homes plus their spouses' locations must remain their real cabins across
/// connected nights (each of which fires the client's full-root resend — the poison generator
/// pre-fix) and across server-only nights after both disconnect (the reporter's 24/7
/// offline-couple scenario). Assertions are pure API outcomes: a home that never leaves the real
/// cabin and a spouse NPC that sleeps in it are exactly what the bug broke.
/// </para>
/// </summary>
// Exclusive: mutates global calendar state (SetDate/SetTime/SetClockSpeed) and relies on the
// server sleeping alone in the offline phase. Two clients — both couples present together,
// matching the incident report (two farmhands' spouses vanished).
[TestServer(
    Clients = 2,
    Password = "test-password-123",
    Isolation = IsolationMode.SharedClass,
    Exclusive = true
)]
public class LobbyHomedSpouseSteadyStateTests : TestBase
{
    // Land on a non-festival day that allows weddings (canHaveWeddingOnDay excludes festival
    // days). Spring 2 → weddings fire spring 3.
    private const string Season = "spring";
    private const int EngageDay = 2;

    private const string SpouseNpcA = "Abigail";
    private const string SpouseNpcB = "Penny";

    // Both clients play each ceremony as a real cutscene at CLIENT_TPS=5 — minutes, not seconds
    // (see WeddingTests.CeremoniesResolveTimeout for the full cost breakdown).
    private static readonly TimeSpan CeremoniesResolveTimeout = TimeSpan.FromSeconds(240);

    [Fact]
    public async Task MarriedCouples_UnderPassword_HomesStayRealAcrossNightsAndOffline()
    {
        var ct = TestContext.Current.CancellationToken;

        var setDate = await ServerApi.SetDate(Season, EngageDay, year: 1, ct);
        Assert.NotNull(setDate);
        Assert.True(setDate.Success, $"SetDate({Season} {EngageDay}) failed: {setDate.Error}");

        // Both farmhands join concurrently THROUGH THE LOBBY (password server): each lands in
        // the lobby cabin, auto-authenticates via !login, and is warped to its real cabin.
        var (farmhandA, farmhandBConn) = await Farmers.ConnectBothConcurrentlyAsync(
            primaryPrefix: "LobbyHomeA",
            secondaryPrefix: "LobbyHomeB",
            ct: ct
        );
        await using var farmhandB = farmhandBConn;
        var uidA = farmhandA.JoinResult.UniqueMultiplayerId;
        var uidB = farmhandB.Uid;

        Assert.True(
            await ServerApi.WaitForPlayerByIdAsync(uidA, ct: ct),
            $"Farmhand A (uid={uidA}) must be present server-side after the concurrent join."
        );
        Assert.True(
            await ServerApi.WaitForPlayerByIdAsync(uidB, ct: ct),
            $"Farmhand B (uid={uidB}) must be present server-side after the concurrent join."
        );

        // Capture each farmhand's REAL home right after the join. With prevention in place the
        // client's copy is born with this value and every later read must still equal it — any
        // drift (to the lobby or anywhere else) is the regression this test gates.
        var (homeA, homeB) = await CaptureRealHomesAsync(uidA, uidB, ct);

        // Engage both farmhands on their own clients (client-authoritative roots), wait for the
        // engagements to replicate, then sleep through to the wedding day.
        var engageA = await GameClient.Actions.EngageToNpc(SpouseNpcA, daysUntilWedding: 1);
        Assert.True(
            engageA?.Success == true && engageA.IsEngaged,
            $"EngageToNpc({SpouseNpcA}) failed: {engageA?.Error}"
        );
        var engageB = await farmhandB.Client.Actions.EngageToNpc(SpouseNpcB, daysUntilWedding: 1);
        Assert.True(
            engageB?.Success == true && engageB.IsEngaged,
            $"EngageToNpc({SpouseNpcB}) failed: {engageB?.Error}"
        );

        var hostSeesBoth = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_LobbyHome_EngagementReplicated,
            async () =>
            {
                var a = await ServerApi.GetWeddingState(uidA, SpouseNpcA, ct);
                var b = await ServerApi.GetWeddingState(uidB, SpouseNpcB, ct);
                return a?.Success == true
                    && a.FarmhandSpouse == SpouseNpcA
                    && b?.Success == true
                    && b.FarmhandSpouse == SpouseNpcB;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(hostSeesBoth, "Both engagements must replicate client→host before sleeping.");

        // Night 1 (wedding night): both sleep, ceremonies fire next morning.
        await SleepBothAndWaitForMorningAsync(farmhandB, ct);

        // Both ceremonies must complete host-side (endBehaviors warps each spouse to the Farm).
        var ceremoniesDone = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_LobbyHome_CeremoniesCompleted,
            async () =>
            {
                var a = await ServerApi.GetWeddingState(uidA, SpouseNpcA, ct);
                var b = await ServerApi.GetWeddingState(uidB, SpouseNpcB, ct);
                var host = await ServerApi.GetWeddingState(ct: ct);
                return a?.SpouseCurrentLocation == "Farm"
                    && b?.SpouseCurrentLocation == "Farm"
                    && host?.IsWeddingActive == false;
            },
            timeout: CeremoniesResolveTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ceremoniesDone,
            "Both wedding ceremonies must complete (spouses warped to Farm, no active wedding)."
        );

        var marriedA = await ServerApi.GetWeddingState(uidA, SpouseNpcA, ct);
        var marriedB = await ServerApi.GetWeddingState(uidB, SpouseNpcB, ct);
        Assert.True(
            marriedA?.FarmhandSpouse == SpouseNpcA && marriedB?.FarmhandSpouse == SpouseNpcB,
            "Both farmhands must be married after the ceremonies "
                + $"(A spouse={marriedA?.FarmhandSpouse}, B spouse={marriedB?.FarmhandSpouse})."
        );

        // Wedding morning: homes must still be the captured real cabins (the ceremony and the
        // day transition around it must not have moved them).
        await AssertHomesUnchangedAsync(uidA, homeA, uidB, homeB, "wedding morning", ct);

        // Nights 2 and 3, both couples CONNECTED: each night fires the client's full-root
        // resend — pre-fix, that was the poison generator under password. Each morning the
        // homes must be unchanged and each spouse NPC must have been homed into the couple's
        // real cabin by marriageDuties (pre-fix they'd sit in the lobby at (-999,-999)).
        for (var night = 2; night <= 3; night++)
        {
            await SleepBothAndWaitForMorningAsync(farmhandB, ct);
            await AssertHomesUnchangedAsync(uidA, homeA, uidB, homeB, $"morning {night}", ct);
            await AssertSpousesInCabinsAsync(uidA, homeA, uidB, homeB, $"morning {night}", ct);
        }

        // Offline phase (the reporter's 24/7 scenario): both players disconnect, the server
        // sleeps two nights alone. marriageDuties keeps running for OFFLINE farmhands, reading
        // the persisted farmhandData homes — which must stay the real cabins.
        await farmhandB.DisconnectAsync();
        await DisconnectAsync();
        Assert.True(
            await ServerApi.WaitForPlayersRemovedByIdAsync(new[] { uidA, uidB }, ct: ct),
            "Both farmhands must be removed server-side before the server-only nights — "
                + "otherwise marriageDuties still reads their live roots, not farmhandData."
        );

        for (var night = 4; night <= 5; night++)
        {
            await AdvanceDayServerOnlyAsync(ct);
            await AssertHomesUnchangedAsync(
                uidA,
                homeA,
                uidB,
                homeB,
                $"offline morning {night}",
                ct
            );
            await AssertSpousesInCabinsAsync(
                uidA,
                homeA,
                uidB,
                homeB,
                $"offline morning {night}",
                ct
            );
        }

        LogSuccess(
            "Both couples stayed healthy under password protection: farmhand homes never left "
                + $"their real cabins ({homeA}, {homeB}) across the wedding, two connected "
                + "nights (nightly client full-root resends), and two server-only nights with "
                + "both players offline — and both NPC spouses woke up inside those cabins every "
                + "morning instead of the lobby."
        );
    }

    /// <summary>
    /// Reads both farmhands' homeLocation from /diagnostics/state and asserts they are real,
    /// distinct cabin interiors ("FarmHouse&lt;guid&gt;", never the bare host "FarmHouse").
    /// </summary>
    private async Task<(string HomeA, string HomeB)> CaptureRealHomesAsync(
        long uidA,
        long uidB,
        CancellationToken ct
    )
    {
        var diag = await ServerApi.GetDiagnosticsState(ct);
        Assert.NotNull(diag);
        var entryA = diag.FarmhandData.FirstOrDefault(f => f.UniqueMultiplayerId == uidA);
        var entryB = diag.FarmhandData.FirstOrDefault(f => f.UniqueMultiplayerId == uidB);
        Assert.True(
            entryA != null && entryB != null,
            $"Both farmhands must appear in /diagnostics/state farmhandData "
                + $"(A found={entryA != null}, B found={entryB != null})."
        );

        foreach (var (entry, label) in new[] { (entryA!, "A"), (entryB!, "B") })
        {
            Assert.True(
                entry.HomeLocation.StartsWith("FarmHouse", StringComparison.Ordinal)
                    && entry.HomeLocation.Length > "FarmHouse".Length,
                $"Farmhand {label}'s home after join must be a cabin interior "
                    + $"(FarmHouse<guid>), got '{entry.HomeLocation}'."
            );
        }
        Assert.True(
            entryA!.HomeLocation != entryB!.HomeLocation,
            "The two farmhands must be homed at distinct cabins "
                + $"(both at '{entryA.HomeLocation}')."
        );

        return (entryA.HomeLocation, entryB.HomeLocation);
    }

    private async Task SleepBothAndWaitForMorningAsync(
        Infrastructure.Fixture.SecondFarmer farmhandB,
        CancellationToken ct
    )
    {
        var before = await ServerApi.GetStatus(ct);
        Assert.NotNull(before);

        var sleepA = await GameClient.Actions.Sleep();
        Assert.True(sleepA?.Success == true, $"Farmhand A sleep failed: {sleepA?.Error}");
        var sleepB = await farmhandB.Client.Actions.Sleep();
        Assert.True(sleepB?.Success == true, $"Farmhand B sleep failed: {sleepB?.Error}");

        await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        await ServerApi.SetClockSpeed(20, ct);
        try
        {
            var (dayChanged, disconnected) = await DayChange.WaitAsync(
                before.Day,
                before.Season,
                before.Year,
                checkConnection: true,
                ct
            );
            Assert.False(disconnected, "A farmhand disconnected during the sleep-through.");
            Assert.True(
                dayChanged,
                $"Day did not advance past {before.Season} {before.Day} Y{before.Year}."
            );
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }
    }

    /// <summary>
    /// Advances one in-game day with NO clients connected: the host passes out at 2:00 AM
    /// (same pattern as HostAutomationTests.HostPassesOut_WhenTimeReaches2AM).
    /// </summary>
    private async Task AdvanceDayServerOnlyAsync(CancellationToken ct)
    {
        var before = await ServerApi.GetStatus(ct);
        Assert.NotNull(before);

        await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        await ServerApi.SetClockSpeed(20, ct);
        try
        {
            var dayChanged = await DayChange.WaitAsync(before.Day, before.Season, before.Year, ct);
            Assert.True(
                dayChanged,
                $"Server-only day did not advance past {before.Season} {before.Day} Y{before.Year}."
            );
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }
    }

    private async Task AssertHomesUnchangedAsync(
        long uidA,
        string homeA,
        long uidB,
        string homeB,
        string when,
        CancellationToken ct
    )
    {
        var diag = await ServerApi.GetDiagnosticsState(ct);
        Assert.NotNull(diag);
        foreach (var (uid, expected, label) in new[] { (uidA, homeA, "A"), (uidB, homeB, "B") })
        {
            var entry = diag.FarmhandData.FirstOrDefault(f => f.UniqueMultiplayerId == uid);
            Assert.True(entry != null, $"[{when}] Farmhand {label} missing from farmhandData.");
            Assert.True(
                expected == entry!.HomeLocation,
                $"[{when}] Farmhand {label}'s homeLocation drifted from its real cabin: "
                    + $"expected '{expected}', got '{entry.HomeLocation}'. A lobby value here is "
                    + "the lobby-homed-spouses regression (client resend re-poisoning the home)."
            );
        }
    }

    /// <summary>
    /// Polls until both spouse NPCs are physically inside their couple's real cabin — the
    /// durable outcome marriageDuties produces from a healthy homeLocation. Pre-fix they'd be
    /// warped to the lobby interior's (-999,-999) instead.
    /// </summary>
    private async Task AssertSpousesInCabinsAsync(
        long uidA,
        string homeA,
        long uidB,
        string homeB,
        string when,
        CancellationToken ct
    )
    {
        var inCabins = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_LobbyHome_SpousesInCabins,
            async () =>
            {
                var a = await ServerApi.GetWeddingState(uidA, SpouseNpcA, ct);
                var b = await ServerApi.GetWeddingState(uidB, SpouseNpcB, ct);
                return a?.SpouseCurrentLocation == homeA && b?.SpouseCurrentLocation == homeB;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        if (!inCabins)
        {
            var a = await ServerApi.GetWeddingState(uidA, SpouseNpcA, ct);
            var b = await ServerApi.GetWeddingState(uidB, SpouseNpcB, ct);
            Assert.Fail(
                $"[{when}] Spouse NPCs are not inside their couples' cabins.\n"
                    + $"  {SpouseNpcA}: at '{a?.SpouseCurrentLocation}' (expected '{homeA}')\n"
                    + $"  {SpouseNpcB}: at '{b?.SpouseCurrentLocation}' (expected '{homeB}')\n"
                    + "A lobby interior here is the reported bug: marriageDuties re-homing the "
                    + "spouse into the lobby's level-0 (-999,-999) bed spot."
            );
        }
    }
}

/// <summary>
/// Covers the reconciliation layer against the reporter's actual save shape, on a PASSWORDLESS
/// server — load-bearing, because the old join-time repair lived in PasswordProtectionService,
/// whose constructor early-returns without a password (its patches never register), which is
/// exactly why disabling the password permanently disabled the only heal. The sweeps live in
/// CabinManagerService (unconditionally constructed), so they must work here.
///
/// <para>
/// /test/stamp_lobby_home reproduces the poisoned state live: a shared lobby cabin (built at the
/// lobby position; classification is position-based), a farmhand married (synthesized) to an NPC,
/// the farmhand's homeLocation/lastSleepLocation and the NPC's DefaultMap/position all pointing
/// at the lobby interior. One real day transition must heal everything (DayStarted sweep +
/// marriageDuties interplay, the reporter's live scenario), and the healed state must survive a
/// /reload (SaveLoaded sweep guards the disk path regardless of whether the nightly save was
/// written before or after the DayStarted heal).
/// </para>
/// </summary>
// Exclusive: mutates the calendar and farmhand/NPC state on the shared server.
[TestServer(Clients = 0, Isolation = IsolationMode.SharedClass, Exclusive = true)]
public class LobbyHomedSpouseHealTests : TestBase
{
    private const string SpouseNpc = "Abigail";

    [Fact]
    public async Task PoisonedLobbyHomes_HealOnDayStart_AndStayHealedAcrossReload()
    {
        var ct = TestContext.Current.CancellationToken;

        // The boot world becomes reachable (IsReady flips) a beat before its SaveLoaded
        // handlers finish, so a stamp can race the boot's own SaveLoaded sweep — which would
        // heal the poison instantly and leave the day-transition leg below testing nothing
        // (observed live: stamp at T, boot save_loaded sweep at T+0.6s). No status field
        // orders against SaveLoaded-handler completion, but /reload's completion contract
        // does: it resolves only on the tick AFTER SaveLoaded, once every reconciliation
        // handler has run. Reload first, then stamp into the quiescent world — nothing sweeps
        // again until the next SaveLoaded/DayStarted, both of which this test drives itself.
        var ready = await ServerApi.WaitForServerOnline(
            TestTimings.DayChangeTimeout,
            cancellationToken: ct,
            requireInviteCode: false
        );
        Assert.True(ready?.IsReady == true, "Server must be ready before the settle reload.");
        await ReloadServerAsync();

        // Poison: lobby cabin + synthesized married couple, both homed at the lobby.
        var stamp = await ServerApi.StampLobbyHome(SpouseNpc, ct);
        Assert.NotNull(stamp);
        Assert.True(stamp.Success, $"stamp_lobby_home failed: {stamp.Error}");
        Assert.False(
            string.IsNullOrEmpty(stamp.OriginalHome),
            "Stamp must capture the farmhand's original real-cabin home."
        );
        Assert.NotEqual(stamp.OriginalHome, stamp.LobbyLocation);

        // The poison must be visible through the API before the transition — otherwise a green
        // result would prove nothing about the heal.
        var poisoned = await ServerApi.GetDiagnosticsState(ct);
        var poisonedEntry = poisoned?.FarmhandData.FirstOrDefault(f =>
            f.UniqueMultiplayerId == stamp.StampedUid
        );
        Assert.True(
            poisonedEntry?.HomeLocation == stamp.LobbyLocation,
            $"Stamped farmhand must read lobby-homed pre-heal "
                + $"(home='{poisonedEntry?.HomeLocation}', lobby='{stamp.LobbyLocation}')."
        );
        var poisonedNpc = await ServerApi.GetWeddingState(stamp.StampedUid, SpouseNpc, ct);
        Assert.True(
            poisonedNpc?.SpouseCurrentLocation == stamp.LobbyLocation,
            $"Stamped NPC must sit in the lobby pre-heal "
                + $"(at '{poisonedNpc?.SpouseCurrentLocation}')."
        );

        // One real day transition, server alone (host passes out at 2:00 AM).
        var before = await ServerApi.GetStatus(ct);
        Assert.NotNull(before);
        await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        await ServerApi.SetClockSpeed(20, ct);
        try
        {
            var dayChanged = await DayChange.WaitAsync(before.Day, before.Season, before.Year, ct);
            Assert.True(dayChanged, "Day did not advance for the heal transition.");
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }

        // The DayStarted sweep must restore the farmhand to their OWN cabin (ownership-first
        // reassignment via the cabin's farmhandReference), scrub the lobby sleep hint, and pull
        // the NPC into that cabin.
        var healed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_LobbyHome_PoisonHealed,
            async () => await ReadCoupleHealedAsync(stamp, ct),
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        if (!healed)
        {
            await FailWithCoupleStateAsync("after the day transition", stamp, ct);
        }

        // The heal must survive a reload. /reload completion is gated on SaveLoaded (all
        // CabinManagerService handlers done), so the first post-reload read is authoritative.
        // The nightly save is written BEFORE the DayStarted sweep runs (log-verified: the
        // reloaded world re-heals with context=save_loaded), so this leg loads a genuinely
        // poisoned disk save and exercises the SaveLoaded migration — the reporter's
        // restore-a-poisoned-save scenario.
        await ReloadServerAsync();
        if (!await ReadCoupleHealedAsync(stamp, ct))
        {
            await FailWithCoupleStateAsync("after /reload", stamp, ct);
        }

        LogSuccess(
            $"Lobby-poisoned couple healed: farmhand {stamp.StampedUid} restored to its own "
                + $"cabin '{stamp.OriginalHome}' (from lobby '{stamp.LobbyLocation}'), "
                + $"{SpouseNpc} re-homed into that cabin, and the heal survived a /reload."
        );
    }

    private async Task<bool> ReadCoupleHealedAsync(
        TestStampLobbyHomeResponse stamp,
        CancellationToken ct
    )
    {
        var diag = await ServerApi.GetDiagnosticsState(ct);
        var entry = diag?.FarmhandData.FirstOrDefault(f =>
            f.UniqueMultiplayerId == stamp.StampedUid
        );
        if (entry == null)
        {
            return false;
        }

        var npcState = await ServerApi.GetWeddingState(stamp.StampedUid, SpouseNpc, ct);
        return entry.HomeLocation == stamp.OriginalHome
            && entry.LastSleepLocation == stamp.OriginalHome
            && npcState?.SpouseCurrentLocation == stamp.OriginalHome;
    }

    private async Task FailWithCoupleStateAsync(
        string when,
        TestStampLobbyHomeResponse stamp,
        CancellationToken ct
    )
    {
        var diag = await ServerApi.GetDiagnosticsState(ct);
        var entry = diag?.FarmhandData.FirstOrDefault(f =>
            f.UniqueMultiplayerId == stamp.StampedUid
        );
        var npcState = await ServerApi.GetWeddingState(stamp.StampedUid, SpouseNpc, ct);
        Assert.Fail(
            $"Lobby-poisoned couple not healed {when}.\n"
                + $"  farmhand home: '{entry?.HomeLocation}' (expected '{stamp.OriginalHome}', "
                + $"poison was '{stamp.LobbyLocation}')\n"
                + $"  farmhand lastSleep: '{entry?.LastSleepLocation}' (expected "
                + $"'{stamp.OriginalHome}')\n"
                + $"  {SpouseNpc} location: '{npcState?.SpouseCurrentLocation}' (expected "
                + $"'{stamp.OriginalHome}')"
        );
    }
}
