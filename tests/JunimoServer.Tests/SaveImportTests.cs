using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E tests for the <c>saves import</c> feature (one-shot co-op save import with optional host
/// swap). Asserts only via the server HTTP API snapshot (<c>/diagnostics/state</c>), per
/// <c>tests-assert-via-http-api.md</c>.
///
/// The source co-op save is generated in-process: create a game (the master is the "Server" bot),
/// then <c>POST /test/seed_import_source</c> makes that master look like a real played human owner
/// (non-Server name + inventory, plus the world/relationship/house state and FarmHouse contents each
/// test asserts), then <c>SleepToSaveAsync</c> writes it to disk. <c>POST /test/import_save</c> then
/// CLONES that save under a new folder name (ExecuteImport rejects importing the active save) and
/// runs the import on the clone, after which <c>ReloadServerAsync</c> loads the transformed clone and
/// runs the Layer B finalizer.
///
/// The bind on a swap comes from the import ARG, not the source save: the LAN-generated source owner
/// is customized but has <c>userID==""</c> (LAN never stamps — <c>abandoned-claim-is-steam-only.md</c>),
/// so <c>HasUserId==true</c> after reload proves Layer B re-stamped the bind, not a carried value.
///
/// Class-level <c>Exclusive</c> serializes the methods (each begins with its own
/// <c>CreateNewGameOnServerAsync</c>, so a prior test's imported active save never leaks in) — see
/// <c>test-broker-invariants.md</c>.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedClass, Exclusive = true, StartingCabins = 2)]
public class SaveImportTests : TestBase
{
    public SaveImportTests() { }

    // A synthetic all-digit Steam64-shaped id for the bind. Must be all-digit (import validates the
    // flag); NOT the lettered /test/stamp_claim literal, which import would reject.
    private const string SyntheticBindId = "76561190000000001";

    /// <summary>
    /// Generates a co-op source save on the active server: fresh game (Server master + spare cabins),
    /// then seeds the master to look like a real owner per <paramref name="seed"/>, then saves. The
    /// caller must have a connected in-world primary client (for the sleep-save) — this connects one.
    /// Returns the seed response (owner uid etc.).
    /// </summary>
    private async Task<TestSeedImportSourceResponse> GenerateSourceSaveAsync(
        TestSeedImportSourceRequest seed,
        CancellationToken ct,
        int startingCabins = 2
    )
    {
        await CreateNewGameOnServerAsync(
            farmType: 0,
            cabinStrategy: "CabinStack",
            startingCabins: startingCabins
        );

        // A connected, customized, in-world client is required for the day-transition save.
        var client = await Farmers.ConnectNewAsync(ct: ct);
        var customized = await ServerApi.WaitForFarmhandByNameAsync(
            client.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        Assert.True(customized, $"Source client '{client.FarmerName}' should be customized");

        var seedResult = await ServerApi.SeedImportSource(seed, ct);
        Assert.True(seedResult?.Success == true, $"SeedImportSource failed: {seedResult?.Error}");
        Assert.NotEqual(0, seedResult!.OwnerUid);

        // Flush to disk, then disconnect so /reload (which needs 0 clients) can run later.
        await SleepToSaveAsync(ct);
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);

        return seedResult;
    }

    /// <summary>Test 1 — swap+bind: the demoted owner becomes a bound customized cabin farmhand and a
    /// fresh "Server" master is installed.</summary>
    [Fact]
    public async Task Import_SwapHost_DemotesOwnerAndBindsUserId()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest { OwnerName = "Alice" },
            ct
        );
        var ownerUid = seed.OwnerUid;

        var import = await ServerApi.ImportSave(
            new TestImportSaveRequest { SwapHostTo = SyntheticBindId },
            ct
        );
        Assert.True(import?.Success == true, $"Import failed: {import?.Error}");
        Assert.True(import!.Swapped, "Import should report a host swap");
        Assert.Equal(ownerUid, import.FormerOwnerUid);

        await ReloadServerAsync();

        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_SwapFinalized,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }

                // Former owner is a customized + bound farmhand, present (not swept).
                var ownerEntry = state.FarmhandData.FirstOrDefault(f =>
                    f.UniqueMultiplayerId == ownerUid
                );
                if (ownerEntry == null || !ownerEntry.IsCustomized || !ownerEntry.HasUserId)
                {
                    return false;
                }

                // A cabin is owned by the former owner with the bind visible.
                var cabin = state.Cabins.FirstOrDefault(c => c.OwnerId == ownerUid);
                if (cabin == null || !cabin.OwnerHasUserId)
                {
                    return false;
                }

                // The new master is "Server" and is NOT the former owner.
                var serverMasterIsFresh = ownerEntry.Name != "Server";
                return serverMasterIsFresh;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ok,
            $"After swap import + reload, owner uid={ownerUid} must be a customized, bound cabin "
                + "farmhand and a fresh Server master must be installed"
        );
        Log($"Swap import finalized: owner uid={ownerUid} demoted + bound");
    }

    /// <summary>Test 2 — contents move: the owner's farmhouse chest + fridge item end up in their
    /// cabin and the Server-owned FarmHouse is left empty. (Co-op source: ≥2 spare cabins → the owner
    /// is auto-homed into a spare cabin → exercises the REUSE path.)</summary>
    [Fact]
    public async Task Import_SwapHost_MovesFarmhouseContentsToCabin()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest
            {
                OwnerName = "Bob",
                PlaceChest = true,
                PlaceFridgeItem = true,
            },
            ct
        );
        Assert.True(seed.ChestPlaced, "Source chest should have been placed");
        Assert.True(seed.FridgeItemPlaced, "Source fridge item should have been placed");
        var ownerUid = seed.OwnerUid;

        var import = await ServerApi.ImportSave(
            new TestImportSaveRequest { SwapHostTo = SyntheticBindId },
            ct
        );
        Assert.True(import?.Success == true, $"Import failed: {import?.Error}");

        await ReloadServerAsync();

        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_ContentsMoved,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }

                var cabin = state.Cabins.FirstOrDefault(c => c.OwnerId == ownerUid);
                if (cabin == null)
                {
                    return false;
                }

                // The owner's cabin holds the chest and the fridge item; the host FarmHouse is empty
                // (objects, fridge AND furniture — the starter furniture must have moved too).
                var cabinHasContents = cabin.ObjectCount >= 1 && cabin.FridgeItemCount >= 1;
                var farmHouseEmpty =
                    state.FarmHouseObjectCount == 0
                    && state.FarmHouseFridgeItemCount == 0
                    && state.FarmHouseFurnitureCount == 0;
                return cabinHasContents && farmHouseEmpty;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ok,
            $"After swap import, owner uid={ownerUid} cabin must hold the moved chest + fridge item "
                + "and the Server FarmHouse must be empty (objects, fridge, furniture)"
        );
        Log("Contents move confirmed (reuse path)");
    }

    /// <summary>Test 2b — upgraded-house variant through the BUILD path: a single-cabin (no spare)
    /// source with a &gt; level-0 house forces the finalizer to build the cabin and realize its map to
    /// the owner's level before the move. Asserts the placed contents survive and no starter giftbox
    /// remains.</summary>
    [Fact]
    public async Task Import_SwapHost_UpgradedHouse_BuildPath_MovesContents()
    {
        var ct = TestContext.Current.CancellationToken;
        // startingCabins:1 → no spare assignable cabin → TryAssignFarmhandHome fails → build path.
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest
            {
                OwnerName = "Carol",
                HouseUpgradeLevel = 1,
                PlaceChest = true,
                PlaceFridgeItem = true,
            },
            ct,
            startingCabins: 1
        );
        var ownerUid = seed.OwnerUid;

        var import = await ServerApi.ImportSave(
            new TestImportSaveRequest { SwapHostTo = SyntheticBindId },
            ct
        );
        Assert.True(import?.Success == true, $"Import failed: {import?.Error}");

        await ReloadServerAsync();

        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_ContentsMovedUpgraded,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }

                var cabin = state.Cabins.FirstOrDefault(c => c.OwnerId == ownerUid);
                if (cabin == null)
                {
                    return false;
                }

                // Chest + fridge item survived the level-0→level-N realization; FarmHouse empty
                // (objects, fridge AND furniture).
                var cabinHasContents = cabin.ObjectCount >= 1 && cabin.FridgeItemCount >= 1;
                var farmHouseEmpty =
                    state.FarmHouseObjectCount == 0
                    && state.FarmHouseFridgeItemCount == 0
                    && state.FarmHouseFurnitureCount == 0;
                return cabinHasContents && farmHouseEmpty;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ok,
            $"After build-path swap import of an upgraded house, owner uid={ownerUid} cabin must hold "
                + "the moved contents and the FarmHouse must be empty"
        );
        Log("Contents move confirmed (build path, upgraded house)");
    }

    /// <summary>Test 3 — pet relocation: a pet in the source FarmHouse ends up in the owner's cabin,
    /// not the Server FarmHouse. (Spouse/children relocation is CI-untested — distinct mechanisms;
    /// see the coverage TODO at the bottom of this file.)</summary>
    [Fact]
    public async Task Import_SwapHost_RelocatesPetToCabin()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest { OwnerName = "Dave", SpawnPet = true },
            ct
        );
        Assert.True(seed.PetSpawned, "Source pet should have been spawned");
        var ownerUid = seed.OwnerUid;

        var import = await ServerApi.ImportSave(
            new TestImportSaveRequest { SwapHostTo = SyntheticBindId },
            ct
        );
        Assert.True(import?.Success == true, $"Import failed: {import?.Error}");

        await ReloadServerAsync();

        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_PetRelocated,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }

                var cabin = state.Cabins.FirstOrDefault(c => c.OwnerId == ownerUid);
                if (cabin == null)
                {
                    return false;
                }

                // The PET specifically is now in the owner's cabin. Assert the pet-specific PetCount so
                // the test can only pass if the pet actually reached the cabin — not if some other NPC
                // did. (The FarmHouse pet count is not a useful signal: the pet lives on the Farm near
                // its bowl after an overnight save, so it's absent from the FarmHouse regardless. The
                // cabin PetCount is the real proof of relocation.)
                return cabin.PetCount >= 1;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ok,
            $"After swap import, the pet must be relocated into owner uid={ownerUid}'s cabin and not "
                + "remain in the Server FarmHouse"
        );
        Log("Pet relocation confirmed");
    }

    /// <summary>Test 11 — cellar-contents move: a cask the owner built in the master-keyed "Cellar"-1
    /// follows them into the cellar the engine reassigns to their demoted farmhand, and the
    /// Server-host's "Cellar"-1 is left empty. Proves cellar contents are not lost to the host
    /// (cellar contents are location-bound, so a raw assignment change wouldn't carry them).</summary>
    [Fact]
    public async Task Import_SwapHost_MovesCellarContentsToOwnerCellar()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest { OwnerName = "Liam", PlaceCellarItem = true },
            ct
        );
        Assert.True(seed.CellarItemPlaced, "Source cellar cask should have been placed");
        var ownerUid = seed.OwnerUid;

        var import = await ServerApi.ImportSave(
            new TestImportSaveRequest { SwapHostTo = SyntheticBindId },
            ct
        );
        Assert.True(import?.Success == true, $"Import failed: {import?.Error}");

        await ReloadServerAsync();

        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_CellarMoved,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }

                var cabin = state.Cabins.FirstOrDefault(c => c.OwnerId == ownerUid);
                if (cabin == null)
                {
                    return false;
                }

                // The cask reached the owner's reassigned cellar; the Server-host's "Cellar"-1 is empty.
                return cabin.CellarObjectCount >= 1 && state.MasterCellarObjectCount == 0;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ok,
            $"After swap import, owner uid={ownerUid}'s cellar must hold the moved cask and the "
                + "Server-host's main cellar must be empty"
        );
        Log("Cellar-contents move confirmed");
    }

    /// <summary>Test 4 — as-is: importing without a bind preserves the master/owner identity (no
    /// Server swap), and no finalize intent is consumed.</summary>
    [Fact]
    public async Task Import_AsIs_PreservesOwnerAsHost()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed the master as a real owner so the source isn't a clone-blank Server master; as-is
        // keeps that owner AS the host (the headline trap, which this test pins as intended for a
        // single-player-style import).
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest { OwnerName = "Erin" },
            ct
        );
        var ownerUid = seed.OwnerUid;

        var import = await ServerApi.ImportSave(new TestImportSaveRequest(), ct); // no SwapHostTo
        Assert.True(import?.Success == true, $"As-is import failed: {import?.Error}");
        Assert.False(import!.Swapped, "As-is import must not report a swap");

        await ReloadServerAsync();

        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_AsIsPreserved,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }

                // POSITIVE identity check: the master must STILL be the seeded owner "Erin" — proving
                // as-is preserved the owner AS the host, NOT installed a blank "Server" master (which a
                // wrongful swap would). (Asserting only "owner not in farmhandData" was vacuous: the
                // master is never in farmhandData regardless. MasterName is the real signal.)
                if (state.MasterName != "Erin")
                {
                    return false;
                }
                // And the owner must NOT have been demoted into a bound farmhand (belt-and-suspenders).
                var boundFarmhand = state.FarmhandData.FirstOrDefault(f =>
                    f.UniqueMultiplayerId == ownerUid && f.HasUserId
                );
                return boundFarmhand == null;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ok,
            "As-is import must leave the seeded owner 'Erin' as the host (not install a blank Server master)"
        );
        Log("As-is import preserved owner as host");
    }

    /// <summary>Test 5 — resilience (the core in-place fault-tolerance guarantee): importing a save
    /// with a malformed &lt;player&gt; fails (Warn) and leaves the target main file byte-unchanged,
    /// and a re-run after fixing the input succeeds.</summary>
    [Fact]
    public async Task Import_SwapHost_MalformedSave_LeavesFileByteUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await GenerateSourceSaveAsync(new TestSeedImportSourceRequest { OwnerName = "Frank" }, ct);

        // Clone the valid active save to a fresh folder, then corrupt the clone's <player> — one
        // step. The original active save stays intact. CorruptSave returns the actual folder name it
        // created (re-stamped uniqueID).
        var corrupt = await ServerApi.CorruptSave("import-resilience", ct);
        Assert.True(corrupt?.Success == true, $"CorruptSave failed: {corrupt?.Error}");
        var corruptedFolder = corrupt!.TargetSaveName;
        Assert.False(
            string.IsNullOrEmpty(corruptedFolder),
            "CorruptSave should return a folder name"
        );

        // Swap-import the corrupted clone (SkipClone — the corrupted bytes are what the transform
        // sees). Must fail and leave the file byte-unchanged.
        var importBad = await ServerApi.ImportSave(
            new TestImportSaveRequest
            {
                TargetSaveName = corruptedFolder,
                SwapHostTo = SyntheticBindId,
                SkipClone = true,
            },
            ct
        );
        Assert.NotNull(importBad);
        Assert.False(
            importBad!.Swapped,
            "A malformed-save import must not report a successful swap"
        );
        Assert.False(
            string.IsNullOrEmpty(importBad.ImportError),
            "A malformed-save import must report an import error"
        );
        // Byte-unchanged: the .tmp was discarded before any rename.
        Assert.Equal(importBad.PreImportMainFileHash, importBad.PostImportMainFileHash);
        Assert.False(
            string.IsNullOrEmpty(importBad.PreImportMainFileHash),
            "Pre-import hash should be non-empty (file existed)"
        );
        Log("Malformed import left the save byte-unchanged");

        // Repeatability: clone a fresh copy from the valid active source and swap-import it — succeeds
        // and leaves no .tmp behind. Use the actual folder the import created for the no-junk check.
        var importGood = await ServerApi.ImportSave(
            new TestImportSaveRequest
            {
                TargetSaveName = "import-resilience-ok",
                SwapHostTo = SyntheticBindId,
            },
            ct
        );
        Assert.True(
            importGood?.Success == true && importGood.Swapped,
            $"Re-run on a valid save should succeed: {importGood?.ImportError ?? importGood?.Error}"
        );
        var leftover = await ServerApi.SaveTmpExists(importGood!.TargetSaveName, ct);
        Assert.False(
            leftover?.Exists == true,
            "A successful import must leave no .tmp on the volume"
        );
        Log("Re-run on a fixed input succeeded (repeatable, no junk)");
    }

    /// <summary>Tests 7/7b/7c/8 (merged) — the blank Server master's Layer-A field handling, all of
    /// which are independent of the finalizer and observable after one swap+reload, so one game-create
    /// covers them. Asserts the three master-gated bucket-B fields are KEPT (mail/event flag,
    /// caveChoice, the keyed Krobus friendship + stats.DaysPlayed — risk #9), AND the relationship
    /// bucket-A field is CLEARED (spouse — risk #1; the demoted owner keeps theirs). Each property is
    /// a distinct <c>Assert</c> so a regression names exactly which bucket decision broke.</summary>
    [Fact]
    public async Task Import_SwapHost_CarriesMasterGatedState()
    {
        var ct = TestContext.Current.CancellationToken;
        const string mailFlag = "ccDoorUnlock";
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest
            {
                OwnerName = "Grace",
                MailFlag = mailFlag,
                EventSeen = "191393",
                CaveChoice = 1, // bat/fruit cave
                ShadowFriendshipPoints = 1300, // > 1250 (Dark Shrine pacifism gate)
                DaysPlayed = 42,
                Spouse = "Abigail",
            },
            ct
        );
        var ownerUid = seed.OwnerUid;

        var import = await ServerApi.ImportSave(
            new TestImportSaveRequest { SwapHostTo = SyntheticBindId },
            ct
        );
        Assert.True(import?.Success == true, $"Import failed: {import?.Error}");

        await ReloadServerAsync();

        // One diagnostics read carries BOTH query params (?masterFlag for the mail bit,
        // ?masterFriendKey for the keyed friendship). Poll until the snapshot is populated, then make
        // each assertion separately so a failure pinpoints the exact regressed field.
        DiagnosticsStateResponse? state = null;
        var ready = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_MasterGatedState,
            async () =>
            {
                state = await ServerApi.GetDiagnosticsState(
                    mailFlag,
                    ct,
                    masterFriendKey: "Krobus"
                );
                // Gate on the load completing: the master must be the fresh "Server" host.
                return state is { MasterName: "Server" };
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(ready, "Swap import + reload did not produce a 'Server' master in time");

        // Bucket B — KEEP (master-gated world-state; zeroing any reverts the imported world):
        Assert.True(
            state!.MasterHasFlag == true,
            $"Master must carry the seeded mail flag '{mailFlag}' (mail/event store — CC/Joja/island geometry)"
        );
        Assert.Equal(1, state.MasterCaveChoice); // bat/fruit-cave daily regen
        Assert.True(
            state.MasterShadowFriendshipPoints is >= 1250,
            $"Master must carry the Krobus friendship (shadow-pacifism gate), got "
                + $"{state.MasterShadowFriendshipPoints?.ToString() ?? "null"}"
        );
        Assert.True(
            state.MasterDaysPlayed >= 42,
            $"Master must carry stats.DaysPlayed (same-day-reconnect gate), got {state.MasterDaysPlayed}"
        );

        // Bucket A — CLEAR (relationship state; a non-cleared spouse = duplicate/dangling marriage):
        Assert.False(
            state.MasterHasSpouse,
            "Master spouse must be CLEARED (no duplicate marriage with the owner's NPC)"
        );
        // ...and the demoted owner (who KEEPS their spouse) is present in farmhands.
        Assert.True(
            state.FarmhandData.Any(f => f.UniqueMultiplayerId == ownerUid),
            $"Demoted owner uid={ownerUid} must be present in farmhands"
        );
        Log("Master gated-state carry + spouse-clear confirmed");
    }

    /// <summary>Test 9 — finalizer is single-shot across reloads (risk #10): a successful swap import
    /// finalizes exactly once. The intent is cleared in the finalize's <c>finally</c>, so a second
    /// reload reads no pending intent and does NOT re-run — proven by the process-wide
    /// <c>SaveImportFinalizeCount</c> going +1 on the first reload and STAYING (a re-fire would bump
    /// it), not merely by the owner staying customized+bound (which a harmless re-run would also leave
    /// intact). Without the <c>finally</c>, the second boot would re-finalize against an emptied
    /// FarmHouse on every reload forever.
    /// TODO: the mid-finalize-THROW partial-failure path (a throw after the contents move leaving a
    /// stable but partially-moved world) is NOT exercised here — it would need a test-only fault-
    /// injection knob in the finalizer, deliberately not wired to keep test-only code out of the
    /// production path. Manual-verification gap; this test covers the success-path single-shot only.</summary>
    [Fact]
    public async Task Import_SwapHost_FinalizeIsSingleShot_AcrossReloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest { OwnerName = "Judy", PlaceChest = true },
            ct
        );
        var ownerUid = seed.OwnerUid;

        // Capture the process-wide finalize counter BEFORE the import (the server may be reused across
        // this Exclusive class's methods, so use a RELATIVE delta, not an absolute count of 1).
        var preState = await ServerApi.GetDiagnosticsState(ct);
        Assert.NotNull(preState);
        var baseFinalizeCount = preState!.SaveImportFinalizeCount;

        var import = await ServerApi.ImportSave(
            new TestImportSaveRequest { SwapHostTo = SyntheticBindId },
            ct
        );
        Assert.True(import?.Success == true, $"Import failed: {import?.Error}");

        // First reload runs the finalizer exactly once.
        await ReloadServerAsync();

        // After the first reload: owner demoted + bound, FarmHouse empty, and the finalizer ran ONCE.
        var firstOk = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_PartialThenStable,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }
                var ownerEntry = state.FarmhandData.FirstOrDefault(f =>
                    f.UniqueMultiplayerId == ownerUid
                );
                return ownerEntry is { IsCustomized: true, HasUserId: true }
                    && state.FarmHouseObjectCount == 0
                    && state.SaveImportFinalizeCount == baseFinalizeCount + 1;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            firstOk,
            "First reload should finalize exactly once: owner demoted+bound, FarmHouse emptied, "
                + "finalize count +1"
        );

        // Second reload: the intent was cleared in the finally, so the finalizer must NOT re-run.
        // The finalize counter must stay at base+1 (a re-fire would bump it to +2) — this is the
        // genuine single-shot proof, distinct from the owner merely staying customized+bound (which a
        // harmless re-run would also leave intact).
        await ReloadServerAsync();
        var secondOk = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_SaveImport_SecondReloadNoop,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                if (state == null)
                {
                    return false;
                }
                var ownerEntry = state.FarmhandData.FirstOrDefault(f =>
                    f.UniqueMultiplayerId == ownerUid
                );
                return ownerEntry is { IsCustomized: true, HasUserId: true }
                    && state.SaveImportFinalizeCount == baseFinalizeCount + 1;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            secondOk,
            "Second reload must be a clean no-op (intent cleared in finally; no re-finalize loop)"
        );
        Log("Finalizer single-shot across reloads confirmed");
    }

    /// <summary>Test 10 — userID-collision guard (risk #12): a swap whose bind id collides with an
    /// existing farmhand's userID is rejected (byte-unchanged), while a non-colliding id succeeds.</summary>
    [Fact]
    public async Task Import_SwapHost_RejectsUserIdCollision()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed a spare uncustomized farmhand slot with SyntheticBindId (before the save, so the clone
        // carries it). A swap binding the SAME id must then be rejected by the cross-farmhand
        // userID-collision walk (LAN won't stamp a userID on its own).
        var seed = await GenerateSourceSaveAsync(
            new TestSeedImportSourceRequest
            {
                OwnerName = "Karl",
                InjectFarmhandUserId = SyntheticBindId,
            },
            ct
        );
        Assert.True(seed.FarmhandUserIdInjected, "Collision userID should have been injected");

        var importBad = await ServerApi.ImportSave(
            new TestImportSaveRequest
            {
                TargetSaveName = "import-collision",
                SwapHostTo = SyntheticBindId,
            },
            ct
        );
        Assert.NotNull(importBad);
        Assert.False(importBad!.Swapped, "A colliding-id import must not swap");
        Assert.False(
            string.IsNullOrEmpty(importBad.ImportError),
            "A colliding-id import must report an error naming the conflict"
        );
        Assert.Equal(importBad.PreImportMainFileHash, importBad.PostImportMainFileHash);
        Log("userID-collision rejected, save byte-unchanged");

        // A non-colliding id succeeds.
        var importGood = await ServerApi.ImportSave(
            new TestImportSaveRequest
            {
                TargetSaveName = "import-collision-ok",
                SwapHostTo = "76561190000000002",
            },
            ct
        );
        Assert.True(
            importGood?.Success == true && importGood.Swapped,
            $"A non-colliding id should succeed: {importGood?.ImportError ?? importGood?.Error}"
        );
        Log("Non-colliding id accepted");
    }

    // ── CI coverage gaps (documented, not faked) ──────────────────────────────────────
    //
    // TODO: Test 6 (1.5-origin fold) — needs a real pre-1.6 co-op save fixture at
    //   tests/JunimoServer.Tests/Fixtures/SaveImport/legacy-1.5-coop/. In-process generation only
    //   produces a 1.6 save, so the engine MigrateFarmhands fold (SaveMigrator_1_6.cs:1716-1730) is
    //   not exercised by CI. The transform is compatible with both shapes (Layer A only touches
    //   <player>/<farmhands>; the fold only touches cabins' obsolete_farmhand), but verify on a real
    //   1.5 save when one can be sourced. Manual-verification step until then.
    //
    // TODO: spouse + children NPC relocation into the cabin (Layer B step 6) is a DISTINCT mechanism
    //   from the pet path (Test 3): the child path is Farmer.getChildren() →
    //   Utility.getHomeOfFarmer(this).getChildren() filtering Child NPCs by the FarmHouse characters
    //   list + the Child.idOfParent re-stamp ordering; the spouse path relies on marriageDuties
    //   self-relocation. Generating a married-with-kids source in-process is heavy. Test 8 covers the
    //   master-spouse-CLEAR half (the part the clone-blank could get wrong); the NPC-object
    //   relocation into the cabin is a manual-verification gap.
    //
    // TODO: full Community-Center-revert behavior (doors relock / greenhouse → ruins on a zeroed
    //   master) is too heavy to generate in-process. Test 7 covers the MECHANISM (the master carries
    //   the mail flag); the world-geometry consequence is a manual-verification step.
    //
    // TODO: full player-to-player marriage survival across the swap needs two customized farmhands;
    //   Test 8 covers only the master-clear half. p2p-marriage survival is a manual-verification gap.
}
