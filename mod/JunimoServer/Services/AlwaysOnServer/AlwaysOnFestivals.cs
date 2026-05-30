using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using System;
using SObject = StardewValley.Object;

namespace JunimoServer.Services.AlwaysOn
{
    public class AlwaysOnServerFestivals
    {
        private const string StartNowText = "Type !event to start now";

        private bool eventCommandUsed;

        private bool eggHuntAvailable;
        private DateTime? eggHuntStartTime;
        private bool eggHuntAnnounced;
        private bool eggHuntStarted;

        private bool flowerDanceAvailable;
        private DateTime? flowerDanceStartTime;
        private bool flowerDanceAnnounced;
        private bool flowerDanceStarted;

        private bool luauSoupAvailable;
        private DateTime? luauSoupStartTime;
        private bool luauSoupAnnounced;
        private bool luauSoupStarted;

        private bool jellyDanceAvailable;
        private DateTime? jellyDanceStartTime;
        private bool jellyDanceAnnounced;
        private bool jellyDanceStarted;

        private bool grangeDisplayAvailable;
        private DateTime? grangeDisplayStartTime;
        private bool grangeDisplayAnnounced;
        private bool grangeDisplayStarted;

        private bool goldenPumpkinAvailable;
        private DateTime? goldenPumpkinStartTime;

        private bool iceFishingAvailable;
        private DateTime? iceFishingStartTime;
        private bool iceFishingAnnounced;
        private bool iceFishingStarted;

        private bool winterFeastAvailable;
        private DateTime? winterFeastStartTime;

        // Variables for timeout reset
        private DateTime? resetStartTime;
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

            chatCommandService.RegisterCommand("event", "Tries to start the current festival's event.", StartEventCommand);
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

        /// <summary>
        /// Called on time change. Handles festival reset when time passes the festival window.
        /// </summary>
        public void UpdateFestivalStatus()
        {
            if (Game1.otherFarmers.Count == 0)
            {
                return;
            }

            // Reset festival state when festival time window ends
            var currentTime = Game1.timeOfDay;

            if (SDateHelper.IsEggFestivalToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                eggHuntAvailable = false;
                eggHuntStartTime = null;
                eggHuntAnnounced = false;
                eggHuntStarted = false;
            }
            else if (SDateHelper.IsFlowerDanceToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                flowerDanceAvailable = false;
                flowerDanceStartTime = null;
                flowerDanceAnnounced = false;
                flowerDanceStarted = false;
            }
            else if (SDateHelper.IsLuauToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                luauSoupAvailable = false;
                luauSoupStartTime = null;
                luauSoupAnnounced = false;
                luauSoupStarted = false;
            }
            else if (SDateHelper.IsDanceOfJelliesToday() && currentTime >= 2410)
            {
                ResetFestivalState();
                jellyDanceAvailable = false;
                jellyDanceStartTime = null;
                jellyDanceAnnounced = false;
                jellyDanceStarted = false;
            }
            else if (SDateHelper.IsStardewValleyFairToday() && currentTime >= 1510)
            {
                ResetFestivalState();
                Game1.displayHUD = true;
                grangeDisplayAvailable = false;
                grangeDisplayStartTime = null;
                grangeDisplayAnnounced = false;
                grangeDisplayStarted = false;
            }
            else if (SDateHelper.IsSpiritsEveToday() && currentTime >= 2400)
            {
                ResetFestivalState();
                Game1.displayHUD = true;
                goldenPumpkinAvailable = false;
                goldenPumpkinStartTime = null;
            }
            else if (SDateHelper.IsFestivalOfIceToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                iceFishingAvailable = false;
                iceFishingStartTime = null;
                iceFishingAnnounced = false;
                iceFishingStarted = false;
            }
            else if (SDateHelper.IsFeastOfWinterStarToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                winterFeastAvailable = false;
                winterFeastStartTime = null;
            }
        }

