using System;
using System.Collections.Generic;
using System.Linq;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace JunimoServer.Services.AlwaysOn;

public class AlwaysOnServerFestivals
{
    private const string StartNowText = "Type !event to start now";

    // When the timeout backstop clock starts ticking
    private enum TimeoutStart
    {
        OnEntry,
        AfterMainEvent,
    }

    private class FestivalSpec
    {
        public Func<bool> IsToday;

        // Clear active-festival state once in-game time reaches this (HHMM, e.g. 1410 = 14:10)
        public int ResetCutoff;
        public bool RestoreHudOnReset;

        // Main-event festivals run a host-triggered countdown then start the event;
        // leave-only festivals (HasMainEvent = false) just wait for players to leave.
        public bool HasMainEvent;
        public Func<int> CountdownSeconds;
        public string AnnounceText;
        public Action OnAnnounce; // e.g. add the iridium starfruit to the Luau soup
        public bool AutoEndAfterCountdown; // host ends the festival once the countdown elapses (Fair)
        public string EndLogText; // logged when AutoEndAfterCountdown fires

        // Wall-clock backstop: end the festival and warp everyone home once this elapses
        // (from entry or after the main event)
        public Func<double> TimeoutSeconds;

        // Only read on the main-event path; leave-only festivals leave it defaulted (OnEntry).
        public TimeoutStart TimeoutStart;
    }

    private readonly List<FestivalSpec> _festivals;

    // Active festival and main-event countdown state
    private FestivalSpec _activeFestival;
    private DateTime? _runStartTime;
    private bool _announced;
    private bool _started;
    private bool _eventCommandUsed;

    // Wall-clock timeout backstop state
    private DateTime? _timeoutStartTime;
    private bool _timeoutWarned;

    private bool _warpingToFestival;
    private bool _startedFestivalEnd;

    // Independent per-second throttles for the two diagnostic log lines
    private DateTime _lastStartLogTime = DateTime.MinValue;
    private DateTime _lastLeaveLogTime = DateTime.MinValue;

    protected readonly IModHelper _helper;
    protected readonly IMonitor _monitor;
    protected readonly AlwaysOnConfig Config;

    public AlwaysOnServerFestivals(
        IModHelper helper,
        IMonitor monitor,
        ChatCommandsService chatCommandService,
        AlwaysOnConfig config
    )
    {
        _helper = helper;
        _monitor = monitor;
        Config = config;

        _festivals = BuildFestivalSpecs();

        chatCommandService.RegisterCommand(
            "event",
            "Tries to start the current festival's event.",
            StartEventCommand
        );
    }

