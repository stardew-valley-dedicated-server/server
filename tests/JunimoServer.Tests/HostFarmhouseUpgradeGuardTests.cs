using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Pins the host's main farmhouse invariants: it stays at HouseUpgradeLevel 0 (#346) and always
/// holds a usable bed. Debug house-upgrade commands (the only residual upgrade path) must be blocked
/// by <c>HostFarmhouseUpgradeGuard</c>; a farmhouse without a usable bed must be healed on load.
/// Drives the real vanilla debug commands through parseDebugInput so the Harmony prefix runs.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class HostFarmhouseUpgradeGuardTests : TestBase
{
    [Theory]
    [InlineData("houseupgrade 2")] // DebugCommands.HouseUpgrade (targets the host's home)
    [InlineData("upgradehouse")] // DebugCommands.UpgradeHouse (+1 to the host's level)
    [InlineData("thishouseupgrade 2")] // DebugCommands.ThisHouseUpgrade (the FarmHouse the host stands in)
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task DebugHouseUpgradeCommand_OnHost_IsBlocked(string command)
    {
        var ct = TestCt;

        var response = await ServerApi.RunDebugCommand(command, ct);
        Assert.NotNull(response);
        Assert.True(response.Success, $"command failed: {response.Error}");

        Assert.Equal(0, response.HostHouseUpgradeLevel);
        LogSuccess(
            $"'{command}' was blocked — host farmhouse stayed at level {response.HostHouseUpgradeLevel}"
        );
    }

    /// <summary>
    /// A host farmhouse without a bed <c>GetPlayerBed</c> finds (none at all, or a Double at level 0)
    /// is healed on load, and the host can then sleep in place. In place matters: a host that warps
    /// home first rides the warp's fade into the day transition, which masks a missing bed.
    /// </summary>
    [Theory]
    [InlineData(null)] // no bed at all
    [InlineData("2052")] // BedFurniture.DOUBLE_BED_INDEX: a Double bed, unusable at level 0
    [TestServer(Clients = 1, Exclusive = true)]
    public async Task UnusableHostBed_HealedOnLoad_HostSleepsInPlace(string? wrongBedId)
    {
        var ct = TestCt;

        // Build the broken save: clear all furniture (the bed is furniture), optionally place the
        // wrong bed, persist, reload.
        await WarpHostHomeAsync(ct);

        var clear = await ServerApi.RunDebugCommand("clearfurniture", ct);
        Assert.True(clear?.Success == true, $"clearfurniture failed: {clear?.Error}");
        var bedRemoved = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_HostFarmhouse_BedRemoved,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                return state is { FarmHouseHasPlayerBed: false, FarmHouseFurnitureCount: 0 };
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(bedRemoved, "clearfurniture must leave the host farmhouse with no bed");

        if (wrongBedId != null)
        {
            var placed = await ServerApi.SetHostFarmhouseBed(wrongBedId, ct);
            Assert.True(
                placed?.Success == true,
                $"placing bed {wrongBedId} failed: {placed?.Error}"
            );
            var bedReplaced = await PollingHelper.WaitUntilAsync(
                WaitName.Polling_HostFarmhouse_BedReplaced,
                async () =>
                {
                    var state = await ServerApi.GetDiagnosticsState(ct);
                    return state is { FarmHouseHasPlayerBed: false, FarmHouseFurnitureCount: 1 };
                },
                TestTimings.CabinAssignmentTimeout,
                cancellationToken: ct
            );
            Assert.True(
                bedReplaced,
                $"bed {wrongBedId} must be present yet not found by GetPlayerBed at level 0"
            );
        }

        var saved = await ServerApi.ForceSave(ct);
        Assert.True(saved?.Success == true, $"ForceSave failed: {saved?.Error}");
        await ReloadServerAsync();

        var healed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_HostFarmhouse_BedHealed,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                return state is { FarmHouseHasPlayerBed: true, FarmHouseFurnitureCount: 1 };
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            healed,
            "After reload the level-0 host farmhouse must hold exactly one bed that GetPlayerBed finds"
        );
        Log($"Host farmhouse healed on load (wrongBedId={wrongBedId ?? "none"})");

        // A farmhand sleeps while the host already stands in its farmhouse, so the host sleeps in
        // place with no warp fade to hide a missing bed.
        var farmer = await Farmers.ConnectFastAsync(namePrefix: "BedSleeper", ct: ct);
        Assert.True(
            await ServerApi.WaitForPlayerByIdAsync(farmer.JoinResult.UniqueMultiplayerId, ct: ct),
            "Sleeper must be connected before the host is parked home"
        );
        await WarpHostHomeAsync(ct);

        await SleepToSaveAsync(ct);
        LogSuccess("Host slept in place on the healed bed and the day advanced");
    }

    /// <summary>
    /// Warps the host into its farmhouse via the vanilla debug warp and waits for arrival.
    /// </summary>
    private async Task WarpHostHomeAsync(CancellationToken ct)
    {
        var warp = await ServerApi.RunDebugCommand("warp FarmHouse 5 9", ct);
        Assert.True(warp?.Success == true, $"warp failed: {warp?.Error}");
        var home = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_HostFarmhouse_HostWarpedHome,
            async () =>
            {
                var state = await ServerApi.GetDiagnosticsState(ct);
                return state?.HostLocation == "FarmHouse";
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(home, "Host must arrive in FarmHouse after the debug warp");
    }
}
