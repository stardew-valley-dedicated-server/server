using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E tests for transport-scoped farmhand visibility and server-authoritative ownership
/// (FarmhandOwnershipService gate/recorder + FarmhandSenderService's visibility filter).
///
/// The farmhand list a client receives is now scoped by the connection's transport identity:
/// platform-stamped or ownership-mapped slots are hidden from LAN clients, IP-created
/// (customized, unstamped, unmapped) farmhands are hidden from platform clients, and a
/// map-owned farmhand is visible only to its recorded owner. Ownership is recorded
/// server-side at the join gate's approve moment from the connection's transport identity
/// (SDR Steam64 or Galaxy uint64), never from client-declared data. In this harness the
/// Steam-build test clients always reach the server via Galaxy P2P — SDR is never used —
/// so recorded platforms are "galaxy"; the SN_ branch shares the parser/recorder/gate path.
///
/// Slot presence/absence is asserted by UniqueMultiplayerId on the client's slot list
/// (uncustomized slots have no name to match on); ownership state is asserted via
/// /diagnostics/state (HasOwner/OwnerPlatform booleans — tests-assert-via-http-api).
///
/// The Steam tests deliberately use the plain <c>[TestServer(WithSteam = true)]</c> shared
/// steam config (SharedAssembly, no overrides, no /newgame) like every other steam test: each
/// host slice partitions off one steam server account (a cost policy — see
/// ResourceRequirements.FromAttribute), so forking the config wedges prestart on a
/// single-slice run. All assertions are uid-scoped, so a shared world is fine.
/// Mixed-transport joins work because test servers always run with AllowIpConnections; the
/// production IP-off posture is exercised via the runtime /test/set_ip_connections toggle.
///
/// Exclusive: these tests JOIN farmhand slots, and a mid-join farmhand is briefly
/// stamped-but-uncustomized — the exact shape AbandonedClaim_OnDisconnect's stuck-state poll
/// matches on the same shared steam server. Exclusive serializes against it (and against any
/// other joiner), closing that cross-class race. (No KeepConnected class shares these
/// configs, so Exclusive is deadlock-safe per test-broker-invariants.)
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Exclusive = true)]
public class FarmhandVisibilityTests : TestBase
{
    public FarmhandVisibilityTests() { }

    /// <summary>
    /// A platform-stamped (uncustomized) slot must be hidden from a LAN client's list — LAN
    /// carries no identity, so it can never be the stamp's owner. The fresh slot stays
    /// available. Pre-fix, the stamped slot could be the one exposed slot (enumeration-order
    /// dependent) and LAN clients could claim it — the vanilla free-for-all this closes.
    ///
    /// SharedClass isolation gives this test its own LAN server instance: the /newgame +
    /// stamp + list sequence must not interleave with the other classes on the shared
    /// lan-c2 config (SaveImportTests, AbandonedClaimTests) that also reset the world.
    /// </summary>
    [Fact]
    [TestServer(Isolation = IsolationMode.SharedClass, StartingCabins = 2)]
    public async Task LanList_ExcludesPlatformStampedSlot()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(
            farmType: 0,
            cabinStrategy: "CabinStack",
            startingCabins: 2
        );

        // Stamp one of the two spare slots with a synthetic platform userID (no map entry —
        // this exercises the legacy-stamp row of the visibility matrix).
        var stamp = await ServerApi.StampClaim(ct);
        Assert.True(stamp?.Success == true, $"StampClaim failed: {stamp?.Error}");
        var stampedUid = stamp!.StampedUid;
        Assert.NotEqual(0, stampedUid);

        // Fresh LAN connect: the stamped slot must be absent, a fresh slot present.
        var connect = await Connect.WithRetryAsync(ct);
        Connect.AssertConnectionSuccess(connect);
        var slots = connect.Farmhands!.Farmhands;