    private List<FestivalSpec> BuildFestivalSpecs()
    {
        return new List<FestivalSpec>
        {
            new FestivalSpec
            {
                IsToday = SDateHelper.IsEggFestivalToday,
                ResetCutoff = 1410,
                HasMainEvent = true,
                CountdownSeconds = () => Config.EggHuntCountdownSeconds,
                AnnounceText = "The Egg Hunt will begin in {0:0.#} minutes.",
                TimeoutStart = TimeoutStart.AfterMainEvent,
                TimeoutSeconds = () => Config.EggFestivalTimeOutSeconds,
            },
            new FestivalSpec
            {
                IsToday = SDateHelper.IsFlowerDanceToday,
                ResetCutoff = 1410,
                HasMainEvent = true,
                CountdownSeconds = () => Config.FlowerDanceCountdownSeconds,
                AnnounceText = "The Flower Dance will begin in {0:0.#} minutes.",
                TimeoutStart = TimeoutStart.AfterMainEvent,
                TimeoutSeconds = () => Config.FlowerDanceTimeOutSeconds,
            },
            new FestivalSpec
            {
                IsToday = SDateHelper.IsLuauToday,
                ResetCutoff = 1410,
                HasMainEvent = true,
                CountdownSeconds = () => Config.LuauSoupCountdownSeconds,
                AnnounceText = "The Soup Tasting will begin in {0:0.#} minutes.",
                OnAnnounce = AddIridiumStarfruitToSoup,
                TimeoutStart = TimeoutStart.AfterMainEvent,
                TimeoutSeconds = () => Config.LuauTimeOutSeconds,
            },
            new FestivalSpec
            {
                IsToday = SDateHelper.IsDanceOfJelliesToday,
                ResetCutoff = 2410,
                HasMainEvent = true,
                CountdownSeconds = () => Config.JellyDanceCountdownSeconds,
                AnnounceText = "The Dance of the Moonlight Jellies will begin in {0:0.#} minutes.",
                TimeoutStart = TimeoutStart.AfterMainEvent,
                TimeoutSeconds = () => Config.DanceOfJelliesTimeOutSeconds,
            },
            new FestivalSpec
            {
                IsToday = SDateHelper.IsStardewValleyFairToday,
                ResetCutoff = 1510,
                RestoreHudOnReset = true,
                HasMainEvent = true,
                CountdownSeconds = () => Config.GrangeDisplayCountdownSeconds,
                AnnounceText = "The Grange Judging will begin in {0:0.#} minutes.",
                AutoEndAfterCountdown = true,
                TimeoutStart = TimeoutStart.OnEntry,
                TimeoutSeconds = () => Config.FairTimeOutSeconds,
                EndLogText = "Grange display finished, triggering festival end",
            },
            new FestivalSpec
            {
                IsToday = SDateHelper.IsSpiritsEveToday,
                ResetCutoff = 2400,
                RestoreHudOnReset = true,
                HasMainEvent = false,
                TimeoutSeconds = () => Config.SpiritsEveTimeOutSeconds,
            },
            new FestivalSpec
            {
                IsToday = SDateHelper.IsFestivalOfIceToday,
                ResetCutoff = 1410,
                HasMainEvent = true,
                CountdownSeconds = () => Config.IceFishingCountdownSeconds,
                AnnounceText = "The Ice Fishing Contest will begin in {0:0.#} minutes.",
                TimeoutStart = TimeoutStart.AfterMainEvent,
                TimeoutSeconds = () => Config.FestivalOfIceTimeOutSeconds,
            },
            new FestivalSpec
            {
                IsToday = SDateHelper.IsFeastOfWinterStarToday,
                ResetCutoff = 1410,
                HasMainEvent = false,
                TimeoutSeconds = () => Config.WinterStarTimeOutSeconds,
            },
        };
    }

    /// <summary>
    /// Get elapsed seconds since a start time, or 0 if not started.
    /// </summary>
    private static double ElapsedSeconds(DateTime? startTime)
    {
        if (!startTime.HasValue)
        {
            return 0;
        }

        return (DateTime.UtcNow - startTime.Value).TotalSeconds;
    }

    private void AddIridiumStarfruitToSoup()
    {
        var item = new SObject("268", 1, false, -1, 3);
        _helper
            .Reflection.GetMethod(Game1.CurrentEvent, "addItemToLuauSoup")
            .Invoke(item, Game1.player);
    }

    /// <summary>
    /// Called on time change. Handles festival reset when time passes the festival window.
    /// </summary>
    public void UpdateFestivalStatus()
    {
        // Reset once we hold festival state and time has passed the festival window.
        // Not gated on connected players: a festival can end with nobody present
        // (the no-players leave path), and that stale state must still clear so it
        // doesn't poison the next festival. The _activeFestival guard keeps it one-shot.
        if (_activeFestival == null)
        {
            return;
        }

        var currentTime = Game1.timeOfDay;

        foreach (var spec in _festivals)
        {
            if (spec.IsToday() && currentTime >= spec.ResetCutoff)
            {
                ResetFestivalState();
                if (spec.RestoreHudOnReset)
                {
                    Game1.displayHUD = true;
                }
                ClearActiveFestival();
                break;
            }
        }
    }

    private void ResetFestivalState()
    {
        _timeoutStartTime = null;
        _timeoutWarned = false;
        _warpingToFestival = false;
        _startedFestivalEnd = false;
    }

    private void ClearActiveFestival()
    {
        _activeFestival = null;
        _runStartTime = null;
        _announced = false;
        _started = false;
        _eventCommandUsed = false;
    }

