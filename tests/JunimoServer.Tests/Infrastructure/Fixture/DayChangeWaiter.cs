using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Infrastructure.Fixture;

internal sealed class DayChangeWaiter
{
    private readonly TestBase _testBase;
    private readonly string _displayName;

    public DayChangeWaiter(TestBase testBase, string displayName)
    {
        _testBase = testBase;
        _displayName = displayName;
    }

    /// <summary>
    /// Polls GET /status until the day/season/year changes from the given values,
    /// then waits for IsReady to confirm the visual transition is complete.
    /// </summary>
    public async Task<bool> WaitAsync(
        int day,
        string season,
        int year,
        CancellationToken ct = default
    )
    {
        var (dayChanged, _) = await WaitAsync(day, season, year, checkConnection: false, ct);
        return dayChanged;
    }

    /// <summary>
    /// Polls GET /status until the day/season/year changes, optionally checking
    /// that the game client remains connected. After detection, waits for IsReady
    /// to ensure the visual transition (newDaySync, save, map loading) is complete.
    /// </summary>
    public async Task<(bool DayChanged, bool Disconnected)> WaitAsync(
        int day,
        string season,
        int year,
        bool checkConnection,
        CancellationToken ct = default
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + TestTimings.DayChangeTimeout;
        long since = 0;
        var attempt = 0;

        // Outer long-poll: each iteration calls /wait/status with cursor `since`
        // and a server-side hard cap (10s). When `checkConnection` is true we
        // interleave a GameClient liveness check between iterations — once per
        // 10s window instead of the prior 4× / 1s cadence, but still well below
        // any meaningful disconnection latency.
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                if (checkConnection && attempt > 1)
                {
                    var gameState = await _testBase.GameClient.GetState();
                    if (gameState?.IsConnected != true)
                    {
                        LogWarning(
                            $"Farmhand disconnected during day change wait (attempt {attempt})"
                        );
                        return (false, true);
                    }
                }

                var outerRemaining = deadline - DateTime.UtcNow;
                if (outerRemaining <= TimeSpan.Zero)
                {
                    break;
                }

                var status = await _testBase.ServerApi.WaitForStatusAsync(
                    since: since,
                    timeout: outerRemaining,
                    ct: ct
                );
                if (status == null)
                {
                    continue; // 408 — re-issue under our deadline
                }

                since = status.Version;

                if (status.Day != day || status.Season != season || status.Year != year)
                {
                    Log(
                        FormattableString.Invariant(
                            $"Day changed after {sw.Elapsed.TotalSeconds:F1}s: "
                        ) + $"{season} {day} Y{year} -> {status.Season} {status.Day} Y{status.Year}"
                    );

                    if (checkConnection)
                    {
                        var finalState = await _testBase.GameClient.GetState();
                        if (finalState?.IsConnected != true)
                        {
                            LogWarning("Farmhand disconnected during day transition");
                            return (true, true);
                        }
                    }

                    // Wait for the day TRANSITION to finish (newDaySync barrier + save + map load),
                    // NOT for the composite IsReady. IsReady (= GameServer.isGameAvailable) also stays
                    // false through a post-transition festival or wedding (those keep weddingsToday /
                    // CurrentEvent.isWedding / isFestival set), so settling on IsReady dead-waits the
                    // full festival/wedding here — e.g. the same-day-weddings test burned the whole
                    // 30s DayTransitionSettleTimeout before its own wedding poll even started.
                    // DayTransitionComplete flips true the moment the transition itself is done,
                    // regardless of those activities; the caller's scenario poll (festival entry,
                    // wedding render) then takes over. On an ordinary day it equals IsReady, so
                    // non-festival/non-wedding tests are unchanged.
                    var readySw = System.Diagnostics.Stopwatch.StartNew();
                    var settled = await PollingHelper.LongPollAsync(
                        WaitName.Polling_TestBase_PostTransitionSettle,
                        async (settleSince, settleRemaining) =>
                        {
                            var s = await _testBase.ServerApi.WaitForStatusAsync(
                                since: settleSince,
                                dayTransitionComplete: true,
                                timeout: settleRemaining,
                                ct: ct
                            );
                            return new PollingHelper.LongPollResult(
                                s != null,
                                s?.Version ?? settleSince
                            );
                        },
                        TestTimings.DayTransitionSettleTimeout,
                        cancellationToken: ct
                    );
                    Log(
                        $"Post-transition settle: {readySw.ElapsedMilliseconds}ms"
                            + $"{(settled ? "" : " (timed out)")}"
                    );

                    return (true, false);
                }

                if (attempt % 5 == 0)
                {
                    LogDetail(
                        $"Still waiting for day change... (time={status.TimeOfDay}, attempt {attempt})"
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogWarning($"Status poll error: {ex.Message}");
            }
        }

        LogWarning(
            $"Timed out waiting for day change after {TestTimings.DayChangeTimeout.TotalSeconds}s"
        );
        try
        {
            await FailureContext.DumpAsync(
                _testBase.ServerApi,
                reason: "WaitForDayChangeAsync_timeout",
                extras: new Dictionary<string, object?>
                {
                    ["expectedDay"] = day,
                    ["expectedSeason"] = season,
                    ["expectedYear"] = year,
                    ["attempts"] = attempt,
                    ["checkConnection"] = checkConnection,
                }
            );
        }
        catch
        { /* diagnostic dump is best-effort; don't mask the test's real failure */
        }
        return (false, false);
    }

    private void Log(string message) =>
        SetupEventBus.EmitTestAnnotation(
            _displayName,
            AnnotationLevel.Info,
            AnnotationSource.Body,
            message
        );

    private void LogDetail(string message) =>
        SetupEventBus.EmitTestAnnotation(
            _displayName,
            AnnotationLevel.Detail,
            AnnotationSource.Body,
            message
        );

    private void LogWarning(string message) =>
        SetupEventBus.EmitTestAnnotation(
            _displayName,
            AnnotationLevel.Warning,
            AnnotationSource.Body,
            message
        );
}
