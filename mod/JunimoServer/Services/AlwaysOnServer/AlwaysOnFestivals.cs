using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.Linq;
using SObject = StardewValley.Object;

namespace JunimoServer.Services.AlwaysOn
{
    public class AlwaysOnServerFestivals
    {
        private const string StartNowText = "Type !event to start now";

        // When does the offline-timeout clock start
        private enum TimeoutStart
        {
            OnEntry,
            AfterMainEvent,
        }

        private class FestivalSpec
        {
            public Func<bool> IsToday;

            public int ResetCutoff;
            public bool RestoreHudOnReset;

            public bool HasMainEvent;

            public Func<int> CountdownSeconds;
            public string AnnounceText;
            public Action OnAnnounce;  // optional action (add iridium star in Luau)
            public bool AutoEndAfterCountdown;

            public Func<double> TimeoutSeconds;
            public TimeoutStart TimeoutStart;
            public string EndLogText;
        }

        private readonly List<FestivalSpec> _festivals;

        // Current festival & run state
        private FestivalSpec _activeFestival;
        private DateTime? _runStartTime;
        private bool _announced;
        private bool _started;

        private bool eventCommandUsed;

        // Variables for timeout reset
        private DateTime? _timeoutStartTime;
        private bool _timeoutWarned;

        // Track if we're currently warping to festival to avoid repeated warps
        private bool _warpingToFestival;

        // Track if we've started the festival end process
        private bool _startedFestivalEnd;

        // Log throttle
        private DateTime _lastLogTime = DateTime.MinValue;

        protected readonly IModHelper _helper;
        protected readonly IMonitor _monitor;
        protected readonly AlwaysOnConfig Config;