    /// <summary>
    /// Called every tick. Warps the host to the festival when other players are ready,
    /// mirroring the game's DedicatedServer (monitor ready state, then warp directly).
    /// </summary>
    public void HandleFestivalStart()
    {
        if (
            Game1.whereIsTodaysFest != null
            && (DateTime.UtcNow - _lastStartLogTime).TotalSeconds >= 1.0
        )
        {
            _lastStartLogTime = DateTime.UtcNow;
            var numberReady = Game1.netReady.GetNumberReady("festivalStart");
            var numberRequired = Game1.netReady.GetNumberRequired("festivalStart");
            _monitor.Log(
                $"[Festival] online={CountOnlineOtherPlayers()}, otherFarmers={Game1.otherFarmers.Count}, isFestival={Game1.CurrentEvent?.isFestival}, warping={_warpingToFestival}, ready={numberReady}/{numberRequired}, CheckOthersReady={CheckOthersReady("festivalStart")}",
                LogLevel.Trace
            );
        }

        // Don't warp the host in if nobody (online, non-disconnecting) is there to attend.
        // Consistent with the no-players end check; CurrentEvent is null here so otherFarmers
        // would be live, but use the same online count so a player disconnecting mid-warp-in is
        // seen immediately rather than one removeDisconnectedFarmers pass later.
        if (CountOnlineOtherPlayers() == 0)
        {
            return;
        }

        if (Game1.CurrentEvent?.isFestival == true || _warpingToFestival)
        {
            return;
        }

        if (Game1.whereIsTodaysFest != null && CheckOthersReady("festivalStart"))
        {
            _monitor.Log(
                "Other players ready for festival, warping host to festival location",
                LogLevel.Info
            );
            _warpingToFestival = true;

            // Mark host as ready so players' ReadyCheckDialog completes
            Game1.netReady.SetLocalReady("festivalStart", true);

            var locationRequest = Game1.getLocationRequest(Game1.whereIsTodaysFest);
            locationRequest.OnWarp += delegate
            {
                _warpingToFestival = false;
                BeginActiveFestival();
            };

            int x = -1;
            int y = -1;
            Utility.getDefaultWarpLocation(Game1.whereIsTodaysFest, ref x, ref y);
            Game1.warpFarmer(locationRequest, x, y, 2);
        }
    }

    /// <summary>
    /// Mark today's festival active and reset its main-event countdown state.
    /// Called once the host finishes warping into the festival.
    /// </summary>
    private void BeginActiveFestival()
    {
        // Clear the end/timeout latches too, not just countdown state. Normally
        // UpdateFestivalStatus's reset-cutoff path clears them when the prior festival's day
        // ends, but a date jump (/test/set_date) lands at 06:00 — below any ResetCutoff — so a
        // prior festival's _startedFestivalEnd can survive into the next one and make
        // HandleFestivalLeave early-return, stranding it on the wall-clock timeout. Resetting
        // here makes each festival start clean regardless of how the day changed.
        ResetFestivalState();
        _activeFestival = _festivals.FirstOrDefault(spec => spec.IsToday());
        _runStartTime = null;
        _announced = false;
        _started = false;
        _eventCommandUsed = false;
    }

    /// <summary>
    /// Get the festival host NPC for the current event.
    /// </summary>
    private NPC GetFestivalHost()
    {
        if (Game1.CurrentEvent == null)
        {
            return null;
        }
        return _helper.Reflection.GetField<NPC>(Game1.CurrentEvent, "festivalHost").GetValue();
    }

    /// <summary>
    /// Check if other players (excluding host) are ready for a given ready check.
    /// Same logic as DedicatedServer.CheckOthersReady.
    /// </summary>
    private bool CheckOthersReady(string readyCheck)
    {
        int numberReady = Game1.netReady.GetNumberReady(readyCheck);
        if (numberReady <= 0)
        {
            return false;
        }

        // If not fully ready, check if everyone except host is ready
        if (!Game1.netReady.IsReady(readyCheck))
        {
            return numberReady >= Game1.netReady.GetNumberRequired(readyCheck) - 1;
        }

        return false;
    }

