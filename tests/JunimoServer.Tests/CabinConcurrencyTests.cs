using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Regression tests for the client-originated phantom farmhand race.
///
/// Background: vanilla Building.performActionOnConstruction (Building.cs:1220-1222)
/// calls Cabin.CreateFarmhand on every peer that receives the
/// buildingConstructedEvent — no Game1.IsMasterGame guard. When a dedicated server
/// builds a cabin at daysOfConstructionLeft=0 while a client is connected, the
/// client sees HasOwner==false before the cabin.farmhandReference.uid delta
/// arrives, runs CreateFarmhand locally with Utility.RandomLong(), and replicates
/// a phantom farmhandData entry back to the server. The phantom shares
/// homeLocation with the legitimate entry; a later DestroyCabin cleanup pre-fix
/// matched and deleted both, breaking GrantAdminById for the real player.
///
/// Fix (NetworkTweaker.cs):
///  - SendBuildingConstructedEvent_Prefix replaces network propagation of
///    buildingConstructedEvent with a direct local OnBuildingConstructed call, so
///    peers never run Cabin.CreateFarmhand (Utility.RandomLong) and never replicate
///    phantom farmhandData entries back to the master. This is the structural fix.
///
/// The invariant asserted below: after any sequence of client joins/disconnects,
/// every farmhandData entry on the server must correspond to a cabin whose
/// farmhandReference.uid equals that entry's uid. Orphans are phantoms.
///
/// StartingCabins = 1 is deliberate: forces EnsureAtLeastXCabins to build a new
/// cabin on each additional join, exercising the race path.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, StartingCabins = 1)]
public class CabinConcurrencyTests : TestBase
{
    public CabinConcurrencyTests() { }

    /// <summary>
    /// Joins a farmer and verifies no phantom farmhandData entries accumulate.
    /// An entry is a phantom if no cabin references it via farmhandReference.uid.
    /// </summary>
    [Fact]
    public async Task Join_DoesNotAccumulatePhantomFarmhandEntries()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = await Farmers.ConnectNewAsync(ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            client.FarmerName,
            requireCustomized: true,
            ct: ct
        );

        var state = await ServerApi.GetDiagnosticsState(ct);
        Assert.NotNull(state);

        var cabinUids = state
            .Cabins.Select(c => c.FarmhandReferenceUid)
            .Where(uid => uid != 0)
            .ToHashSet();

        var orphans = state
            .FarmhandData.Where(f => !cabinUids.Contains(f.UniqueMultiplayerId))
            .ToList();

        if (orphans.Count > 0)
        {
            Log($"Phantoms detected (not referenced by any cabin):");
            foreach (var o in orphans)
            {
                Log(
                    $"  uid={o.UniqueMultiplayerId} name='{o.Name}' "
                        + $"home='{o.HomeLocation}' isCustomized={o.IsCustomized}"
                );
            }
            Log($"Live cabins:");
            foreach (var c in state.Cabins)
            {
                Log(
                    $"  '{c.IndoorsName}' ownerId={c.OwnerId} "
                        + $"farmhandReferenceUid={c.FarmhandReferenceUid}"
                );
            }
        }

        Assert.True(
            orphans.Count == 0,
            $"Found {orphans.Count} phantom farmhandData entries not referenced by "
                + $"any cabin. This indicates a client-originated CreateFarmhand was not "
                + $"rejected by the master on server-side. UIDs: "
                + $"{string.Join(", ", orphans.Select(o => o.UniqueMultiplayerId))}"
        );
    }

    /// <summary>
    /// Direct regression for the original failing test symptom:
    /// farmer A joins, farmer B joins and disconnects, B's farmhand is deleted,
    /// and A must still be grantable as admin afterwards. Pre-fix, DestroyCabin's
    /// broad cleanup removed A's entry when it matched B's destroyed cabin by
    /// shared homeLocation (both uids pointed at the same cabin due to the phantom
    /// hijacking cabin ownership on the server).
    /// </summary>
    [Fact]
    public async Task SecondFarmerDisconnectAndDelete_DoesNotBreakGrantAdminForFirst()
    {
        var ct = TestContext.Current.CancellationToken;

        // First farmer joins and customizes.
        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        var uidA = clientA.JoinResult.UniqueMultiplayerId;
        Log($"Farmer A joined: name='{clientA.FarmerName}' uid={uidA}");

        // Second farmer joins on the same test client (reusing it after A disconnects).
        // This triggers EnsureAtLeastXCabins to build a new cabin while A-era state
        // is still in farmhandData, exercising the cross-cabin cleanup path.
        var clientB = await Farmers.ConnectNewAsync(breakSession: true, ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            clientB.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        var uidB = clientB.JoinResult.UniqueMultiplayerId;
        Log($"Farmer B joined: name='{clientB.FarmerName}' uid={uidB}");

        // Disconnect B and wait for persistence so the server has a stable
        // customized farmhand snapshot before we delete it.
        await Farmers.DisconnectAndWaitForPersistenceAsync(clientB.FarmerName, ct);

        // Delete B's farmhand. Pre-fix, this DestroyCabin call could also
        // collaterally remove A's farmhandData entry if a phantom had hijacked
        // ownership of A's cabin.
        Log($"Deleting farmer B (uid={uidB})...");
        var deleteResp = await ServerApi.DeleteFarmhandById(uidB, ct);
        Assert.True(
            deleteResp?.Success,
            $"DELETE /farmhands for B should succeed: {deleteResp?.Error ?? "(null response)"}"
        );
        Farmers.CreatedFarmers.RemoveAll(f => f.Uid == uidB);

        // Verify A's farmhandData entry survived.
        var state = await ServerApi.GetDiagnosticsState(ct);
        Assert.NotNull(state);
        var aStillPresent = state.FarmhandData.Any(f => f.UniqueMultiplayerId == uidA);
        Assert.True(
            aStillPresent,
            $"Farmer A (uid={uidA}) must still be in farmhandData after B's cabin "
                + $"was destroyed; pre-fix the DestroyCabin cleanup would collaterally "
                + $"remove A's entry. Current farmhandData: "
                + $"{string.Join(", ", state.FarmhandData.Select(f => f.UniqueMultiplayerId))}"
        );

        // Reconnect A (needed for GrantAdminById — getAllFarmers iterates
        // farmhandData and A's entry is only live while A is connected).
        var clientAReconnect = await Farmers.ReconnectAsync(clientA.FarmerName, ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );

        // The exact symptom from the original flake: GrantAdminById must succeed.
        var grantResp = await ServerApi.GrantAdminById(uidA, ct);
        Assert.True(
            grantResp?.Success,
            $"GrantAdminById(uid={uidA}) must succeed after B's cabin destroy; "
                + $"got: {grantResp?.Error ?? "(null response)"}"
        );
    }
}