        public AlwaysOnServerFestivals(IModHelper helper, IMonitor monitor, ChatCommandsService chatCommandService, AlwaysOnConfig config)
        {
            _helper = helper;
            _monitor = monitor;
            Config = config;

            _festivals = BuildFestivalSpecs();

            chatCommandService.RegisterCommand("event", "Tries to start the current festival's event.", StartEventCommand);
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
                    TimeoutSeconds = () => TicksToSeconds(Config.EggFestivalTimeOut + 180),
                },
                new FestivalSpec
                {
                    IsToday = SDateHelper.IsFlowerDanceToday,
                    ResetCutoff = 1410,
                    HasMainEvent = true,
                    CountdownSeconds = () => Config.FlowerDanceCountdownSeconds,
                    AnnounceText = "The Flower Dance will begin in {0:0.#} minutes.",
                    TimeoutStart = TimeoutStart.AfterMainEvent,
                    TimeoutSeconds = () => TicksToSeconds(Config.FlowerDanceTimeOut + 90),
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
                    TimeoutSeconds = () => TicksToSeconds(Config.LuauTimeOut + 80),
                },
                new FestivalSpec
                {
                    IsToday = SDateHelper.IsDanceOfJelliesToday,
                    ResetCutoff = 2410,
                    HasMainEvent = true,
                    CountdownSeconds = () => Config.JellyDanceCountdownSeconds,
                    AnnounceText = "The Dance of the Moonlight Jellies will begin in {0:0.#} minutes.",
                    TimeoutStart = TimeoutStart.AfterMainEvent,
                    TimeoutSeconds = () => TicksToSeconds(Config.DanceOfJelliesTimeOut + 180),
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
                    TimeoutSeconds = () => TicksToSeconds(Config.FairTimeOut),
                    EndLogText = "Grange display finished, triggering festival end",
                },
                new FestivalSpec
                {
                    IsToday = SDateHelper.IsSpiritsEveToday,
                    ResetCutoff = 2400,
                    RestoreHudOnReset = true,
                    HasMainEvent = false,
                    TimeoutStart = TimeoutStart.OnEntry,
                    TimeoutSeconds = () => TicksToSeconds(Config.SpiritsEveTimeOut),
                    EndLogText = "Spirit's Eve timeout, triggering festival end",
                },
                new FestivalSpec
                {
                    IsToday = SDateHelper.IsFestivalOfIceToday,
                    ResetCutoff = 1410,
                    HasMainEvent = true,
                    CountdownSeconds = () => Config.IceFishingCountdownSeconds,
                    AnnounceText = "The Ice Fishing Contest will begin in {0:0.#} minutes.",
                    TimeoutStart = TimeoutStart.AfterMainEvent,
                    TimeoutSeconds = () => TicksToSeconds(Config.FestivalOfIceTimeOut + 180),
                },
                new FestivalSpec
                {
                    IsToday = SDateHelper.IsFeastOfWinterStarToday,
                    ResetCutoff = 1410,
                    HasMainEvent = false,
                    TimeoutStart = TimeoutStart.OnEntry,
                    TimeoutSeconds = () => TicksToSeconds(Config.WinterStarTimeOut),
                    EndLogText = "Winter Feast timeout, triggering festival end",
                },
            };
        }

        /// <summary>
        /// Convert a config value (in ticks at 60 TPS) to seconds.
        /// </summary>
        private static double TicksToSeconds(int ticks) => ticks / 60.0;

        /// <summary>
        /// Get elapsed seconds since a start time, or 0 if not started.
        /// </summary>
        private static double ElapsedSeconds(DateTime? startTime)
        {
            if (!startTime.HasValue) return 0;
            return (DateTime.UtcNow - startTime.Value).TotalSeconds;
        }

        private void AddIridiumStarfruitToSoup()
        {
            var item = new SObject("268", 1, false, -1, 3);
            _helper.Reflection.GetMethod(Game1.CurrentEvent, "addItemToLuauSoup").Invoke(item, Game1.player);
        }

        /// <summary>
        /// Called on time change. Handles festival reset when time passes the festival window.
        /// </summary>
        public void UpdateFestivalStatus()
        {
            if (Game1.otherFarmers.Count == 0)
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
            Game1.options.setServerMode("online");
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
            eventCommandUsed = false;
        }

        /// <summary>
        /// Called every tick. Handles warping the host to the festival when other players are ready.
        /// Uses the same approach as the game's DedicatedServer - monitor ready state and warp directly.
        /// </summary>
        public void HandleFestivalStart()
        {
            // Debug: log once per second on festival days
            if (Game1.whereIsTodaysFest != null && (DateTime.UtcNow - _lastLogTime).TotalSeconds >= 1.0)
            {
                _lastLogTime = DateTime.UtcNow;
                var numberReady = Game1.netReady.GetNumberReady("festivalStart");
                var numberRequired = Game1.netReady.GetNumberRequired("festivalStart");
                _monitor.Log($"[Festival] otherFarmers={Game1.otherFarmers.Count}, isFestival={Game1.CurrentEvent?.isFestival}, warping={_warpingToFestival}, ready={numberReady}/{numberRequired}, CheckOthersReady={CheckOthersReady("festivalStart")}", LogLevel.Info);
            }

            if (Game1.otherFarmers.Count == 0)
            {
                return;
            }

            // Already at festival or already warping
            if (Game1.CurrentEvent?.isFestival == true || _warpingToFestival)
            {
                return;
            }

            // Check if there's a festival today and others are ready
            if (Game1.whereIsTodaysFest != null && CheckOthersReady("festivalStart"))
            {
                _monitor.Log("Other players ready for festival, warping host to festival location", LogLevel.Info);
                _warpingToFestival = true;

                // Mark host as ready so players' ReadyCheckDialog completes
                Game1.netReady.SetLocalReady("festivalStart", true);

                var locationRequest = Game1.getLocationRequest(Game1.whereIsTodaysFest);
                locationRequest.OnWarp += delegate
                {
                    _warpingToFestival = false;
                    SetFestivalAvailableFlag();
                };

                int x = -1;
                int y = -1;
                Utility.getDefaultWarpLocation(Game1.whereIsTodaysFest, ref x, ref y);
                Game1.warpFarmer(locationRequest, x, y, 2);
            }
        }

        /// <summary>
        /// Set the appropriate festival available flag based on today's festival.
        /// </summary>
        private void SetFestivalAvailableFlag()
        {
            _activeFestival = _festivals.FirstOrDefault(spec => spec.IsToday());
            _runStartTime = null;
            _announced = false;
            _started = false;
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

        public void HandleFestivalEvents()
        {
            if (Game1.CurrentEvent == null || !Game1.CurrentEvent.isFestival)
            {
                return;
            }

            if (_activeFestival == null)
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
        /// Festivals with a host-triggered main event
        /// </summary>
        private void RunMainEventCountdown(FestivalSpec spec)
        {
            if (eventCommandUsed)
            {
                _runStartTime = DateTime.UtcNow.AddSeconds(-spec.CountdownSeconds());
                if (!_announced)
                {
                    spec.OnAnnounce?.Invoke();
                    _announced = true;
                }
                eventCommandUsed = false;
            }

            if (!_runStartTime.HasValue)
            {
                _runStartTime = DateTime.UtcNow;
            }

            double elapsed = ElapsedSeconds(_runStartTime);
            double countdownSeconds = spec.CountdownSeconds();

            if (spec.TimeoutStart == TimeoutStart.OnEntry)
            {
                RunOfflineTimeout(spec);
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

            if (elapsed >= countdownSeconds + 5.0 / 60.0)
            {
                if (spec.TimeoutStart == TimeoutStart.AfterMainEvent)
                {
                    RunOfflineTimeout(spec);
                }

                if (spec.AutoEndAfterCountdown && !_startedFestivalEnd)
                {
                    _monitor.Log(spec.EndLogText);
                    Game1.CurrentEvent.TryStartEndFestivalDialogue(Game1.player);
                    _startedFestivalEnd = true;
                }
            }
        }

        /// <summary>
        /// Festivals with no host-triggered main event
        /// </summary>
        private void RunLeaveOnly(FestivalSpec spec)
        {
            if (!_runStartTime.HasValue)
            {
                _runStartTime = DateTime.UtcNow;
            }

            RunOfflineTimeout(spec);

            if (ElapsedSeconds(_runStartTime) >= TicksToSeconds(10) && !_startedFestivalEnd)
            {
                _monitor.Log(spec.EndLogText);
                Game1.CurrentEvent.TryStartEndFestivalDialogue(Game1.player);
                _startedFestivalEnd = true;
            }
        }

        private void RunOfflineTimeout(FestivalSpec spec)
        {
            if (!_timeoutStartTime.HasValue)
            {
                _timeoutStartTime = DateTime.UtcNow;
            }

            double resetElapsed = ElapsedSeconds(_timeoutStartTime);
            double timeoutSeconds = spec.TimeoutSeconds();

            if (!_timeoutWarned && resetElapsed >= timeoutSeconds - Config.FestivalExitWarningSeconds)
            {
                _helper.SendPublicMessage($"{Config.FestivalExitWarningSeconds / 60f:0.#} minutes to the exit or");
                _helper.SendPublicMessage("everyone will be kicked.");
                _timeoutWarned = true;
            }

            if (resetElapsed >= timeoutSeconds)
            {
                Game1.options.setServerMode("offline");
            }
        }

        /// <summary>
        /// Called every tick. Handles leaving the festival when other players are ready.
        /// Uses TryStartEndFestivalDialogue like the game's DedicatedServer does.
        /// </summary>
        public void HandleFestivalLeave()
        {
            if (Game1.otherFarmers.Count == 0)
            {
                return;
            }

            // Only handle if we're at a festival and haven't started ending yet
            if (Game1.CurrentEvent?.isFestival != true || _startedFestivalEnd)
            {
                return;
            }

            // Debug: log festivalEnd ready state once per second
            var endReady = Game1.netReady.GetNumberReady("festivalEnd");
            var endRequired = Game1.netReady.GetNumberRequired("festivalEnd");
            if ((DateTime.UtcNow - _lastLogTime).TotalSeconds >= 1.0)
            {
                _lastLogTime = DateTime.UtcNow;
                _monitor.Log($"[FestivalLeave] ready={endReady}/{endRequired}, CheckOthersReady={CheckOthersReady("festivalEnd")}", LogLevel.Info);
            }

            if (CheckOthersReady("festivalEnd"))
            {
                _monitor.Log("Other players ready to leave festival, triggering end dialogue", LogLevel.Info);
                Game1.CurrentEvent.TryStartEndFestivalDialogue(Game1.player);
                _startedFestivalEnd = true;
            }
        }

        /// <summary>
        /// Starts the current days event if there is any.
        /// </summary>
        /// <param name="args"></param>
        /// <param name="msg"></param>
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

            eventCommandUsed = true;
            _activeFestival = spec;
        }
    }
}