    /// <summary>
    /// Count online non-host players, excluding any mid-disconnect, mirroring how the game's
    /// <c>DedicatedServer</c> builds its <c>onlineIds</c> set (DedicatedServer.cs:286-293).
    ///
    /// <para>
    /// Do NOT use <c>Game1.otherFarmers.Count</c> for "is the festival empty?". A player who
    /// disconnects mid-festival is only <i>marked</i> in <c>disconnectingFarmers</c>; the actual
    /// <c>Game1.otherFarmers.Remove</c> runs in <c>Multiplayer.removeDisconnectedFarmers</c>, which
    /// is gated on <c>Game1.CurrentEvent == null</c> (Multiplayer.cs:1821). So during a festival
    /// the count never drops — and gating the no-players force-end on it would hang the festival
    /// forever (event up → not removed → count stays 1 → never ends → event stays up).
    /// <c>isDisconnecting</c> is the signal the engine itself uses to see through that.
    /// </para>
    /// </summary>
    private static int CountOnlineOtherPlayers()
    {
        return Game1
            .getOnlineFarmers()
            .Count(f =>
                f.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID
                && !Game1.Multiplayer.isDisconnecting(f)
            );
    }

    /// <summary>
    /// Called once per second. Drives the active festival's main-event countdown
    /// (or, for leave-only festivals, just the timeout backstop).
    /// </summary>
    public void HandleFestivalEvents()
    {
        if (Game1.CurrentEvent?.isFestival != true || _activeFestival == null)
        {
            return;
        }

        if (_activeFestival.HasMainEvent)
        {
            RunMainEventCountdown(_activeFestival);
        }
        else
        {
            RunLeaveOnly(_activeFestival);
        }
    }

    /// <summary>
    /// Announce a countdown, start the host-triggered main event when it elapses,
    /// then (for the Fair) auto-end. !event short-circuits the countdown.
    /// </summary>
    private void RunMainEventCountdown(FestivalSpec spec)
    {
        if (_eventCommandUsed)
        {
            _runStartTime = DateTime.UtcNow.AddSeconds(-spec.CountdownSeconds());
            if (!_announced)
            {
                spec.OnAnnounce?.Invoke();
                _announced = true;
            }
            _eventCommandUsed = false;
        }

        if (!_runStartTime.HasValue)
        {
            _runStartTime = DateTime.UtcNow;
        }

        double elapsed = ElapsedSeconds(_runStartTime);
        double countdownSeconds = spec.CountdownSeconds();

        if (spec.TimeoutStart == TimeoutStart.OnEntry)
        {
            RunFestivalTimeout(spec);
        }

        if (!_announced)
        {
            float countdownMinutes = spec.CountdownSeconds() / 60f;
            _helper.SendPublicMessage(string.Format(spec.AnnounceText, countdownMinutes));
            _helper.SendPublicMessage(StartNowText);
            spec.OnAnnounce?.Invoke();
            _announced = true;
        }

        if (!_started && elapsed >= countdownSeconds)
        {
            var festivalHost = GetFestivalHost();
            if (festivalHost != null)
            {
                Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
            }
            _started = true;
        }

        // Small slack so the main event has been kicked off on an earlier tick before we start
        // its timeout / auto-end.
        if (elapsed >= countdownSeconds + 0.1)
        {
            if (spec.TimeoutStart == TimeoutStart.AfterMainEvent)
            {
                RunFestivalTimeout(spec);
            }

            if (spec.AutoEndAfterCountdown && !_startedFestivalEnd)
            {
                EndFestival(spec.EndLogText, force: false);
            }
        }
    }

    /// <summary>
    /// Festivals with no host-triggered main event. Like the game's DedicatedServer,
    /// the host only leaves once other players are ready (handled in
    /// <see cref="HandleFestivalLeave"/>); here we just run the wall-clock backstop
    /// so an empty or AFK festival still ends.
    /// </summary>
    private void RunLeaveOnly(FestivalSpec spec)
    {
        RunFestivalTimeout(spec);
    }

