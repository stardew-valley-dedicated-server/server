using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Proves the server resolves a mod-added farm by its <c>Data/AdditionalFarms</c> <c>Id</c>
/// rather than by position. The base game ships exactly one AdditionalFarms entry
/// (MeadowlandsFarm), so resolving it can't distinguish "by Id" from "by index 0". The
/// TestFarmMod fixture (loaded via <see cref="TestServerAttribute.FixtureFarmMod"/>) adds a
/// second entry, <c>JunimoTest.SecondFarm</c>; selecting it by Id and getting that exact key
/// back proves resolution is keyed on Id — the regression PR #379's positional approach hit.
///
/// Its own class (not folded into FarmMapTypeTests): the fixture mod loads at container start
/// and changes Data/AdditionalFarms, so it must run on a separate, fixture-loaded server. The
/// FixtureFarmMod flag is part of the server reuse key, so this costs at most one extra pooled
/// server for this class.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedClass, Priority = 91, Exclusive = true, FixtureFarmMod = true)]
public class ModFarmDisambiguationTests : TestBase
{
    /// <summary>Must match the Id TestFarmMod adds to Data/AdditionalFarms (separate net6.0 assembly, not referenced here).</summary>
    private const string FixtureFarmId = "JunimoTest.SecondFarm";

    [Fact]
    public async Task NewGame_WithModdedFarmId_ResolvesSecondAdditionalFarmsEntryById()
    {
        LogSection($"Testing modded farm selection by Id: {FixtureFarmId}");

        await CreateNewGameOnServerAsync(FixtureFarmId);

        Log($"Server ready: {Server.BaseUrl}");

        // The fixture's entry is the SECOND AdditionalFarms entry (after base MeadowlandsFarm);
        // getting its Id back proves resolution matched on Id, not array position.
        var status = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.Equal(FixtureFarmId, status.FarmTypeKey);
        Log($"Farm type key verified: {status.FarmTypeKey}");

        // Verify the modded farm map built cabins (server-side proof the farm loaded).
        var cabins = await ServerApi.GetCabins(TestContext.Current.CancellationToken);
        Assert.NotNull(cabins);
        Assert.True(cabins.TotalCount >= 1, $"Expected at least 1 cabin, got {cabins.TotalCount}");

        // NOTE: deliberately no client join here. A joining client re-resolves whichModFarm.Id
        // against ITS OWN Data/AdditionalFarms and throws "<Id> is not a valid farm type" if
        // absent (NetWorldState.SetFarmType, decompiled :899-902). The fixture mod is only on
        // the server, so the test client lacks the entry. The by-Id resolution under test is
        // fully proven server-side via FarmTypeKey; the join-a-modded-farm path is already
        // covered by FarmMapTypeTests' MeadowlandsFarm case (base content every client has).
    }

    [Fact]
    public async Task NewGame_WithModdedKeyword_ResolvesToTheInstalledModFarm()
    {
        LogSection("Testing the \"modded\" keyword resolves to the installed mod farm");

        await CreateNewGameOnServerAsync("modded");

        // "modded" picks the first non-Meadowlands AdditionalFarms entry; the fixture mod adds
        // exactly one (JunimoTest.SecondFarm), so the keyword must land on it.
        var status = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.Equal(FixtureFarmId, status.FarmTypeKey);
        Log($"\"modded\" resolved to: {status.FarmTypeKey}");
    }
}
