using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;

using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for host automation behavior.
/// Verifies that the server correctly pauses/unpauses time based on player presence,
/// and that the host bot automates sleeping and day transitions.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class HostAutomationTests : TestBase
{
    public HostAutomationTests() { }

    /// <summary>
    /// Verifies that game time does NOT advance when no other player is connected.
    /// The server's AlwaysOn service pauses the game when otherFarmers.Count == 0
    /// and timeOfDay is between 610 and 2500.
    ///
    /// Uses Exclusive=true to prevent other tests from acquiring this server
    /// during the verification window.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task TimePaused_WhenNoPlayersConnected()
    {
        await Connect.EnsureDisconnectedAsync();

        var ct = TestContext.Current.CancellationToken;
        Log($"Exclusive access granted, refs={Lease!.RefCount}");

        // Wait until no other players are connected (previous test may still be cleaning up).
        var noPlayers = await PollingHelper.LongPollAsync(
            WaitName.Polling_HostAutomation_NoPlayers,
            async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(since: since, isReady: true, playerCount: 0, timeout: remaining, ct: ct);
                if (s != null) Log($"PlayerCount==0 confirmed: PlayerCount={s.PlayerCount}, IsReady={s.IsReady}");
                return new PollingHelper.LongPollResult(s != null, s?.Version ?? since);
            }, TestTimings.ServerReadyBetweenTests, cancellationToken: ct);
        Assert.True(noPlayers, "Server should have no players connected before testing time pause");

        // Set time to a known mid-day value so we're firmly inside the pause window (610-2500)
        var setTimeResult = await ServerApi.SetTime(TestTimings.Noon, ct);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        Log($"Set time to {setTimeResult.TimeOfDay}");

        // Poll until the game reports IsPaused=true (confirms AlwaysOn paused with 0 players)
        var pauseConfirmed = await PollingHelper.LongPollAsync(
            WaitName.Polling_HostAutomation_PauseConfirmed,
            async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(since: since, isPaused: true, timeout: remaining, ct: ct);
                return new PollingHelper.LongPollResult(s != null, s?.Version ?? since);
            }, TestTimings.NetworkSyncTimeout, cancellationToken: ct);
        Assert.True(pauseConfirmed, "Server should report IsPaused=true with no players connected");

        // Read current time
        var status1 = await ServerApi.GetStatus(ct);
        Assert.NotNull(status1);
        var time1 = status1.TimeOfDay;
        Log($"Time reading 1: {time1}, PlayerCount: {status1.PlayerCount}, IsPaused: {status1.IsPaused}");

        // Speed up the clock 10x so the verification window covers more game-ticks
        var speedResult = await ServerApi.SetClockSpeed(10, ct);
        Assert.True(speedResult?.Success, $"SetClockSpeed failed: {speedResult?.Error}");
        Log($"Clock speed set to {speedResult!.Multiplier}x ({speedResult.EffectiveMs}ms/min)");

        try
        {
            // Poll for the verification window. If time advances at any point,
            // the test fails immediately instead of waiting the full duration.
            // At 10x speed, 2s covers ~28 game-ticks worth of verification.
            // No equality filter on TimeOfDay — use `since` to wait for any newer
            // snapshot, then check TimeOfDay-changed on the returned body.
            var timeAdvanced = await PollingHelper.LongPollAsync(
                WaitName.Polling_HostAutomation_TimeAdvanced,
                async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(since: since, timeout: remaining, ct: ct);
                if (s == null) return new PollingHelper.LongPollResult(false, since);
                if (s.TimeOfDay != time1)
                {
                    Log($"Time changed: {time1} → {s.TimeOfDay}, PlayerCount={s.PlayerCount}");
                    return new PollingHelper.LongPollResult(true, s.Version);
                }
                return new PollingHelper.LongPollResult(false, s.Version);
            }, TestTimings.TimePausedVerification, cancellationToken: ct);

            // Time should NOT have advanced (game is paused with no other players)
            Assert.False(timeAdvanced, $"Time should not advance while no players connected, but changed from {time1}");
            LogSuccess("Confirmed: time did not advance while no players connected");
        }
        finally
        {
            // Always restore clock speed
            await ServerApi.SetClockSpeed(1, ct);
        }
    }

    /// <summary>
    /// Verifies that game time DOES advance when another player is connected.
    /// The server's AlwaysOn service unpauses the game when otherFarmers.Count >= 1.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true)]
    public async Task TimeAdvances_WhenPlayerConnected()
    {
        await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;

        // Set time to a known value so the measurement is clean
        var setTimeResult = await ServerApi.SetTime(TestTimings.Noon, ct);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");

        // Poll until game is unpaused (confirms game is running with player connected)
        var unpauseConfirmed = await PollingHelper.LongPollAsync(
            WaitName.Polling_HostAutomation_UnpauseConfirmed,
            async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(since: since, isPaused: false, timeout: remaining, ct: ct);
                if (s == null) return new PollingHelper.LongPollResult(false, since);
                if (s.TimeOfDay >= TestTimings.Noon)
                    return new PollingHelper.LongPollResult(true, s.Version);
                return new PollingHelper.LongPollResult(false, s.Version);
            }, TestTimings.NetworkSyncTimeout, cancellationToken: ct);
        Assert.True(unpauseConfirmed, "Server should report IsPaused=false with a player connected");

        var status1 = await ServerApi.GetStatus(ct);
        var time1 = status1!.TimeOfDay;
        Log($"Time reading 1: {time1}, IsPaused: {status1.IsPaused}");

        // Speed up the clock 10x so tick fires in ~0.7s instead of ~7s
        var speedResult = await ServerApi.SetClockSpeed(10, ct);
        Assert.True(speedResult?.Success, $"SetClockSpeed failed: {speedResult?.Error}");
        Log($"Clock speed set to {speedResult!.Multiplier}x ({speedResult.EffectiveMs}ms/min)");

        try
        {
            // Poll until time advances (should complete in ~0.7s at 10x speed).
            // No equality filter on TimeOfDay — wait for any newer snapshot via
            // `since`, then check the >- condition on the returned body.
            await PollingHelper.LongPollAsync(
                WaitName.Polling_HostAutomation_TimeAdvancedSecond,
                async (since, remaining) =>
                {
                    var s = await ServerApi.WaitForStatusAsync(since: since, timeout: remaining, ct: ct);
                    if (s == null) return new PollingHelper.LongPollResult(false, since);
                    return new PollingHelper.LongPollResult(s.TimeOfDay > time1, s.Version);
                }, TestTimings.TimeAdvanceWait, cancellationToken: ct);
        }
        finally
        {
            // Always restore clock speed
            await ServerApi.SetClockSpeed(1, ct);
        }

        var statusFinal = await ServerApi.GetStatus(ct);
        var time2 = statusFinal!.TimeOfDay;

        // Time should have advanced
        Assert.True(time2 > time1,
            $"Time should have advanced with a player connected, but went from {time1} to {time2}");

        LogSuccess($"Confirmed: time advanced from {time1} to {time2} (+{time2 - time1} game-minutes)");
    }

    /// <summary>
    /// Verifies that when a connected player goes to sleep, the host bot
    /// automatically sleeps too, triggering a day transition.
    ///
    /// AlwaysOn.HandleAutoSleep() detects (numberRequired - numReady == 1)
    /// and calls startSleep() on the host.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true)]
    public async Task HostAutoSleeps_WhenPlayerSleeps()
    {
        await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        // Record the current day
        var statusBefore = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
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

        // Verify the farmhand is still connected after the sleep action.
        // If it disconnected immediately, the day change would be a false positive
        // (server auto-sleeps with 0 players, not reacting to the farmhand's sleep).
        var postSleepState = await GameClient.GetState();
        Assert.NotNull(postSleepState);
        Assert.True(postSleepState.IsConnected,
            "Farmhand disconnected immediately after sleep action. " +
            "cannot verify host auto-sleep behavior");

        // Wait for the day to transition while monitoring the connection.
        // The host bot should detect the farmhand is ready to sleep,
        // then auto-sleep itself, triggering NewDay().
        // We check the connection on every poll so we can fail fast with a clear
        // error instead of waiting the full timeout on a false positive.
        var (dayChanged, disconnected) = await DayChange.WaitAsync(
            dayBefore, seasonBefore, yearBefore, checkConnection: true, TestContext.Current.CancellationToken);

        Assert.False(disconnected,
            "False positive: farmhand disconnected during day change wait. " +
            "the day change (if any) was caused by the server auto-sleeping with " +
            "0 players, not by the host reacting to the farmhand's sleep request");
        Assert.True(dayChanged, "Day should have advanced after farmhand slept and host auto-slept");

        var statusAfter = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(statusAfter);
        Log($"After sleep: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}");

        // Verify it's actually a new day (not the same day)
        Assert.True(
            statusAfter.Day != dayBefore || statusAfter.Season != seasonBefore || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}");
    }

    /// <summary>
    /// Verifies that when time reaches 2:00 AM (2600 game-time) with a player connected,
    /// the game triggers a pass-out and the host bot ensures the day transitions.
    ///
    /// At 2600, AlwaysOn unpauses the game, the game forces performPassoutWarp(),
    /// and both players transition to the next day.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true)]
    public async Task HostPassesOut_WhenTimeReaches2AM()
    {
        await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        // Record the current day
        var statusBefore = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        Log($"Before pass-out: {seasonBefore} {dayBefore}, Year {yearBefore}, Time {statusBefore.TimeOfDay}");

        // Set time to 2550, just one 10-minute tick before 2:00 AM (2600).
        var setTimeResult = await ServerApi.SetTime(TestTimings.PrePassOutTime, TestContext.Current.CancellationToken);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        Log($"Set time to {setTimeResult.TimeOfDay}, waiting for 2:00 AM pass-out...");

        // Speed up clock 10x so the tick from 2550→2600 takes ~0.7s instead of ~7s
        var speedResult = await ServerApi.SetClockSpeed(10, TestContext.Current.CancellationToken);
        Assert.True(speedResult?.Success, $"SetClockSpeed failed: {speedResult?.Error}");

        bool dayChanged;
        try
        {
            // Wait for the day to transition.
            // At 2600, the game forces pass-out → sleep ready check → day transition.
            dayChanged = await DayChange.WaitAsync(dayBefore, seasonBefore, yearBefore, TestContext.Current.CancellationToken);
        }
        finally
        {
            // Always restore clock speed
            await ServerApi.SetClockSpeed(1, TestContext.Current.CancellationToken);
        }
        Assert.True(dayChanged, "Day should have advanced after 2:00 AM pass-out");

        var statusAfter = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(statusAfter);
        Log($"After pass-out: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}");

        Assert.True(
            statusAfter.Day != dayBefore || statusAfter.Season != seasonBefore || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}");
    }

    /// <summary>
    /// Regression for issue #242: the Mr. Qi mystery-box overnight cutscene (QiPlaneEvent) hangs the
    /// host, so the new day never starts. QiPlaneEvent's completion gate is advanced in its draw(),
    /// not its tickUpdate(), so on a headless host (draws gated/desynced) it never converges.
    /// QiPlaneEventOverrides force-completes it via a framerate-independent game-time fallback.
    ///
    /// Runs across an FPS matrix because the fix must NOT assume a particular SERVER_FPS — the bug
    /// reporter was on unbounded FPS. The day must advance whether rendering is disabled (0) or capped.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1)]
    [TestServer(Exclusive = true)]
    public async Task HostCompletesQiPlaneEvent_AcrossFpsMatrix(int fps)
    {
        var ct = TestContext.Current.CancellationToken;
        await Farmers.ConnectNewAsync(ct: ct);

        var initialRendering = await ServerApi.GetRendering(ct);
        Assert.NotNull(initialRendering);
        var initialFps = initialRendering.Fps;

        try
        {
            var fpsResult = await ServerApi.SetServerFps(fps, ct);
            Assert.True(fpsResult?.Success, $"SetServerFps({fps}) failed: {fpsResult?.Error}");

            // Queue the Mr. Qi mystery-box event for the next overnight transition.
            var queueResult = await ServerApi.QueueFarmEvent("qiplane", ct);
            Assert.NotNull(queueResult);
            Assert.True(queueResult.Success, $"QueueFarmEvent failed: {queueResult.Error}");

            var statusBefore = await ServerApi.GetStatus(ct);
            Assert.NotNull(statusBefore);
            var dayBefore = statusBefore.Day;
            var seasonBefore = statusBefore.Season;
            var yearBefore = statusBefore.Year;
            Log($"Before sleep (fps={fps}): {seasonBefore} {dayBefore}, Year {yearBefore}");

            // Sleep the farmhand; the host auto-sleeps and runs the overnight QiPlaneEvent.
            var sleepResult = await GameClient.Actions.Sleep();
            Assert.True(sleepResult?.Success, $"Sleep action failed: {sleepResult?.Error}");

            // Without the fix the overnight transition hangs here (QiPlaneEvent never completes) and
            // this times out. With the fix the game-time fallback completes the event and the day starts.
            var (dayChanged, disconnected) = await DayChange.WaitAsync(
                dayBefore, seasonBefore, yearBefore, checkConnection: true, ct);

            Assert.False(disconnected, "Farmhand disconnected during the QiPlaneEvent overnight wait");
            Assert.True(dayChanged,
                $"Day should advance after the host completes the overnight QiPlaneEvent (fps={fps})");
        }
        finally
        {
            // Restore the server's render rate so the shared server isn't left at the test's FPS.
            // Use a fresh cleanup token (not ct) so the restore still runs if the test was cancelled
            // mid-body — otherwise a timeout would leave the shared server stuck at the test's FPS.
            using var cleanupCts = new CancellationTokenSource(TestTimings.CleanupTimeout);
            await ServerApi.SetServerFps(initialFps, cleanupCts.Token);
        }
    }

}