        private void ResetFestivalState()
        {
            Game1.options.setServerMode("online");
            resetStartTime = null;
            _timeoutWarned = false;
            _warpingToFestival = false;
            _startedFestivalEnd = false;
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
            if (SDateHelper.IsEggFestivalToday()) eggHuntAvailable = true;
            else if (SDateHelper.IsFlowerDanceToday()) flowerDanceAvailable = true;
            else if (SDateHelper.IsLuauToday()) luauSoupAvailable = true;
            else if (SDateHelper.IsDanceOfJelliesToday()) jellyDanceAvailable = true;
            else if (SDateHelper.IsStardewValleyFairToday()) grangeDisplayAvailable = true;
            else if (SDateHelper.IsSpiritsEveToday()) goldenPumpkinAvailable = true;
            else if (SDateHelper.IsFestivalOfIceToday()) iceFishingAvailable = true;
            else if (SDateHelper.IsFeastOfWinterStarToday()) winterFeastAvailable = true;
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

            // Egg-hunt event
            if (eggHuntAvailable)
            {
                if (eventCommandUsed)
                {
                    eggHuntStartTime = DateTime.UtcNow.AddSeconds(-TicksToSeconds(Config.EggHuntCountDownConfig));
                    eggHuntAnnounced = true;
                    eventCommandUsed = false;
                }

                if (!eggHuntStartTime.HasValue)
                {
                    eggHuntStartTime = DateTime.UtcNow;
                }

                double elapsed = ElapsedSeconds(eggHuntStartTime);
                double countdownSeconds = TicksToSeconds(Config.EggHuntCountDownConfig);

                if (!eggHuntAnnounced)
                {
                    float chatEgg = Config.EggHuntCountDownConfig / 60f;
                    _helper.SendPublicMessage($"The Egg Hunt will begin in {chatEgg:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                    eggHuntAnnounced = true;
                }

                if (!eggHuntStarted && elapsed >= countdownSeconds)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                    eggHuntStarted = true;
                }

                if (elapsed >= countdownSeconds + 5.0 / 60.0)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        //_helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    //festival timeout
                    if (!resetStartTime.HasValue)
                    {
                        resetStartTime = DateTime.UtcNow;
                    }
                    if (ElapsedSeconds(resetStartTime) >= TicksToSeconds(Config.EggFestivalTimeOut + 180))
                    {
                        Game1.options.setServerMode("offline");
                    }
                    ///////////////////////////////////////////////
                }
            }

            // Flower dance event
            else if (flowerDanceAvailable)
            {
                if (eventCommandUsed)
                {
                    flowerDanceStartTime = DateTime.UtcNow.AddSeconds(-TicksToSeconds(Config.FlowerDanceCountDownConfig));
                    flowerDanceAnnounced = true;
                    eventCommandUsed = false;
                }

                if (!flowerDanceStartTime.HasValue)
                {
                    flowerDanceStartTime = DateTime.UtcNow;
                }

                double elapsed = ElapsedSeconds(flowerDanceStartTime);
                double countdownSeconds = TicksToSeconds(Config.FlowerDanceCountDownConfig);

                if (!flowerDanceAnnounced)
                {
                    float chatFlower = Config.FlowerDanceCountDownConfig / 60f;
                    _helper.SendPublicMessage($"The Flower Dance will begin in {chatFlower:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                    flowerDanceAnnounced = true;
                }

                if (!flowerDanceStarted && elapsed >= countdownSeconds)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                    flowerDanceStarted = true;
                }

                if (elapsed >= countdownSeconds + 5.0 / 60.0)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        // _helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    //festival timeout
                    if (!resetStartTime.HasValue)
                    {
                        resetStartTime = DateTime.UtcNow;
                    }
                    if (ElapsedSeconds(resetStartTime) >= TicksToSeconds(Config.FlowerDanceTimeOut + 90))
                    {
                        Game1.options.setServerMode("offline");
                    }
                    ///////////////////////////////////////////////
                }
            }

            // LuauSoup event
            else if (luauSoupAvailable)
            {
                if (eventCommandUsed)
                {
                    luauSoupStartTime = DateTime.UtcNow.AddSeconds(-TicksToSeconds(Config.LuauSoupCountDownConfig));
                    // Add iridium starfruit to soup
                    var item = new SObject("268", 1, false, -1, 3);
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "addItemToLuauSoup").Invoke(item, Game1.player);
                    luauSoupAnnounced = true;
                    eventCommandUsed = false;
                }

                if (!luauSoupStartTime.HasValue)
                {
                    luauSoupStartTime = DateTime.UtcNow;
                }

                double elapsed = ElapsedSeconds(luauSoupStartTime);
                double countdownSeconds = TicksToSeconds(Config.LuauSoupCountDownConfig);

                if (!luauSoupAnnounced)
                {
                    float chatSoup = Config.LuauSoupCountDownConfig / 60f;
                    _helper.SendPublicMessage($"The Soup Tasting will begin in {chatSoup:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);

                    // Add iridium starfruit to soup
                    var item = new SObject("268", 1, false, -1, 3);
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "addItemToLuauSoup").Invoke(item, Game1.player);
                    luauSoupAnnounced = true;
                }