        Assert.DoesNotContain(slots, s => s.UniqueMultiplayerId == stampedUid);
        Assert.Contains(
            slots,
            s => !s.IsCustomized && !s.IsEmpty && s.UniqueMultiplayerId != stampedUid
        );
        Log($"LAN list excluded stamped slot uid={stampedUid} and offered a fresh slot");
    }

    /// <summary>
    /// The returning-platform-player resume path, E2E for the first time: a Steam-build client
    /// joins a fresh slot and customizes (the gate recorder maps the farmhand to the
    /// connection's transport identity at the approve moment — asserted via HasOwner), then
    /// disconnects, and on reconnect sees its own farmhand in the list and is approved to
    /// rejoin it. Proves recorder + gate together, where vanilla had zero join-time
    /// enforcement on identity-bearing transports whose stamps don't match connection ids.
    ///
    /// Transport note: in this harness the Steam-build client reaches the server via Galaxy
    /// P2P (a GN_ connection, platform "galaxy"), never Steam SDR — so OwnerPlatform is
    /// asserted non-empty rather than pinned to "steam". The SN_ (SDR) branch rides the same
    /// parser/recorder/gate path.
    /// </summary>
    [Fact]
    [TestServer(WithSteam = true)]
    public async Task SteamReturningPlayer_SeesAndRejoinsOwnFarmhand()
    {
        var ct = TestCt;

        // The S-code is gated on the Galaxy lobby carrying the SteamLobbyId stamp (not on
        // GameServer init). By now the server is long booted, so the code must be exposed —
        // this catches the gate ever wedging shut (the harness would silently fall back to
        // the GOG code and every other assertion would still pass).
        var status = await ServerApi.GetStatus(ct);
        Assert.False(
            string.IsNullOrEmpty(status?.SteamInviteCode),
            "SteamInviteCode should be exposed once the Galaxy lobby carries the SteamLobbyId stamp"
        );

        // No /newgame: this runs on the shared steam server (see class doc); all assertions
        // are scoped to this test's own farmhand uid, and the cabin pool replenishes fresh
        // slots on every connect.
        // Join + customize via the Steam primary client.
        var client = await Farmers.ConnectNewAsync(ct: ct);
        var uid = client.JoinResult.UniqueMultiplayerId;

        // Recorder proof: an ownership record with a platform tag exists server-side. This is
        // the observable "gate approved and recorded from the transport" signal — only the
        // approve-wrapped recorder writes platform records for a live join. (Platform is
        // "galaxy" in this harness, see the transport note above; asserted non-empty.)
        string ownerPlatform = "";
        var recorded = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_FarmhandVisibility_OwnershipRecorded,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                var entry = state?.FarmhandData.FirstOrDefault(f => f.UniqueMultiplayerId == uid);
                if (entry is not { HasOwner: true } || string.IsNullOrEmpty(entry.OwnerPlatform))
                {
                    return false;
                }

                ownerPlatform = entry.OwnerPlatform;
                return true;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            recorded,
            $"Farmhand uid={uid} should have a platform ownership record after the approved join"
        );
        Log($"Ownership recorded for uid={uid} (platform={ownerPlatform})");

        // Disconnect and wait for server-side removal + persisted customization.
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
        var removed = await ServerApi.WaitForPlayerRemovedByIdAsync(uid, ct: ct);
        Assert.True(removed, $"Player uid={uid} was not removed from /players after disconnect");

        // Reconnect (same client container → same platform account/identity): own farmhand
        // visible in the list.
        var connect = await Connect.WithRetryAsync(ct);
        Connect.AssertConnectionSuccess(connect);
        var own = connect.Farmhands!.Farmhands.FirstOrDefault(s => s.UniqueMultiplayerId == uid);
        Assert.NotNull(own);
        Assert.True(own!.IsCustomized, "Own farmhand should be listed as customized on resume");

        // Rejoin approved by the gate (map matches the connection's transport identity).
        // Return to title first — ReconnectAsync (unlike ConnectNewAsync) doesn't ensure a
        // disconnect, and the list check above parked the client at the farmhand menu.
        await Connect.EnsureDisconnectedAsync();
        var rejoin = await Farmers.ReconnectAsync(client.FarmerName, ct: ct);
        Assert.Equal(uid, rejoin.JoinResult.UniqueMultiplayerId);
        Log($"Returning platform player resumed own farmhand uid={uid} (visible + approved)");
    }

    /// <summary>
    /// Mixed-transport scoping on one server (the steam config always has IP connections
    /// enabled): an IP-created farmhand is hidden from the Steam client's list, and the
    /// Steam-owned (mapped) farmhand is hidden from a fresh LAN client's list — while the IP
    /// farmhand stays in the LAN pool.
    /// </summary>
    [Fact]
    [TestServer(WithSteam = true)]
    public async Task MixedTransports_HideCrossTransportFarmhands()
    {
        var ct = TestCt;

        // No /newgame: shared steam server (see class doc), uid-scoped assertions only.

        // Pin the primary (steam-capable) client FIRST — the standard multi-client shape.
        // Leasing the second farmer first can hand the pool's only steam-bearing client to
        // LAN duty, leaving the later steam-required primary lease to starve on the steam
        // ticket (the ClientPool wait wedged a full run this way).
        await GetClientAsync(ct);

        // 1. IP farmhand: a LAN second farmer joins + customizes, then disconnects.
        long lanUid;
        string lanName;
        await using (var lanFarmer = await Farmers.ConnectSecondFarmerAsync(ct: ct))
        {
            lanUid = lanFarmer.Uid;
            lanName = lanFarmer.FarmerName;
            // Wait on /farmhands (the LIVE farmer copy) — /diagnostics/state FarmhandData is
            // the persisted farmhandData entry, which stays uncustomized until the disconnect
            // saveFarmhand clone (cabin-system invariant 7).
            var customized = await ServerApi.WaitForFarmhandByNameAsync(
                lanName,
                requireCustomized: true,
                ct: ct
            );
            Assert.True(
                customized,
                $"LAN farmhand '{lanName}' (uid={lanUid}) should be customized server-side before disconnect"
            );
        }
        var lanRemoved = await ServerApi.WaitForPlayerRemovedByIdAsync(lanUid, ct: ct);
        Assert.True(lanRemoved, $"LAN player uid={lanUid} was not removed after disconnect");

        // 2. Steam client's list must NOT contain the IP farmhand; a fresh slot is offered.
        var steamConnect = await Connect.WithRetryAsync(ct);
        Connect.AssertConnectionSuccess(steamConnect);
        var steamSlots = steamConnect.Farmhands!.Farmhands;
        Assert.DoesNotContain(steamSlots, s => s.UniqueMultiplayerId == lanUid);
        Assert.Contains(steamSlots, s => !s.IsCustomized && !s.IsEmpty);
        Log($"Steam list excluded IP farmhand uid={lanUid}");

        // 3. Steam-build client joins a fresh slot + customizes (mapped at approve via its
        // transport identity — "galaxy" in this harness, see the class doc), disconnects.
        // The ownership map is process state (valid while connected), so it can be polled via
        // diagnostics; customization is waited on inside DisconnectAndWaitForPersistenceAsync
        // via /farmhands (the live copy — see the step-1 note).
        var steamClient = await Farmers.ConnectNewAsync(ct: ct);
        var steamUid = steamClient.JoinResult.UniqueMultiplayerId;
        var steamMapped = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_FarmhandVisibility_SteamFarmhandMapped,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                var entry = state?.FarmhandData.FirstOrDefault(f =>
                    f.UniqueMultiplayerId == steamUid
                );
                return entry is { HasOwner: true };
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            steamMapped,
            $"Platform farmhand uid={steamUid} should be ownership-mapped after the approved join"
        );
        await Farmers.DisconnectAndWaitForPersistenceAsync(steamClient.FarmerName, ct);
        var steamRemoved = await ServerApi.WaitForPlayerRemovedByIdAsync(steamUid, ct: ct);
        Assert.True(steamRemoved, $"Steam player uid={steamUid} was not removed after disconnect");

        // 4. Fresh LAN list: the Steam-owned farmhand is hidden; the IP farmhand is visible.
        var lanLease = await LeaseClientForHelperAsync(ct);
        await using (lanLease)
        {
            var conn = new ConnectionHelper(
                lanLease.Client,
                new ConnectionOptions(),
                serverApi: ServerApi
            );
            var lanConnect = await conn.ConnectViaLanAsync(
                Lease!.ServerLanAddress,
                Lease.ServerLanPort,
                ct
            );
            Assert.True(lanConnect.Success, $"Fresh LAN connect failed: {lanConnect.Error}");
            var lanSlots = lanConnect.Farmhands!.Farmhands;
            Assert.DoesNotContain(lanSlots, s => s.UniqueMultiplayerId == steamUid);
            Assert.Contains(lanSlots, s => s.UniqueMultiplayerId == lanUid);
            Log(
                $"Fresh LAN list excluded steam-owned uid={steamUid} and kept IP farmhand uid={lanUid}"
            );
        }
    }

    /// <summary>
    /// <c>farmhand release</c> on a customized, platform-owned slot: the slot becomes claimable
    /// by the next player on ANY transport — visible to a fresh LAN client (pre-release the
    /// map-owned row hid it), and re-claimable through the platform door, where the first
    /// approved claim re-records ownership and clears the released marker. Also guards the
    /// no-time-advance contract of the release-triggered immediate save (empty server → the
    /// stamp change is written to disk on the spot without a day transition).
    /// </summary>
    [Fact]
    [TestServer(WithSteam = true)]
    public async Task ReleasedFarmhand_ClaimableOnAnyTransport_FirstClaimBecomesOwner()
    {
        var ct = TestCt;
        await GetClientAsync(ct);

        // Platform client (galaxy transport in this harness) creates + customizes → mapped.
        var client = await Farmers.ConnectNewAsync(ct: ct);
        var uid = client.JoinResult.UniqueMultiplayerId;
        Assert.True(
            await WaitOwnershipStateAsync(
                uid,
                e => e is { HasOwner: true },
                WaitName.Polling_FarmhandVisibility_ReleaseMapped,
                ct
            ),
            $"Farmhand uid={uid} should be ownership-mapped after the approved join"
        );
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
        Assert.True(
            await ServerApi.WaitForPlayerRemovedByIdAsync(uid, ct: ct),
            $"Player uid={uid} was not removed after disconnect"
        );

        var dayBefore = (await ServerApi.GetDiagnosticsState(ct))!.DayOfMonth;

        var cmd = await ServerApi.RunConsoleCommand(
            "farmhand",
            new[] { "release", uid.ToString() },
            ct
        );
        Assert.True(cmd?.Success == true, $"Console command dispatch failed: {cmd?.Error}");
        Assert.True(
            await WaitOwnershipStateAsync(
                uid,
                e => e is { Released: true, HasOwner: false, IsCustomized: true, HasUserId: false },
                WaitName.Polling_FarmhandVisibility_Released,
                ct
            ),
            $"Farmhand uid={uid} should be marked released (no owner, customization intact)"
        );

        // The release-triggered save on the empty server must not advance the day.
        Assert.Equal(dayBefore, (await ServerApi.GetDiagnosticsState(ct))!.DayOfMonth);
        Log($"Released farmhand uid={uid} (day unchanged)");

        // Cross-transport claimability, list side: a fresh LAN client now sees the slot.
        var lanLease = await LeaseClientForHelperAsync(ct);
        await using (lanLease)
        {
            var conn = new ConnectionHelper(
                lanLease.Client,
                new ConnectionOptions(),
                serverApi: ServerApi
            );
            var lanConnect = await conn.ConnectViaLanAsync(
                Lease!.ServerLanAddress,
                Lease.ServerLanPort,
                ct
            );
            Assert.True(lanConnect.Success, $"Fresh LAN connect failed: {lanConnect.Error}");
            Assert.Contains(lanConnect.Farmhands!.Farmhands, s => s.UniqueMultiplayerId == uid);
            Log($"Released slot uid={uid} visible to a fresh LAN list");
        }

        // First platform claim wins: the client rejoins the released slot; the approve recorder
        // re-records ownership and the marker is gone.
        var rejoin = await Farmers.ReconnectAsync(client.FarmerName, ct: ct);
        Assert.Equal(uid, rejoin.JoinResult.UniqueMultiplayerId);
        Assert.True(
            await WaitOwnershipStateAsync(
                uid,
                e => e is { HasOwner: true, Released: false },
                WaitName.Polling_FarmhandVisibility_Reowned,
                ct
            ),
            $"Rejoined farmhand uid={uid} should be re-owned with the released marker cleared"
        );
        Log($"Released slot uid={uid} re-claimed; first claim became the owner");
    }

    /// <summary>
    /// The production default posture (AllowIpConnections=false) on the shared steam server,
    /// via the runtime door toggle (vanilla gates per connection attempt, so the flag IS the
    /// door — see /test/set_ip_connections): with the LAN door closed, the platform ownership
    /// flows stay whole — create/customize → mapped; release → claimable; platform rejoin →
    /// re-owned. Pre-fix, a released slot on an IP-off server was claimable by nobody. The
    /// toggle is restored in finally (the shared steam config keeps IP on for mixed tests).
    /// </summary>
    [Fact]
    [TestServer(WithSteam = true)]
    public async Task IpOffPosture_ReleaseAndReclaimStayWhole()
    {
        var ct = TestCt;
        await GetClientAsync(ct);

        var toggled = await ServerApi.SetIpConnections(false, ct);
        Assert.True(
            toggled?.Success == true && !toggled.Enabled,
            $"IP door should be closed: {toggled?.Error}"
        );
        try
        {
            var client = await Farmers.ConnectNewAsync(ct: ct);
            var uid = client.JoinResult.UniqueMultiplayerId;
            Assert.True(
                await WaitOwnershipStateAsync(
                    uid,
                    e => e is { HasOwner: true },
                    WaitName.Polling_FarmhandVisibility_IpOffOwnership,
                    ct
                ),
                $"Farmhand uid={uid} should be ownership-mapped with the IP door closed"
            );
            await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, ct);
            Assert.True(
                await ServerApi.WaitForPlayerRemovedByIdAsync(uid, ct: ct),
                $"Player uid={uid} was not removed after disconnect"
            );

            var cmd = await ServerApi.RunConsoleCommand(
                "farmhand",
                new[] { "release", uid.ToString() },
                ct
            );
            Assert.True(cmd?.Success == true, $"Console command dispatch failed: {cmd?.Error}");
            Assert.True(
                await WaitOwnershipStateAsync(
                    uid,
                    e => e is { Released: true, HasOwner: false },
                    WaitName.Polling_FarmhandVisibility_Released,
                    ct
                ),
                $"Farmhand uid={uid} should be released with the IP door closed"
            );

            var rejoin = await Farmers.ReconnectAsync(client.FarmerName, ct: ct);
            Assert.Equal(uid, rejoin.JoinResult.UniqueMultiplayerId);
            Assert.True(
                await WaitOwnershipStateAsync(
                    uid,
                    e => e is { HasOwner: true, Released: false },
                    WaitName.Polling_FarmhandVisibility_Reowned,
                    ct
                ),
                $"Farmhand uid={uid} should be re-owned through the platform door under IP-off"
            );
            Log($"IP-off posture: release + platform reclaim whole for uid={uid}");
        }
        finally
        {
            // Load-bearing restore: the shared steam server must keep its LAN door for the
            // mixed-transport tests. CancellationToken.None so a canceled test still restores.
            var restored = await ServerApi.SetIpConnections(true, CancellationToken.None);
            Assert.True(
                restored?.Success == true && restored.Enabled,
                $"IP door restore failed: {restored?.Error}"
            );
        }
    }

    /// <summary>Polls /diagnostics/state until the farmhand entry satisfies the predicate
    /// (tests-assert-via-http-api).</summary>
    private async Task<bool> WaitOwnershipStateAsync(
        long uid,
        Func<DiagnosticsFarmhandState, bool> predicate,
        WaitName waitName,
        CancellationToken ct
    )
    {
        return await PollingHelper.WaitUntilAsync(
            waitName,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                var entry = state?.FarmhandData.FirstOrDefault(f => f.UniqueMultiplayerId == uid);
                return entry != null && predicate(entry);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
    }
}