    /// <summary>
    /// Wall-clock backstop: warn at <see cref="AlwaysOnConfig.FestivalExitWarningSeconds"/>
    /// before the timeout, then end the festival and warp everyone home so a stalled
    /// festival can't hang.
    /// </summary>
    private void RunFestivalTimeout(FestivalSpec spec)
    {
        if (!_timeoutStartTime.HasValue)
        {
            _timeoutStartTime = DateTime.UtcNow;
        }

        double resetElapsed = ElapsedSeconds(_timeoutStartTime);
        double timeoutSeconds = spec.TimeoutSeconds();

        if (!_timeoutWarned && resetElapsed >= timeoutSeconds - Config.FestivalExitWarningSeconds)
        {
            _helper.SendPublicMessage(
                $"{Config.FestivalExitWarningSeconds / 60f:0.#} minutes to the exit or everyone will be sent home."
            );
            _timeoutWarned = true;
        }

        // !_startedFestivalEnd so this and HandleFestivalLeave's no-players path don't
        // both fire on the same tick.
        if (resetElapsed >= timeoutSeconds && !_startedFestivalEnd)
        {
            EndFestival(
                "Festival timeout reached, ending festival and sending players home",
                force: true
            );
        }
    }

    /// <summary>
    /// Called every tick. Ends the festival like the game's DedicatedServer: once no online
    /// players remain, or once the remaining players are ready to leave. Both paths go through
    /// <c>TryStartEndFestivalDialogue</c> (the host's own festivalEnd ready then satisfies the
    /// check, since NumberRequired excludes disconnecting players) — matching DedicatedServer.cs:306.
    /// </summary>
    public void HandleFestivalLeave()
    {
        if (Game1.CurrentEvent?.isFestival != true || _startedFestivalEnd)
        {
            return;
        }

        // No online players left at the festival: end it so the host isn't stranded. Mirrors
        // DedicatedServer.Tick's onlineIds.Count == 0 branch (DedicatedServer.cs:294-308), which
        // ends via TryStartEndFestivalDialogue (force: false) — with no one else online the host's
        // own festivalEnd ready satisfies the check and the festival ends gracefully. Counts
        // online non-disconnecting players, NOT otherFarmers.Count (see CountOnlineOtherPlayers).
        if (CountOnlineOtherPlayers() == 0)
        {
            EndFestival(
                "No players remaining at festival, ending and sending host home",
                force: false
            );
            return;
        }

        if ((DateTime.UtcNow - _lastLeaveLogTime).TotalSeconds >= 1.0)
        {
            _lastLeaveLogTime = DateTime.UtcNow;
            var endReady = Game1.netReady.GetNumberReady("festivalEnd");
            var endRequired = Game1.netReady.GetNumberRequired("festivalEnd");
            _monitor.Log(
                $"[FestivalLeave] online={CountOnlineOtherPlayers()}, ready={endReady}/{endRequired}, CheckOthersReady={CheckOthersReady("festivalEnd")}",
                LogLevel.Trace
            );
        }

        if (CheckOthersReady("festivalEnd"))
        {
            EndFestival(
                "Other players ready to leave festival, triggering end dialogue",
                force: false
            );
        }
    }

    /// <summary>
    /// End the active festival. <paramref name="force"/> = true calls <c>forceEndFestival</c>
    /// (ends immediately, warps everyone home, no dialog) for the wall-clock timeout backstop,
    /// where players may still be online but stalled. false uses <c>TryStartEndFestivalDialogue</c>
    /// (the ReadyCheckDialog flow) for the graceful paths — players ready to leave, or no online
    /// players remaining — matching the game's DedicatedServer. The <c>?.</c> guards against
    /// <c>CurrentEvent</c> clearing before this runs.
    /// </summary>
    private void EndFestival(string reason, bool force)
    {
        _monitor.Log(reason, LogLevel.Info);
        if (force)
        {
            Game1.CurrentEvent?.forceEndFestival(Game1.player);
        }
        else
        {
            Game1.CurrentEvent?.TryStartEndFestivalDialogue(Game1.player);
        }
        _startedFestivalEnd = true;
    }

    /// <summary>
    /// !event chat command: force-start today's main-event countdown.
    /// </summary>
    private void StartEventCommand(string[] args, ReceivedMessage msg)
    {
        if (Game1.CurrentEvent is not { isFestival: true })
        {
            _helper.SendPublicMessage("Must be at festival to start the event.");
            return;
        }

        var spec = _festivals.FirstOrDefault(f => f.IsToday());
        if (spec == null)
        {
            return;
        }

        if (!spec.HasMainEvent)
        {
            _helper.SendPublicMessage("This festival doesn't have a host-started event.");
            return;
        }

        _eventCommandUsed = true;
        _activeFestival = spec;
    }
}