                if (!luauSoupStarted && elapsed >= countdownSeconds)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                    luauSoupStarted = true;
                }

                if (elapsed >= countdownSeconds + 5.0 / 60.0)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        //_helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    // Festival timeout
                    if (!resetStartTime.HasValue)
                    {
                        resetStartTime = DateTime.UtcNow;
                    }
                    if (ElapsedSeconds(resetStartTime) >= TicksToSeconds(Config.LuauTimeOut + 80))
                    {
                        Game1.options.setServerMode("offline");
                    }
                    ///////////////////////////////////////////////
                }
            }

            // Dance of the Moonlight Jellies event
            else if (jellyDanceAvailable)
            {
                if (eventCommandUsed)
                {
                    jellyDanceStartTime = DateTime.UtcNow.AddSeconds(-TicksToSeconds(Config.JellyDanceCountDownConfig));
                    jellyDanceAnnounced = true;
                    eventCommandUsed = false;
                }

                if (!jellyDanceStartTime.HasValue)
                {
                    jellyDanceStartTime = DateTime.UtcNow;
                }

                double elapsed = ElapsedSeconds(jellyDanceStartTime);
                double countdownSeconds = TicksToSeconds(Config.JellyDanceCountDownConfig);

                if (!jellyDanceAnnounced)
                {
                    float chatJelly = Config.JellyDanceCountDownConfig / 60f;
                    _helper.SendPublicMessage($"The Dance of the Moonlight Jellies will begin in {chatJelly:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                    jellyDanceAnnounced = true;
                }

                if (!jellyDanceStarted && elapsed >= countdownSeconds)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                    jellyDanceStarted = true;
                }

                if (elapsed >= countdownSeconds + 5.0 / 60.0)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        // _helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    // Festival timeout
                    if (!resetStartTime.HasValue)
                    {
                        resetStartTime = DateTime.UtcNow;
                    }
                    if (ElapsedSeconds(resetStartTime) >= TicksToSeconds(Config.DanceOfJelliesTimeOut + 180))
                    {
                        Game1.options.setServerMode("offline");
                    }
                    ///////////////////////////////////////////////
                }
            }

            // Grange Display event
            else if (grangeDisplayAvailable)
            {
                if (eventCommandUsed)
                {
                    grangeDisplayStartTime = DateTime.UtcNow.AddSeconds(-TicksToSeconds(Config.GrangeDisplayCountDownConfig));
                    grangeDisplayAnnounced = true;
                    eventCommandUsed = false;
                }

                if (!grangeDisplayStartTime.HasValue)
                {
                    grangeDisplayStartTime = DateTime.UtcNow;
                }

                double elapsed = ElapsedSeconds(grangeDisplayStartTime);
                double countdownSeconds = TicksToSeconds(Config.GrangeDisplayCountDownConfig);

                // Festival timeout (runs from the start for this festival)
                if (!resetStartTime.HasValue)
                {
                    resetStartTime = DateTime.UtcNow;
                }

                double resetElapsed = ElapsedSeconds(resetStartTime);
                double fairTimeoutSeconds = TicksToSeconds(Config.FairTimeOut);

                if (!_timeoutWarned && resetElapsed >= fairTimeoutSeconds - TicksToSeconds(120))
                {
                    _helper.SendPublicMessage("2 minutes to the exit or");
                    _helper.SendPublicMessage("everyone will be kicked.");
                    _timeoutWarned = true;
                }

                if (resetElapsed >= fairTimeoutSeconds)
                {
                    Game1.options.setServerMode("offline");
                }

                ///////////////////////////////////////////////
                if (!grangeDisplayAnnounced)
                {
                    float chatGrange = Config.GrangeDisplayCountDownConfig / 60f;
                    _helper.SendPublicMessage($"The Grange Judging will begin in {chatGrange:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                    grangeDisplayAnnounced = true;
                }

                if (!grangeDisplayStarted && elapsed >= countdownSeconds)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                    grangeDisplayStarted = true;
                }

                if (elapsed >= countdownSeconds + 5.0 / 60.0 && !_startedFestivalEnd)
                {
                    _monitor.Log("Grange display finished, triggering festival end");
                    Game1.CurrentEvent.TryStartEndFestivalDialogue(Game1.player);
                    _startedFestivalEnd = true;
                }
            }

            // Golden pumpkin maze event
            else if (goldenPumpkinAvailable)
            {
                if (!goldenPumpkinStartTime.HasValue)
                {
                    goldenPumpkinStartTime = DateTime.UtcNow;
                }

                if (!resetStartTime.HasValue)
                {
                    resetStartTime = DateTime.UtcNow;
                }

                double resetElapsed = ElapsedSeconds(resetStartTime);
                double spiritsTimeoutSeconds = TicksToSeconds(Config.SpiritsEveTimeOut);

                // Festival timeout code
                if (!_timeoutWarned && resetElapsed >= spiritsTimeoutSeconds - TicksToSeconds(120))
                {
                    _helper.SendPublicMessage("2 minutes to the exit or");
                    _helper.SendPublicMessage("everyone will be kicked.");
                    _timeoutWarned = true;
                }

                if (resetElapsed >= spiritsTimeoutSeconds)
                {
                    Game1.options.setServerMode("offline");
                }

                ///////////////////////////////////////////////
                if (ElapsedSeconds(goldenPumpkinStartTime) >= TicksToSeconds(10) && !_startedFestivalEnd)
                {
                    _monitor.Log("Spirit's Eve timeout, triggering festival end");
                    Game1.CurrentEvent.TryStartEndFestivalDialogue(Game1.player);
                    _startedFestivalEnd = true;
                }
            }

            // Ice fishing event
            else if (iceFishingAvailable)
            {
                if (eventCommandUsed)
                {
                    iceFishingStartTime = DateTime.UtcNow.AddSeconds(-TicksToSeconds(Config.IceFishingCountDownConfig));
                    iceFishingAnnounced = true;
                    eventCommandUsed = false;
                }

                if (!iceFishingStartTime.HasValue)
                {
                    iceFishingStartTime = DateTime.UtcNow;
                }

                double elapsed = ElapsedSeconds(iceFishingStartTime);
                double countdownSeconds = TicksToSeconds(Config.IceFishingCountDownConfig);

                if (!iceFishingAnnounced)
                {
                    float chatIceFish = Config.IceFishingCountDownConfig / 60f;
                    _helper.SendPublicMessage($"The Ice Fishing Contest will begin in {chatIceFish:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                    iceFishingAnnounced = true;
                }

                if (!iceFishingStarted && elapsed >= countdownSeconds)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                    iceFishingStarted = true;
                }

                if (elapsed >= countdownSeconds + 5.0 / 60.0)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        //_helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    //festival timeout
                    if (!resetStartTime.HasValue)
                    {
                        resetStartTime = DateTime.UtcNow;
                    }
                    if (ElapsedSeconds(resetStartTime) >= TicksToSeconds(Config.FestivalOfIceTimeOut + 180))
                    {
                        Game1.options.setServerMode("offline");
                    }
                    ///////////////////////////////////////////////
                }
            }

            // Feast of the Winter event
            else if (winterFeastAvailable)
            {
                if (!winterFeastStartTime.HasValue)
                {
                    winterFeastStartTime = DateTime.UtcNow;
                }

                if (!resetStartTime.HasValue)
                {
                    resetStartTime = DateTime.UtcNow;
                }

                double resetElapsed = ElapsedSeconds(resetStartTime);
                double winterTimeoutSeconds = TicksToSeconds(Config.WinterStarTimeOut);

                //festival timeout code
                if (!_timeoutWarned && resetElapsed >= winterTimeoutSeconds - TicksToSeconds(120))
                {
                    _helper.SendPublicMessage("2 minutes to the exit or");
                    _helper.SendPublicMessage("everyone will be kicked.");
                    _timeoutWarned = true;
                }

                if (resetElapsed >= winterTimeoutSeconds)
                {
                    Game1.options.setServerMode("offline");
                }

                ///////////////////////////////////////////////
                if (ElapsedSeconds(winterFeastStartTime) >= TicksToSeconds(10) && !_startedFestivalEnd)
                {
                    _monitor.Log("Winter Feast timeout, triggering festival end");
                    Game1.CurrentEvent.TryStartEndFestivalDialogue(Game1.player);
                    _startedFestivalEnd = true;
                }
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

            if (SDateHelper.IsEggFestivalToday())
            {
                eventCommandUsed = true;
                eggHuntAvailable = true;
            }
            else if (SDateHelper.IsFlowerDanceToday())
            {
                eventCommandUsed = true;
                flowerDanceAvailable = true;
            }
            else if (SDateHelper.IsLuauToday())
            {
                eventCommandUsed = true;
                luauSoupAvailable = true;
            }
            else if (SDateHelper.IsDanceOfJelliesToday())
            {
                eventCommandUsed = true;
                jellyDanceAvailable = true;
            }
            else if (SDateHelper.IsStardewValleyFairToday())
            {
                eventCommandUsed = true;
                grangeDisplayAvailable = true;
            }
            else if (SDateHelper.IsSpiritsEveToday())
            {
                eventCommandUsed = true;
                goldenPumpkinAvailable = true;
            }
            else if (SDateHelper.IsFestivalOfIceToday())
            {
                eventCommandUsed = true;
                iceFishingAvailable = true;
            }
            else if (SDateHelper.IsFeastOfWinterStarToday())
            {
                eventCommandUsed = true;
                winterFeastAvailable = true;
            }
        }

    }
}
