using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for the server settings system (server-settings.json).
///
/// Verifies that the settings file is auto-created with correct defaults,
/// and that the values are applied to the running server. These tests run
/// against the default server configuration (no custom settings injected).
///
/// API-only. Never calls GetClientAsync().
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Clients = 0, Artifacts = false)]
public class ServerSettingsTests : TestBase
{
    public ServerSettingsTests() { }

    #region GET /settings: default values

    /// <summary>
    /// Verifies the /settings endpoint returns a valid response with the expected structure.
    /// </summary>
    [Fact]
    public async Task Settings_ReturnsValidResponse()
    {
        var settings = await ServerApi.GetSettings(TestContext.Current.CancellationToken);

        Assert.NotNull(settings);
        Assert.NotNull(settings.Game);
        Assert.NotNull(settings.Server);
        Log(
            $"Settings loaded: FarmName={settings.Game.FarmName}, Strategy={settings.Server.CabinStrategy}"
        );
    }

    /// <summary>
    /// Test data for default settings verification: (settingPath, expectedValue), matched
    /// against <see cref="GetSettingValue"/>. Expected values must be xUnit-serializable for
    /// theory discovery, so a non-serializable value type (FarmTypeSetting) is given as its
    /// scalar string and compared via ToString().
    /// </summary>
    public static IEnumerable<object[]> DefaultSettingsData =>
        new List<object[]>
        {
            // Game settings
            new object[] { "Game.FarmName", "Junimo" },
            new object[] { "Game.FarmType", "0" },
            new object[] { "Game.ProfitMargin", 1.0f },
            new object[]
            {
                "Game.StartingCabins",
                Math.Max(4, HostPool.Instance.Hosts.Max(h => h.ClientCapacity.Capacity) * 3),
            },
            new object[] { "Game.SpawnMonstersAtNight", "auto" },
            // Server settings
            new object[]
            {
                "Server.MaxPlayers",
                Math.Max(
                    10,
                    Math.Max(4, HostPool.Instance.Hosts.Max(h => h.ClientCapacity.Capacity) * 3) + 1
                ),
            },
            new object[] { "Server.CabinStrategy", "CabinStack" },
            new object[] { "Server.SeparateWallets", false },
            new object[] { "Server.ExistingCabinBehavior", "KeepExisting" },
        };

    /// <summary>
    /// Verifies each default setting has the expected value.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefaultSettingsData))]
    public async Task DefaultSettings_HaveExpectedValues(string settingPath, object expectedValue)
    {
        var settings = await ServerApi.GetSettings(TestContext.Current.CancellationToken);
        Assert.NotNull(settings);

        var actualValue = GetSettingValue(settings, settingPath);
        Log($"{settingPath}: expected={expectedValue}, actual={actualValue}");

        Assert.Equal(expectedValue, actualValue);
    }

    private static object GetSettingValue(SettingsResponse settings, string path)
    {
        return path switch
        {
            "Game.FarmName" => settings.Game.FarmName,
            "Game.FarmType" => settings.Game.FarmType.ToString(),
            "Game.ProfitMargin" => settings.Game.ProfitMargin,
            "Game.StartingCabins" => settings.Game.StartingCabins,
            "Game.SpawnMonstersAtNight" => settings.Game.SpawnMonstersAtNight,
            "Server.MaxPlayers" => settings.Server.MaxPlayers,
            "Server.CabinStrategy" => settings.Server.CabinStrategy,
            "Server.SeparateWallets" => settings.Server.SeparateWallets,
            "Server.ExistingCabinBehavior" => settings.Server.ExistingCabinBehavior,
            _ => throw new ArgumentException($"Unknown setting path: {path}"),
        };
    }

    #endregion

    #region Settings consistency with /status

    /// <summary>
    /// Verifies that /status farmName matches /settings FarmName.
    /// The settings are the source of truth for game creation config;
    /// /status reflects the running game state. They should agree.
    /// </summary>
    [Fact]
    public async Task Settings_FarmNameMatchesStatus()
    {
        var settings = await ServerApi.GetSettings(TestContext.Current.CancellationToken);
        var status = await ServerApi.GetStatus(TestContext.Current.CancellationToken);

        Assert.NotNull(settings);
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");
        Assert.Equal(settings.Game.FarmName, status.FarmName);
        Log($"Both report FarmName: {status.FarmName}");
    }

    /// <summary>
    /// Verifies that /status maxPlayers matches /settings MaxPlayers.
    /// </summary>
    [Fact]
    public async Task Settings_MaxPlayersMatchesStatus()
    {
        var settings = await ServerApi.GetSettings(TestContext.Current.CancellationToken);
        var status = await ServerApi.GetStatus(TestContext.Current.CancellationToken);

        Assert.NotNull(settings);
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");
        Assert.Equal(settings.Server.MaxPlayers, status.MaxPlayers);
        Log($"Both report MaxPlayers: {status.MaxPlayers}");
    }

    #endregion
}
