using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for host automation behavior.
/// Verifies that the server correctly pauses/unpauses time based on player presence,
/// and that the host bot automates sleeping and day transitions.
/// </summary>
[Collection("Integration")]
public class HostAutomationTests : IntegrationTestBase
{
    public HostAutomationTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>
    /// Verifies that game time does NOT advance when no other player is connected.
    /// The server's AlwaysOn service pauses the game when otherFarmers.Count == 0
    /// and timeOfDay is between 610 and 2500.
    /// </summary>
    [Fact]
    public async Task TimePaused_WhenNoPlayersConnected()
    {
        await EnsureDisconnectedAsync();

        // Set time to a known mid-day value so we're firmly inside the pause window (610-2500)
        var setTimeResult = await ServerApi.SetTime(1200);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        Log($"Set time to {setTimeResult.TimeOfDay}");

        // Small delay to let the game loop process the time change
        await Task.Delay(TestTimings.TimeChangeProcessingDelay);

        // Read current time
        var status1 = await ServerApi.GetStatus();
        Assert.NotNull(status1);
        var time1 = status1.TimeOfDay;
        Log($"Time reading 1: {time1}");

        // Wait long enough for multiple time advances to occur if the game were unpaused.
        // Stardew advances time every 7 seconds (10 game-minutes per tick).
        // 15 seconds = ~2 advances = +20 game-minutes if unpaused.
        await Task.Delay(TestTimings.TimePausedVerification);

        // Read time again
        var status2 = await ServerApi.GetStatus();
        Assert.NotNull(status2);
        var time2 = status2.TimeOfDay;
        Log($"Time reading 2: {time2}");

        // Time should NOT have advanced (game is paused with no other players)
        Assert.Equal(time1, time2);
        LogSuccess("Confirmed: time did not advance while no players connected");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that game time DOES advance when another player is connected.
    /// The server's AlwaysOn service unpauses the game when otherFarmers.Count >= 1.
    /// </summary>
    [Fact]
    public async Task TimeAdvances_WhenPlayerConnected()
    {
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Time");
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Set time to a known value so the measurement is clean
        var setTimeResult = await ServerApi.SetTime(1200);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");

        // Let the game process the time change and advance at least one tick
        await Task.Delay(TestTimings.NetworkSyncDelay);

        // Read current time
        var status1 = await ServerApi.GetStatus();
        Assert.NotNull(status1);
        var time1 = status1.TimeOfDay;
        Log($"Time reading 1: {time1}");

        // Wait for at least 2 time advances (~14 seconds)
        await Task.Delay(TestTimings.TimeAdvanceWait);

        // Read time again
        var status2 = await ServerApi.GetStatus();
        Assert.NotNull(status2);
        var time2 = status2.TimeOfDay;
        Log($"Time reading 2: {time2}");

        // Time should have advanced
        Assert.True(time2 > time1,
            $"Time should have advanced with a player connected, but went from {time1} to {time2}");

        LogSuccess($"Confirmed: time advanced from {time1} to {time2} (+{time2 - time1} game-minutes)");

        await AssertNoExceptionsAsync("at end of test");
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
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Sleep");
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Record the current day
        var statusBefore = await ServerApi.GetStatus();
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        Log($"Before sleep: {seasonBefore} {dayBefore}, Year {yearBefore}, Time {statusBefore.TimeOfDay}");

        // Trigger the farmhand to go to sleep
        var sleepResult = await GameClient.Actions.Sleep();
        Assert.NotNull(sleepResult);
        Assert.True(sleepResult.Success, $"Sleep action failed: {sleepResult.Error}");
        Log($"Farmhand sleeping at {sleepResult.Location}");

        // Wait for the day to transition.
        // The host bot should detect the farmhand is ready to sleep,
        // then auto-sleep itself, triggering NewDay().
        var dayChanged = await WaitForDayChangeAsync(dayBefore, seasonBefore, yearBefore);
        Assert.True(dayChanged, "Day should have advanced after farmhand slept and host auto-slept");

        var statusAfter = await ServerApi.GetStatus();
        Assert.NotNull(statusAfter);
        Log($"After sleep: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}");

        // Verify it's actually a new day (not the same day)
        Assert.True(
            statusAfter.Day != dayBefore || statusAfter.Season != seasonBefore || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}");

        await AssertNoExceptionsAsync("at end of test");
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
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Pass");
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Record the current day
        var statusBefore = await ServerApi.GetStatus();
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        Log($"Before pass-out: {seasonBefore} {dayBefore}, Year {yearBefore}, Time {statusBefore.TimeOfDay}");

        // Set time to 2550 — just one 10-minute tick before 2:00 AM (2600).
        // The game advances time every ~7 seconds, so 2550 → 2600 in ~7s.
        var setTimeResult = await ServerApi.SetTime(2550);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        Log($"Set time to {setTimeResult.TimeOfDay}, waiting for 2:00 AM pass-out...");

        // Wait for the day to transition.
        // At 2600, the game forces pass-out → sleep ready check → day transition.
        var dayChanged = await WaitForDayChangeAsync(dayBefore, seasonBefore, yearBefore);
        Assert.True(dayChanged, "Day should have advanced after 2:00 AM pass-out");

        var statusAfter = await ServerApi.GetStatus();
        Assert.NotNull(statusAfter);
        Log($"After pass-out: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}");

        Assert.True(
            statusAfter.Day != dayBefore || statusAfter.Season != seasonBefore || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}");

        await AssertNoExceptionsAsync("at end of test");
    }

    #region Helpers

    /// <summary>
    /// Polls GET /status until the day/season/year changes from the given values.
    /// </summary>
    private async Task<bool> WaitForDayChangeAsync(int day, string season, int year)
    {
        var deadline = DateTime.UtcNow + TestTimings.DayChangeTimeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            await Task.Delay(TestTimings.DayChangePollInterval);

            try
            {
                var status = await ServerApi.GetStatus();
                if (status == null) continue;

                if (status.Day != day || status.Season != season || status.Year != year)
                {
                    Log($"Day changed after {attempt * 2}s: {season} {day} Y{year} → {status.Season} {status.Day} Y{status.Year}");
                    return true;
                }

                if (attempt % 5 == 0)
                {
                    LogDetail($"Still waiting for day change... (time={status.TimeOfDay}, attempt {attempt})");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Status poll error: {ex.Message}");
            }
        }

        LogWarning($"Timed out waiting for day change after {TestTimings.DayChangeTimeout.TotalSeconds}s");
        return false;
    }

    #endregion
}
