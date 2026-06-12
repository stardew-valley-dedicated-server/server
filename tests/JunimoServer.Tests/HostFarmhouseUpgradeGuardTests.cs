using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Pins the invariant that the dedicated host's main farmhouse cannot be upgraded (#346): it is
/// internal-only and must stay at HouseUpgradeLevel 0. A debug house-upgrade command run on the host
/// (the only residual upgrade path, via the admin console) must be blocked by
/// <c>HostFarmhouseUpgradeGuard</c>. Drives the real vanilla command through parseDebugInput so the
/// guard's Harmony prefix is genuinely exercised.
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
        var ct = TestContext.Current.CancellationToken;

        var response = await ServerApi.RunDebugHouseUpgrade(command, ct);
        Assert.NotNull(response);
        Assert.True(response.Success, $"command failed: {response.Error}");

        Assert.Equal(0, response.HostHouseUpgradeLevel);
        LogSuccess(
            $"'{command}' was blocked — host farmhouse stayed at level {response.HostHouseUpgradeLevel}"
        );
    }
}
