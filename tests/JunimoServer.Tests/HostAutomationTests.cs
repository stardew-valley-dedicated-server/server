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
    /// The server's AlwaysOn service pauses the game whenever no authenticated player is
    /// present and the host is at rest, at any time of day.
    ///
    /// Uses Exclusive=true to prevent other tests from acquiring this server
    /// during the verification window.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task TimePaused_WhenNoPlayersConnected()
    {
        await Connect.EnsureDisconnectedAsync();

        var ct = TestCt;
        Log($"Exclusive access granted, refs={Lease!.RefCount}");

        await WaitForNoPlayersAsync(ct);

        // Set time to a known mid-day value
        var setTimeResult = await ServerApi.SetTime(TestTimings.Noon, ct);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        Log($"Set time to {setTimeResult.TimeOfDay}");

        // Poll until the game reports IsPaused=true (confirms AlwaysOn paused with 0 players)
        var pauseConfirmed = await PollingHelper.LongPollAsync(
            WaitName.Polling_HostAutomation_PauseConfirmed,
            async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(
                    since: since,
                    isPaused: true,
                    timeout: remaining,
                    ct: ct
                );
                return new PollingHelper.LongPollResult(s != null, s?.Version ?? since);
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(pauseConfirmed, "Server should report IsPaused=true with no players connected");

        // Read current time
        var status1 = await ServerApi.GetStatus(ct);
        Assert.NotNull(status1);
        var time1 = status1.TimeOfDay;
        Log(
            $"Time reading 1: {time1}, PlayerCount: {status1.PlayerCount}, IsPaused: {status1.IsPaused}"
        );

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
                    var s = await ServerApi.WaitForStatusAsync(
                        since: since,
                        timeout: remaining,
                        ct: ct
                    );
                    if (s == null)
                    {
                        return new PollingHelper.LongPollResult(false, since);
                    }

                    if (s.TimeOfDay != time1)
                    {
                        Log($"Time changed: {time1} → {s.TimeOfDay}, PlayerCount={s.PlayerCount}");
                        return new PollingHelper.LongPollResult(true, s.Version);
                    }
                    return new PollingHelper.LongPollResult(false, s.Version);
                },
                TestTimings.TimePausedVerification,
                cancellationToken: ct
            );

            // Time should NOT have advanced (game is paused with no other players)
            Assert.False(
                timeAdvanced,
                $"Time should not advance while no players connected, but changed from {time1}"
            );
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
        await Farmers.ConnectFastAsync(ct: TestCt);

        var ct = TestCt;

        // Set time to a known value so the measurement is clean
        var setTimeResult = await ServerApi.SetTime(TestTimings.Noon, ct);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");

        // Poll until game is unpaused (confirms game is running with player connected)
        var unpauseConfirmed = await PollingHelper.LongPollAsync(
            WaitName.Polling_HostAutomation_UnpauseConfirmed,
            async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(
                    since: since,
                    isPaused: false,
                    timeout: remaining,
                    ct: ct
                );
                if (s == null)
                {
                    return new PollingHelper.LongPollResult(false, since);
                }

                if (s.TimeOfDay >= TestTimings.Noon)
                {
                    return new PollingHelper.LongPollResult(true, s.Version);
                }

                return new PollingHelper.LongPollResult(false, s.Version);
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            unpauseConfirmed,
            "Server should report IsPaused=false with a player connected"
        );

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
                    var s = await ServerApi.WaitForStatusAsync(
                        since: since,
                        timeout: remaining,
                        ct: ct
                    );
                    if (s == null)
                    {
                        return new PollingHelper.LongPollResult(false, since);
                    }

                    return new PollingHelper.LongPollResult(s.TimeOfDay > time1, s.Version);
                },
                TestTimings.TimeAdvanceWait,
                cancellationToken: ct
            );
        }
        finally
        {
            // Always restore clock speed
            await ServerApi.SetClockSpeed(1, ct);
        }

        var statusFinal = await ServerApi.GetStatus(ct);
        var time2 = statusFinal!.TimeOfDay;

        // Time should have advanced
        Assert.True(
            time2 > time1,
            $"Time should have advanced with a player connected, but went from {time1} to {time2}"
        );

        LogSuccess(
            $"Confirmed: time advanced from {time1} to {time2} (+{time2 - time1} game-minutes)"
        );
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
        await Farmers.ConnectFastAsync(ct: TestCt);

        // Record the current day
        var statusBefore = await ServerApi.GetStatus(TestCt);
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        Log(
            $"Before sleep: {seasonBefore} {dayBefore}, Year {yearBefore}, Time {statusBefore.TimeOfDay}"
        );

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
        Assert.True(
            postSleepState.IsConnected,
            "Farmhand disconnected immediately after sleep action. "
                + "cannot verify host auto-sleep behavior"
        );

        // Wait for the day to transition while monitoring the connection.
        // The host bot should detect the farmhand is ready to sleep,
        // then auto-sleep itself, triggering NewDay().
        // We check the connection on every poll so we can fail fast with a clear
        // error instead of waiting the full timeout on a false positive.
        var (dayChanged, disconnected) = await DayChange.WaitAsync(
            dayBefore,
            seasonBefore,
            yearBefore,
            checkConnection: true,
            TestCt
        );

        Assert.False(
            disconnected,
            "False positive: farmhand disconnected during day change wait. "
                + "the day change (if any) was caused by the server auto-sleeping with "
                + "0 players, not by the host reacting to the farmhand's sleep request"
        );
        Assert.True(
            dayChanged,
            "Day should have advanced after farmhand slept and host auto-slept"
        );

        var statusAfter = await ServerApi.GetStatus(TestCt);
        Assert.NotNull(statusAfter);
        Log(
            $"After sleep: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}"
        );

        // Verify it's actually a new day (not the same day)
        Assert.True(
            statusAfter.Day != dayBefore
                || statusAfter.Season != seasonBefore
                || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}"
        );
    }

    /// <summary>
    /// Verifies that when time reaches 2:00 AM (2600 game-time) with a player connected,
    /// the game triggers a pass-out and the host bot ensures the day transitions.
    ///
    /// With a player present the world runs, so at 2600 the game forces performPassoutWarp()
    /// and both players transition to the next day.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true)]
    public async Task HostPassesOut_WhenTimeReaches2AM()
    {
        await Farmers.ConnectFastAsync(ct: TestCt);

        // Record the current day
        var statusBefore = await ServerApi.GetStatus(TestCt);
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        Log(
            $"Before pass-out: {seasonBefore} {dayBefore}, Year {yearBefore}, Time {statusBefore.TimeOfDay}"
        );

        // Set time to 2550, just one 10-minute tick before 2:00 AM (2600).
        var setTimeResult = await ServerApi.SetTime(TestTimings.PrePassOutTime, TestCt);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        Log($"Set time to {setTimeResult.TimeOfDay}, waiting for 2:00 AM pass-out...");

        // Speed up clock 10x so the tick from 2550→2600 takes ~0.7s instead of ~7s
        var speedResult = await ServerApi.SetClockSpeed(10, TestCt);
        Assert.True(speedResult?.Success, $"SetClockSpeed failed: {speedResult?.Error}");

        bool dayChanged;
        try
        {
            // Wait for the day to transition.
            // At 2600, the game forces pass-out → sleep ready check → day transition.
            dayChanged = await DayChange.WaitAsync(dayBefore, seasonBefore, yearBefore, TestCt);
        }
        finally
        {
            // Always restore clock speed
            await ServerApi.SetClockSpeed(1, TestCt);
        }
        Assert.True(dayChanged, "Day should have advanced after 2:00 AM pass-out");

        var statusAfter = await ServerApi.GetStatus(TestCt);
        Assert.NotNull(statusAfter);
        Log(
            $"After pass-out: {statusAfter.Season} {statusAfter.Day}, Year {statusAfter.Year}, Time {statusAfter.TimeOfDay}"
        );

        Assert.True(
            statusAfter.Day != dayBefore
                || statusAfter.Season != seasonBefore
                || statusAfter.Year != yearBefore,
            $"Expected a new day but still on {seasonBefore} {dayBefore}, Year {yearBefore}"
        );
    }

    /// <summary>
    /// Regression for issue #242: the Mr. Qi mystery-box overnight cutscene (QiPlaneEvent) hangs the
    /// host, so the new day never starts. QiPlaneEvent's completion gate is advanced in its draw(),
    /// not its tickUpdate(), so on a headless host (draws gated/desynced) it never converges.
    /// QiPlaneEventOverrides drives the gate forward from tickUpdate the way an Escape-holding player
    /// would, draw-independent (the test client does the same via QiPlaneSkip).
    ///
    /// Brackets draws-disabled (0) and draws-capped (5): 0 is the original #242 condition (the only
    /// row that catches a reintroduced draw dependency), 5 is the CI-proven server FPS. Completion is
    /// draw-independent, so intermediate caps add no distinct coverage.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [TestServer(Exclusive = true)]
    public async Task HostCompletesQiPlaneEvent_AcrossFpsMatrix(int fps)
    {
        var ct = TestCt;
        await Farmers.ConnectFastAsync(ct: ct);

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
            // this times out. With the fix the host drives the event's gate forward from tickUpdate,
            // completing it so the day starts.
            var (dayChanged, disconnected) = await DayChange.WaitAsync(
                dayBefore,
                seasonBefore,
                yearBefore,
                checkConnection: true,
                ct
            );

            Assert.False(
                disconnected,
                "Farmhand disconnected during the QiPlaneEvent overnight wait"
            );
            Assert.True(
                dayChanged,
                $"Day should advance after the host completes the overnight QiPlaneEvent (fps={fps})"
            );
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

    /// <summary>
    /// A festival date used to be excluded from the empty-server pause, so an empty server
    /// ran through the whole festival day to the 2:00 AM pass-out. The pause now keys on
    /// presence and the host being at rest, not on the calendar.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task TimePaused_WhenNoPlayersConnected_OnFestivalDay()
    {
        await Connect.EnsureDisconnectedAsync();
        var ct = TestCt;
        await WaitForNoPlayersAsync(ct);

        var statusBefore = await ServerApi.GetStatus(ct);
        Assert.NotNull(statusBefore);

        try
        {
            // Egg Festival. /test/set_date runs the new-day reset, so the clock is at 6:00.
            var setDate = await ServerApi.SetDate("spring", 13, statusBefore.Year, ct);
            Assert.True(setDate?.Success, $"SetDate(spring 13) failed: {setDate?.Error}");

            // The scenario proof: the server's own festival-date predicate must read true.
            // whereIsTodaysFest can't serve here — it is only written by the ten-minute clock
            // tick, which the pause under test prevents.
            var festival = await ServerApi.GetFestivalState(ct);
            Assert.True(
                festival?.IsFestivalDay == true,
                "IsFestivalDay must be true after SetDate(spring 13); the test would otherwise run on an ordinary day"
            );

            await WaitForPausedAsync(WaitName.Polling_HostAutomation_FestivalDayPauseConfirmed, ct);

            var status = await ServerApi.GetStatus(ct);
            Assert.NotNull(status);
            Assert.True(
                status.TimeOfDay == 600,
                $"Empty server on a festival date must hold at 6:00, got {status.TimeOfDay} (the clock ran before the pause engaged)"
            );
            LogSuccess("Empty server paused at 6:00 on the Egg Festival date");
        }
        finally
        {
            // Leave the shared server on an ordinary date for the next test.
            using var cleanupCts = new CancellationTokenSource(TestTimings.CleanupTimeout);
            await ServerApi.SetDate("spring", 14, statusBefore.Year, cleanupCts.Token);
        }
    }

    /// <summary>
    /// Past 1:00 AM an empty server pauses like at any other time, then closes the day
    /// through the host's own sleep once AUTO_SLEEP_GRACE_SECONDS (seconds in the test config)
    /// elapse with nobody returning. The clock never runs unattended: it must still read the
    /// value set here when the pause is confirmed, and the new day must start paused at 6:00.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task HostSleepsAfterGrace_WhenServerEmptyPastOneAm()
    {
        await Connect.EnsureDisconnectedAsync();
        var ct = TestCt;
        await WaitForNoPlayersAsync(ct);

        var before = await ServerApi.GetStatus(ct);
        Assert.NotNull(before);

        var setTime = await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        Assert.True(setTime?.Success, $"SetTime failed: {setTime?.Error}");

        await WaitForPausedAsync(WaitName.Polling_HostAutomation_GracePauseConfirmed, ct);
        var paused = await ServerApi.GetStatus(ct);
        Assert.NotNull(paused);
        Assert.True(
            paused.TimeOfDay == TestTimings.PrePassOutTime,
            $"Empty server past 1:00 AM must pause at {TestTimings.PrePassOutTime}, got {paused.TimeOfDay} (the clock ran unattended)"
        );

        var dayChanged = await DayChange.WaitAsync(before.Day, before.Season, before.Year, ct);
        Assert.True(
            dayChanged,
            "Day should advance through the host's grace sleep with nobody connected"
        );

        await WaitForPausedAsync(WaitName.Polling_HostAutomation_NextDayPauseConfirmed, ct);
        var morning = await ServerApi.GetStatus(ct);
        Assert.NotNull(morning);
        Assert.True(
            morning.TimeOfDay == 600,
            $"The new day must start paused at 6:00, got {morning.TimeOfDay}"
        );
        LogSuccess(
            $"Empty server slept after the grace period: {before.Season} {before.Day} → {morning.Season} {morning.Day}, paused at 6:00"
        );
    }

    /// <summary>
    /// An overnight farm event ticks only while the world is unpaused. On an empty server the
    /// grace sleep drives the night, the event plays inside the transition the guard keeps
    /// unpaused, and the pause must engage at 6:00 only once the event is gone.
    /// Uses the earthquake sound event: farmEventOverride bypasses the random pick, its setUp has
    /// no preconditions, and it needs several seconds of unpaused ticks (the Qi plane event
    /// completes on its first host tick, so it never meets the pause).
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task HostCompletesFarmEvent_AfterGraceSleep_WhenServerEmpty()
    {
        await Connect.EnsureDisconnectedAsync();
        var ct = TestCt;
        await WaitForNoPlayersAsync(ct);

        var queueResult = await ServerApi.QueueFarmEvent("earthquake", ct);
        Assert.True(queueResult?.Success, $"QueueFarmEvent failed: {queueResult?.Error}");

        var before = await ServerApi.GetStatus(ct);
        Assert.NotNull(before);

        var setTime = await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        Assert.True(setTime?.Success, $"SetTime failed: {setTime?.Error}");

        var dayChanged = await DayChange.WaitAsync(before.Day, before.Season, before.Year, ct);
        Assert.True(
            dayChanged,
            "Day should advance through the grace sleep with a farm event queued"
        );

        // The day counter, the pause flag, and timeOfDay=600 all read the same whether the event
        // completed or is still up, so the completion itself is the assertion: a stalled event
        // keeps farmEvent non-null.
        var eventCompleted = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_HostAutomation_FarmEventCompleted,
            async () =>
            {
                var state = await ServerApi.GetFarmEventState(ct);
                return state?.Success == true && !state.Active;
            },
            timeout: TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            eventCompleted,
            "The overnight farm event must complete on the empty server; a farmEvent still up means the pause froze it"
        );

        await WaitForPausedAsync(WaitName.Polling_HostAutomation_FarmEventDayPauseConfirmed, ct);
        var morning = await ServerApi.GetStatus(ct);
        Assert.NotNull(morning);
        Assert.True(
            morning.TimeOfDay == 600,
            $"The new day must start paused at 6:00 after the farm event, got {morning.TimeOfDay}"
        );
        LogSuccess("Empty server ran the overnight farm event to completion and paused at 6:00");
    }

    /// <summary>
    /// The last player leaves at 2:00 AM, during or just before the host's pass-out. The game
    /// ends the day only through that pass-out (UpdateOther fires it once the clock reads 2600,
    /// the animation hands over to NewDay), and every step of it runs inside the block the pause
    /// gates. The at-rest check must keep the pause off from 2600 until the transition starts;
    /// otherwise the host freezes mid-pass-out until someone joins. Setting 2600 on an empty
    /// server reproduces the exact state the departing player leaves behind.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task HostPassesOut_WhenServerEmptyAt2AM()
    {
        await Connect.EnsureDisconnectedAsync();
        var ct = TestCt;
        await WaitForNoPlayersAsync(ct);

        var before = await ServerApi.GetStatus(ct);
        Assert.NotNull(before);

        var setTime = await ServerApi.SetTime(TestTimings.PassOutTime, ct);
        Assert.True(setTime?.Success, $"SetTime failed: {setTime?.Error}");

        var dayChanged = await DayChange.WaitAsync(before.Day, before.Season, before.Year, ct);
        Assert.True(
            dayChanged,
            "The empty server must pass out at 2:00 AM and start the next day; a pause engaged at 2600 freezes the pass-out"
        );

        await WaitForPausedAsync(WaitName.Polling_HostAutomation_PassOutDayPauseConfirmed, ct);
        var morning = await ServerApi.GetStatus(ct);
        Assert.NotNull(morning);
        Assert.True(
            morning.TimeOfDay == 600,
            $"The new day must start paused at 6:00 after the pass-out, got {morning.TimeOfDay}"
        );
        LogSuccess("Empty server passed out at 2:00 AM and paused at 6:00");
    }

    /// <summary>
    /// Wait until no other players are connected (a previous test may still be cleaning up).
    /// </summary>
    private async Task WaitForNoPlayersAsync(CancellationToken ct)
    {
        var noPlayers = await PollingHelper.LongPollAsync(
            WaitName.Polling_HostAutomation_NoPlayers,
            async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(
                    since: since,
                    isReady: true,
                    playerCount: 0,
                    timeout: remaining,
                    ct: ct
                );
                if (s != null)
                {
                    Log(
                        $"PlayerCount==0 confirmed: PlayerCount={s.PlayerCount}, IsReady={s.IsReady}"
                    );
                }

                return new PollingHelper.LongPollResult(s != null, s?.Version ?? since);
            },
            TestTimings.ServerReadyBetweenTests,
            cancellationToken: ct
        );
        Assert.True(noPlayers, "Server should have no players connected before testing time pause");
    }

    /// <summary>
    /// Long-poll the status snapshot until it reports IsPaused=true.
    /// </summary>
    private async Task WaitForPausedAsync(WaitName waitName, CancellationToken ct)
    {
        var pauseConfirmed = await PollingHelper.LongPollAsync(
            waitName,
            async (since, remaining) =>
            {
                var s = await ServerApi.WaitForStatusAsync(
                    since: since,
                    isPaused: true,
                    timeout: remaining,
                    ct: ct
                );
                return new PollingHelper.LongPollResult(s != null, s?.Version ?? since);
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(pauseConfirmed, "Server should report IsPaused=true with no players connected");
    }
}
