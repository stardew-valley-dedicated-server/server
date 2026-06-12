using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Regression tests for the abandoned "New Farmer" slot bug: a player clicks "New Farmer"
/// (which makes their client stamp its platform ID onto the slot's farmhand via vanilla
/// Client.sendPlayerIntroduction) but quits before finishing character customization. The
/// slot's farmhand is left with userID set but isCustomized=false, and vanilla FarmhandMenu
/// then greys the slot out and blocks clicks for every other player — permanently locking it
/// to the ghost.
///
/// The fix (CabinManagerService.TryClearAbandonedClaim) clears the userID on the persisted
/// farmhandData entry from the all-transport disconnect hook (CabinManagerService's own
/// always-on GameServer.playerDisconnected postfix), which fires on every transport and
/// regardless of password protection.
///
/// Assertions are server-authoritative via /diagnostics/state Cabins, whose OwnerHasUserId is
/// resolved through cabin.owner — the live otherFarmers copy while connected (where the
/// in-flight userID stamp lives), then the persisted farmhandData copy after the player is
/// removed. (The endpoint exposes a bool, not the raw ID, since it is unauthenticated.) The
/// grey-out itself is client-side render and /cabins AvailableCount can't distinguish a stuck
/// slot, so the userID-present flag is the deterministic signal.
///
/// Transport: this requires Steam (WithSteam=true) — vanilla only stamps userID when
/// getUserID() != "", and LidgrenClient (LAN) returns "", so LAN is immune to the bug and
/// can't reproduce it.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class AbandonedClaimTests : TestBase
{
    public AbandonedClaimTests() { }

    /// <summary>
    /// Disconnect path: a client claims a slot (stamping userID), reaches the character menu,
    /// and disconnects without customizing. The all-transport disconnect hook must clear the
    /// userID on the PERSISTED farmhandData entry — not just the live otherFarmers copy that is
    /// discarded moments later by removeDisconnectedFarmers. The step-3 assertion runs after the
    /// player is removed from /players, so /diagnostics/state then reports the persisted entry:
    /// a pre-fix cabin.owner-only clear leaves it stuck and FAILS this test.
    /// </summary>
    // WithSteam is required: vanilla only stamps userID when getUserID() != "", and
    // LidgrenClient (LAN) returns "" — so the abandoned-claim bug is structurally
    // impossible to reproduce live on LAN. A real Steam client stamps a real platform ID.
    [Fact]
    [TestServer(WithSteam = true)]
    public async Task AbandonedClaim_OnDisconnect_IsClearedDurably()
    {
        var ct = TestContext.Current.CancellationToken;

        // Connect to the FarmhandMenu without customizing. WithRetryAsync is transport-aware
        // (Steam invite code vs LAN address) and uses the primary client; it stops at the menu.
        var connect = await Connect.WithRetryAsync(ct);
        Connect.AssertConnectionSuccess(connect);

        // Pick an uncustomized slot.
        var slot = connect.Farmhands?.Farmhands.FirstOrDefault(s => !s.IsCustomized && !s.IsEmpty);
        Assert.NotNull(slot);

        // Select it — slot.Activate() runs sendPlayerIntroduction, which on Steam stamps userID.
        var select = await GameClient.Farmhands.Select(slot!.Index);
        Assert.True(select?.Success == true, $"Select slot failed: {select?.Error}");

        // Wait for the character menu: userID is now stamped, isCustomized still false.
        var charMenu = await GameClient.Wait.ForCharacter(TestTimings.CharacterMenuTimeout, ct);
        Assert.True(charMenu?.Success == true, $"Character menu did not appear: {charMenu?.Error}");

        // Confirm the stuck state on the server and capture the slot's owner uid. We read via
        // /diagnostics/state Cabins (cabin.owner), which resolves to the LIVE otherFarmers copy
        // while the client is connected — that's where the in-flight userID stamp lives before
        // disconnect persists it to farmhandData (FarmhandData[].HasUserId would still be false here).
        long stuckUid = 0;
        var reproduced = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_AbandonedClaim_StuckStateReproduced,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                var stuck = state?.Cabins.FirstOrDefault(c =>
                    c.OwnerHasUserId && !c.OwnerIsCustomized
                );
                if (stuck == null)
                {
                    return false;
                }

                stuckUid = stuck.OwnerId;
                return true;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            reproduced,
            "Expected a cabin owner with userID set but isCustomized=false (abandoned claim) before disconnect"
        );
        Log($"Reproduced abandoned claim on uid={stuckUid}");

        // Disconnect mid-customization (ExitToTitle works from the character menu).
        await DisconnectAsync();

        // Wait past removeDisconnectedFarmers so cabin.owner now resolves to the PERSISTED entry.
        var removed = await ServerApi.WaitForPlayerRemovedByIdAsync(stuckUid, ct: ct);
        Assert.True(
            removed,
            $"Player uid={stuckUid} was not removed from /players after disconnect"
        );

        // Regression gate: the persisted entry's userID must be cleared. A pre-fix
        // cabin.owner-only clear (live copy) leaves the persisted entry stuck and fails here.
        var healed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_AbandonedClaim_DisconnectHealConfirmed,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false; // transient null must not read as "healed"
                }

                var cabin = state.Cabins.FirstOrDefault(c => c.OwnerId == stuckUid);
                var entry = state.FarmhandData.FirstOrDefault(f =>
                    f.UniqueMultiplayerId == stuckUid
                );
                // Healed when no surviving view of the slot still carries the userID claim.
                var cabinClear = cabin == null || !cabin.OwnerHasUserId;
                var farmhandClear = entry == null || !entry.HasUserId;
                return cabinClear && farmhandClear;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            healed,
            $"Abandoned claim on uid={stuckUid} was not cleared on the persisted farmhandData "
                + $"entry after disconnect (the disconnect hook must clear farmhandData, not just the "
                + $"discarded live otherFarmers copy)"
        );
        Log($"Disconnect heal confirmed durable for uid={stuckUid}");
    }

    /// <summary>
    /// Save-load sweep path: a stuck-but-homed abandoned claim that reaches a save on disk (e.g. a
    /// host crash before the disconnect heal ran, or a save written by a build predating the heal)
    /// must be released on the next reload. Vanilla's load-time ResetFarmhandState does NOT clear it
    /// — it clears userID only when the farmhand has no valid home cabin (the else-branch when
    /// TryAssignFarmhandHome fails), and a homed slot takes the early-return branch with userID
    /// intact — so only CabinManagerService.ClearAbandonedCabinClaimsOnLoad heals this case.
    ///
    /// No Steam required: the live disconnect heal always clears a claim before any save, so a stuck
    /// claim can't reach disk through a normal client flow. Instead /test/stamp_claim constructs the
    /// exact on-disk shape (userID set + isCustomized=false + homeLocation resolving to a cabin)
    /// server-side; a connected client then sleeps to flush the save to disk, and the reload exercises
    /// the sweep. The stamp is synthetic, so this needs no real platform-ID stamp (unlike the
    /// disconnect test).
    /// </summary>
    [Fact]
    [TestServer(StartingCabins = 2)]
    public async Task AbandonedClaim_SweptOnReload()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateNewGameOnServerAsync(
            farmType: 0,
            cabinStrategy: "CabinStack",
            startingCabins: 2
        );

        // A customized in-world player is needed to trigger the day-transition save (the stuck slot
        // itself can't sleep). Its own entry must survive the sweep untouched (isCustomized guard).
        var client = await Farmers.ConnectNewAsync(ct: ct);
        var customizedUid = client.JoinResult.UniqueMultiplayerId;

        // Wait until A's farmhand is customized server-side BEFORE stamping. The join gate returns as
        // soon as the peer is added — seconds before the character XML round-trip sets isCustomized
        // (see FarmerTestHelper.DisconnectAndWaitForPersistenceAsync). Without this wait, /test/stamp_claim
        // sees A's slot as still-uncustomized and stamps A itself instead of the spare slot.
        var customized = await ServerApi.WaitForFarmhandByNameAsync(
            client.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        Assert.True(
            customized,
            $"Player '{client.FarmerName}' should be customized server-side before stamping"
        );

        // Construct the stuck-but-homed claim on a spare uncustomized slot, server-side.
        var stamp = await ServerApi.StampClaim(ct);
        Assert.True(stamp?.Success == true, $"StampClaim failed: {stamp?.Error}");
        var stuckUid = stamp!.StampedUid;
        Assert.NotEqual(0, stuckUid);
        Assert.NotEqual(customizedUid, stuckUid); // must be a different slot than the real player
        Assert.False(
            string.IsNullOrEmpty(stamp.HomeLocation),
            "Stamped slot must be homed (homeLocation resolving to a cabin) to exercise the homed sweep path"
        );
        Log(
            $"Stamped abandoned claim on uid={stuckUid} (userId='{stamp.StampedUserId}', home='{stamp.HomeLocation}')"
        );

        // Confirm the stuck state is present server-side before the save.
        var preState = await ServerApi.GetDiagnosticsState(ct);
        var preEntry = preState?.FarmhandData.FirstOrDefault(f =>
            f.UniqueMultiplayerId == stuckUid
        );
        Assert.NotNull(preEntry);
        Assert.True(preEntry!.HasUserId, "Stamped slot should carry a userId before save");
        Assert.False(
            preEntry.IsCustomized,
            "Stamped slot must be uncustomized (abandoned-claim shape)"
        );

        // Flush the stuck claim to disk via a day-transition save, then disconnect (/reload needs 0
        // clients) and reload — which loads the on-disk save and re-runs OnSaveLoaded → the sweep.
        await SleepToSaveAsync(ct);
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
        await ReloadServerAsync();

        // Regression gate: after reload the sweep must have released the stuck claim. Without the
        // sweep, the homed userID survives reload (vanilla preserves it) and this FAILS.
        DiagnosticsStateResponse? postState = null;
        var swept = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_AbandonedClaim_SweptOnReload,
            async () =>
            {
                postState = await ServerApi.GetDiagnosticsState(ct);
                if (postState == null)
                {
                    return false; // transient null must not read as "swept"
                }

                var entry = postState.FarmhandData.FirstOrDefault(f =>
                    f.UniqueMultiplayerId == stuckUid
                );
                // Cleared when the slot's entry is gone or no longer carries the userID claim.
                return entry == null || !entry.HasUserId;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            swept,
            $"Abandoned claim on uid={stuckUid} was not released by the save-load sweep after reload "
                + $"(ClearAbandonedCabinClaimsOnLoad must clear a stuck-but-homed claim that vanilla's "
                + $"load-time ResetFarmhandState leaves intact)"
        );
        Log($"Save-load sweep confirmed for uid={stuckUid}");

        // Guard: the sweep must NOT touch the real customized player's entry (isCustomized guard).
        var survivor = postState?.FarmhandData.FirstOrDefault(f =>
            f.UniqueMultiplayerId == customizedUid
        );
        Assert.NotNull(survivor);
        Assert.True(
            survivor!.IsCustomized,
            $"Customized player uid={customizedUid} must survive the sweep as a customized farmhand"
        );
    }
}
