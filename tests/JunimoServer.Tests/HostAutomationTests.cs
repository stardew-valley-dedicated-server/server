using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for host automation behavior.
/// Verifies that the server correctly pauses/unpauses time based on player presence,
/// and that the host bot automates sleeping and day transitions.
/// </summary>
[Collection("Integration")]
public class HostAutomationTests : IDisposable, IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly GameTestClient _gameClient;
    private readonly ServerApiClient _serverApi;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _createdFarmers = new();

    public HostAutomationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _gameClient = new GameTestClient();
        _serverApi = new ServerApiClient(_fixture.ServerBaseUrl);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Ensure disconnected so farmers can be deleted
        try
        {
            await _gameClient.Navigate("title");
            await _gameClient.Wait.ForDisconnected(10000);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"  Error disconnecting during cleanup: {ex.Message}");
        }

        await Task.Delay(3000);

        foreach (var farmerName in _createdFarmers)
        {
            try
            {
                _output.WriteLine($"Cleaning up farmer: {farmerName}");
                var result = await _serverApi.DeleteFarmhand(farmerName);
                if (result?.Success == true)
                    _output.WriteLine($"  Deleted successfully");
                else if (result?.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                    _output.WriteLine($"  Farmer not found (ok)");
                else
                    _output.WriteLine($"  Delete failed: {result?.Error ?? "unknown error"}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  Cleanup error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Verifies that game time does NOT advance when no other player is connected.
    /// The server's AlwaysOn service pauses the game when otherFarmers.Count == 0
    /// and timeOfDay is between 610 and 2500.
    /// </summary>
    [Fact]
    public async Task TimePaused_WhenNoPlayersConnected()
    {
        await EnsureDisconnected();

        // Set time to a known mid-day value so we're firmly inside the pause window (610-2500)
        var setTimeResult = await _serverApi.SetTime(1200);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        _output.WriteLine($"Set time to {setTimeResult.TimeOfDay}");

        // Small delay to let the game loop process the time change
        await Task.Delay(1000);

        // Read current time
        var status1 = await _serverApi.GetStatus();
        Assert.NotNull(status1);
        var time1 = status1.TimeOfDay;
        _output.WriteLine($"Time reading 1: {time1}");

        // Wait long enough for multiple time advances to occur if the game were unpaused.
        // Stardew advances time every 7 seconds (10 game-minutes per tick).
        // 15 seconds = ~2 advances = +20 game-minutes if unpaused.
        await Task.Delay(15000);

        // Read time again
        var status2 = await _serverApi.GetStatus();
        Assert.NotNull(status2);
        var time2 = status2.TimeOfDay;
        _output.WriteLine($"Time reading 2: {time2}");

        // Time should NOT have advanced (game is paused with no other players)
        Assert.Equal(time1, time2);
        _output.WriteLine("Confirmed: time did not advance while no players connected");
    }

    /// <summary>
    /// Verifies that game time DOES advance when another player is connected.
    /// The server's AlwaysOn service unpauses the game when otherFarmers.Count >= 1.
    /// </summary>
    [Fact]
    public async Task TimeAdvances_WhenPlayerConnected()
    {
        await EnsureDisconnected();

        var farmerName = $"Time{DateTime.UtcNow.Ticks % 1000}";
        _createdFarmers.Add(farmerName);
        await JoinAndEnterWorld(farmerName);

        // Set time to a known value so the measurement is clean
        var setTimeResult = await _serverApi.SetTime(1200);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");

        // Let the game process the time change and advance at least one tick
        await Task.Delay(2000);

        // Read current time
        var status1 = await _serverApi.GetStatus();
        Assert.NotNull(status1);
        var time1 = status1.TimeOfDay;
        _output.WriteLine($"Time reading 1: {time1}");

        // Wait for at least 2 time advances (~14 seconds)
        await Task.Delay(16000);

        // Read time again
        var status2 = await _serverApi.GetStatus();
        Assert.NotNull(status2);
        var time2 = status2.TimeOfDay;
        _output.WriteLine($"Time reading 2: {time2}");

        // Time should have advanced
        Assert.True(time2 > time1,
            $"Time should have advanced with a player connected, but went from {time1} to {time2}");

        _output.WriteLine($"Confirmed: time advanced from {time1} to {time2} (+{time2 - time1} game-minutes)");

        await DisconnectFromServer();
    }

    /// <summary>
    /// Verifies that when a connected player goes to sleep, the host bot
    /// automatically sleeps too, triggering a day transition.
    ///
    /// AlwaysOn.HandleAutoSleep() detects (numberRequired - numReady == 1)
    /// and calls startSleep() on the host.
    /// </summary>
    [Fact]
    public async Task HostAutoSleeps_WhenPlayerSleeps()
    {
        await EnsureDisconnected();

        var farmerName = $"Sleep{DateTime.UtcNow.Ticks % 1000}";
        _createdFarmers.Add(farmerName);
        await JoinAndEnterWorld(farmerName);

        // Record the current day
        var statusBefore = await _serverApi.GetStatus();
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        _output.WriteLine($"Before sleep: {seasonBefore} {dayBefore}, Year {yearBefore}, Time {statusBefore.TimeOfDay}");

        // Trigger the farmhand to go to sleep
        var sleepResult = await _gameClient.Actions.Sleep();
        Assert.NotNull(sleepResult);
        Assert.True(sleepResult.Success, $"Sleep action failed: {sleepResult.Error}");
        _output.WriteLine($"Farmhand sleeping at {sleepResult.Location}");

        // Wait for the day to transition.
        // The host bot should detect the farmhand is ready to sleep,
        // then auto-sleep itself, triggering NewDay().
        var dayChanged = await WaitForDayChange(dayBefore, seasonBefore, yearBefore, TimeSpan.FromSeconds(120));
        Assert.True(dayChanged, "Day should have advanced after farmhand slept and host auto-slept");

        var statusAfter = await _serverApi.GetStatus();
        Assert.NotNull(statusAfter);
        _output.WriteLine($"After sleep: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}");

        // Verify it's actually a new day (not the same day)
        Assert.True(
            statusAfter.Day != dayBefore || statusAfter.Season != seasonBefore || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}");

        await DisconnectFromServer();
    }

    /// <summary>
    /// Verifies that when time reaches 2:00 AM (2600 game-time) with a player connected,
    /// the game triggers a pass-out and the host bot ensures the day transitions.
    ///
    /// At 2600, AlwaysOn unpauses the game, the game forces performPassoutWarp(),
    /// and both players transition to the next day.
    /// </summary>
    [Fact]
    public async Task HostPassesOut_WhenTimeReaches2AM()
    {
        await EnsureDisconnected();

        var farmerName = $"Pass{DateTime.UtcNow.Ticks % 1000}";
        _createdFarmers.Add(farmerName);
        await JoinAndEnterWorld(farmerName);

        // Record the current day
        var statusBefore = await _serverApi.GetStatus();
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        _output.WriteLine($"Before pass-out: {seasonBefore} {dayBefore}, Year {yearBefore}, Time {statusBefore.TimeOfDay}");

        // Set time to 2550 — just one 10-minute tick before 2:00 AM (2600).
        // The game advances time every ~7 seconds, so 2550 → 2600 in ~7s.
        var setTimeResult = await _serverApi.SetTime(2550);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        _output.WriteLine($"Set time to {setTimeResult.TimeOfDay}, waiting for 2:00 AM pass-out...");

        // Wait for the day to transition.
        // At 2600, the game forces pass-out → sleep ready check → day transition.
        var dayChanged = await WaitForDayChange(dayBefore, seasonBefore, yearBefore, TimeSpan.FromSeconds(120));
        Assert.True(dayChanged, "Day should have advanced after 2:00 AM pass-out");

        var statusAfter = await _serverApi.GetStatus();
        Assert.NotNull(statusAfter);
        _output.WriteLine($"After pass-out: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}");

        Assert.True(
            statusAfter.Day != dayBefore || statusAfter.Season != seasonBefore || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}");

        await DisconnectFromServer();
    }

    #region Helpers

    /// <summary>
    /// Polls GET /status until the day/season/year changes from the given values.
    /// Returns true if day changed within the timeout, false otherwise.
    /// </summary>
    private async Task<bool> WaitForDayChange(int day, string season, int year, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            await Task.Delay(2000);

            try
            {
                var status = await _serverApi.GetStatus();
                if (status == null) continue;

                if (status.Day != day || status.Season != season || status.Year != year)
                {
                    _output.WriteLine($"Day changed after {attempt * 2}s: {season} {day} Y{year} → {status.Season} {status.Day} Y{status.Year}");
                    return true;
                }

                if (attempt % 5 == 0)
                {
                    _output.WriteLine($"Still waiting for day change... (time={status.TimeOfDay}, attempt {attempt})");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Status poll error: {ex.Message}");
            }
        }

        _output.WriteLine($"Timed out waiting for day change after {timeout.TotalSeconds}s");
        return false;
    }

    private async Task EnsureDisconnected()
    {
        await _gameClient.Navigate("title");
        var disconnectWait = await _gameClient.Wait.ForDisconnected(10000);
        Assert.True(disconnectWait?.Success, "Should be disconnected before starting test");
    }

    private async Task DisconnectFromServer()
    {
        var exitResult = await _gameClient.Exit();
        Assert.True(exitResult?.Success, $"Exit failed: {exitResult?.Error}");

        await _gameClient.Wait.ForTitle(30000);
        await _gameClient.Wait.ForDisconnected(10000);
        _output.WriteLine("Disconnected from server");
    }

    /// <summary>
    /// Joins the server with a new farmer and enters the game world.
    /// </summary>
    private async Task JoinAndEnterWorld(string farmerName)
    {
        var status = await _serverApi.GetStatus();
        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");
        Assert.False(string.IsNullOrEmpty(status.InviteCode), "Server should have an invite code");

        var navigateResult = await _gameClient.Navigate("coopmenu");
        Assert.True(navigateResult?.Success, $"Navigate failed: {navigateResult?.Error}");

        var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", 10000);
        Assert.True(menuWait?.Success, $"Wait for CoopMenu failed: {menuWait?.Error}");

        var tabResult = await _gameClient.Coop.Tab(0);
        Assert.True(tabResult?.Success, $"Tab switch failed: {tabResult?.Error}");

        var openResult = await _gameClient.Coop.OpenInviteCodeMenu();
        Assert.True(openResult?.Success, $"Open invite code menu failed: {openResult?.Error}");

        var textInputWait = await _gameClient.Wait.ForTextInput(10000);
        Assert.True(textInputWait?.Success, $"Wait for text input failed: {textInputWait?.Error}");

        var submitResult = await _gameClient.Coop.SubmitInviteCode(status.InviteCode);
        Assert.True(submitResult?.Success, $"Submit invite code failed: {submitResult?.Error}");

        var farmhandWait = await _gameClient.Wait.ForFarmhands(60000);
        Assert.True(farmhandWait?.Success, $"Wait for farmhands failed: {farmhandWait?.Error}");

        await Task.Delay(2000);

        var farmhands = await _gameClient.Farmhands.GetSlots();
        Assert.NotNull(farmhands);
        Assert.True(farmhands.Success, $"Get farmhands failed: {farmhands.Error}");
        Assert.NotEmpty(farmhands.Slots);

        var newSlot = farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
        Assert.NotNull(newSlot);

        var selectResult = await _gameClient.Farmhands.Select(newSlot.Index);
        Assert.True(selectResult?.Success, $"Select farmhand failed: {selectResult?.Error}");

        var charWait = await _gameClient.Wait.ForCharacter(30000);
        Assert.True(charWait?.Success, $"Wait for character menu failed: {charWait?.Error}");

        var customizeResult = await _gameClient.Character.Customize(farmerName, "Testing");
        Assert.True(customizeResult?.Success, $"Customize failed: {customizeResult?.Error}");

        await Task.Delay(200);

        var confirmResult = await _gameClient.Character.Confirm();
        Assert.True(confirmResult?.Success, $"Confirm failed: {confirmResult?.Error}");

        var worldWait = await _gameClient.Wait.ForWorldReady(60000);
        Assert.True(worldWait?.Success, $"Wait for world ready failed: {worldWait?.Error}");

        // Wait for network sync
        await Task.Delay(2000);
        _output.WriteLine($"Entered world as '{farmerName}'");
    }

    #endregion

    public void Dispose()
    {
        _gameClient.Dispose();
        _serverApi.Dispose();
    }
}
