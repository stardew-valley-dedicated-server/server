using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace JunimoServer.Services.AlwaysOn
{
    public class AlwaysOnServerFestivals
    {
        private const string StartNowText = "Type !event to start now";

        private bool eventCommandUsed;

        private bool eggHuntAvailable;
        private int eggHuntCountDown;

        private bool flowerDanceAvailable;
        private int flowerDanceCountDown;

        private bool luauSoupAvailable;
        private int luauSoupCountDown;

        private bool jellyDanceAvailable;
        private int jellyDanceCountDown;

        private bool grangeDisplayAvailable;
        private int grangeDisplayCountDown;

        private bool goldenPumpkinAvailable;
        private int goldenPumpkinCountDown;

        private bool iceFishingAvailable;
        private int iceFishingCountDown;

        private bool winterFeastAvailable;
        private int winterFeastCountDown;

        // Variables for timeout reset
        private int festivalTicksForReset;

        // Track if we're currently warping to festival to avoid repeated warps
        private bool _warpingToFestival;

        // Track if we've started the festival end process
        private bool _startedFestivalEnd;

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
                eggHuntCountDown = 0;
            }
            else if (SDateHelper.IsFlowerDanceToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                flowerDanceAvailable = false;
                flowerDanceCountDown = 0;
            }
            else if (SDateHelper.IsLuauToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                luauSoupAvailable = false;
                luauSoupCountDown = 0;
            }
            else if (SDateHelper.IsDanceOfJelliesToday() && currentTime >= 2410)
            {
                ResetFestivalState();
                jellyDanceAvailable = false;
                jellyDanceCountDown = 0;
            }
            else if (SDateHelper.IsStardewValleyFairToday() && currentTime >= 1510)
            {
                ResetFestivalState();
                Game1.displayHUD = true;
                grangeDisplayAvailable = false;
                grangeDisplayCountDown = 0;
            }
            else if (SDateHelper.IsSpiritsEveToday() && currentTime >= 2400)
            {
                ResetFestivalState();
                Game1.displayHUD = true;
                goldenPumpkinAvailable = false;
                goldenPumpkinCountDown = 0;
            }
            else if (SDateHelper.IsFestivalOfIceToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                iceFishingAvailable = false;
                iceFishingCountDown = 0;
            }
            else if (SDateHelper.IsFeastOfWinterStarToday() && currentTime >= 1410)
            {
                ResetFestivalState();
                winterFeastAvailable = false;
                winterFeastCountDown = 0;
            }
        }

        private void ResetFestivalState()
        {
            Game1.options.setServerMode("online");
            festivalTicksForReset = 0;
            _warpingToFestival = false;
            _startedFestivalEnd = false;
        }

        /// <summary>
        /// Called every tick. Handles warping the host to the festival when other players are ready.
        /// Uses the same approach as the game's DedicatedServer - monitor ready state and warp directly.
        /// </summary>
        private int _logThrottle = 0;

        public void HandleFestivalStart()
        {
            _logThrottle++;

            // Debug: log every 60 ticks (once per second) on festival days
            if (Game1.whereIsTodaysFest != null && _logThrottle % 60 == 0)
            {
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
                    eggHuntCountDown = Config.EggHuntCountDownConfig;
                    eventCommandUsed = false;
                }

                eggHuntCountDown += 1;

                float chatEgg = Config.EggHuntCountDownConfig / 60f;
                if (eggHuntCountDown == 1)
                {
                    _helper.SendPublicMessage($"The Egg Hunt will begin in {chatEgg:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                }

                if (eggHuntCountDown == Config.EggHuntCountDownConfig + 1)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                }

                if (eggHuntCountDown >= Config.EggHuntCountDownConfig + 5)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        //_helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    //festival timeout
                    festivalTicksForReset += 1;
                    if (festivalTicksForReset >= Config.EggFestivalTimeOut + 180)
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
                    flowerDanceCountDown = Config.FlowerDanceCountDownConfig;
                    eventCommandUsed = false;
                }

                flowerDanceCountDown += 1;

                float chatFlower = Config.FlowerDanceCountDownConfig / 60f;
                if (flowerDanceCountDown == 1)
                {
                    _helper.SendPublicMessage($"The Flower Dance will begin in {chatFlower:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                }

                if (flowerDanceCountDown == Config.FlowerDanceCountDownConfig + 1)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                }

                if (flowerDanceCountDown >= Config.FlowerDanceCountDownConfig + 5)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        // _helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    //festival timeout
                    festivalTicksForReset += 1;
                    if (festivalTicksForReset >= Config.FlowerDanceTimeOut + 90)
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
                    luauSoupCountDown = Config.LuauSoupCountDownConfig;
                    // Add iridium starfruit to soup
                    var item = new SObject("268", 1, false, -1, 3);
                    _helper.Reflection.GetMethod(new Event(), "addItemToLuauSoup").Invoke(item, Game1.player);
                    eventCommandUsed = false;
                }

                luauSoupCountDown += 1;

                float chatSoup = Config.LuauSoupCountDownConfig / 60f;
                if (luauSoupCountDown == 1)
                {
                    _helper.SendPublicMessage($"The Soup Tasting will begin in {chatSoup:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);

                    // Add iridium starfruit to soup
                    var item = new SObject("268", 1, false, -1, 3);
                    _helper.Reflection.GetMethod(new Event(), "addItemToLuauSoup").Invoke(item, Game1.player);
                }

                if (luauSoupCountDown == Config.LuauSoupCountDownConfig + 1)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                }

                if (luauSoupCountDown >= Config.LuauSoupCountDownConfig + 5)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        //_helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    // Festival timeout
                    festivalTicksForReset += 1;
                    if (festivalTicksForReset >= Config.LuauTimeOut + 80)
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
                    jellyDanceCountDown = Config.JellyDanceCountDownConfig;
                    eventCommandUsed = false;
                }

                jellyDanceCountDown += 1;

                float chatJelly = Config.JellyDanceCountDownConfig / 60f;
                if (jellyDanceCountDown == 1)
                {
                    _helper.SendPublicMessage($"The Dance of the Moonlight Jellies will begin in {chatJelly:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                }

                if (jellyDanceCountDown == Config.JellyDanceCountDownConfig + 1)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                }

                if (jellyDanceCountDown >= Config.JellyDanceCountDownConfig + 5)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        // _helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    // Festival timeout
                    festivalTicksForReset += 1;
                    if (festivalTicksForReset >= Config.DanceOfJelliesTimeOut + 180)
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
                    grangeDisplayCountDown = Config.GrangeDisplayCountDownConfig;
                    eventCommandUsed = false;
                }

                grangeDisplayCountDown += 1;
                festivalTicksForReset += 1;

                // Festival timeout code
                if (festivalTicksForReset == Config.FairTimeOut - 120)
                {
                    _helper.SendPublicMessage("2 minutes to the exit or");
                    _helper.SendPublicMessage("everyone will be kicked.");
                }

                if (festivalTicksForReset >= Config.FairTimeOut)
                {
                    Game1.options.setServerMode("offline");
                }

                ///////////////////////////////////////////////
                float chatGrange = Config.GrangeDisplayCountDownConfig / 60f;
                if (grangeDisplayCountDown == 1)
                {
                    _helper.SendPublicMessage($"The Grange Judging will begin in {chatGrange:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                }

                if (grangeDisplayCountDown == Config.GrangeDisplayCountDownConfig + 1)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                }

                if (grangeDisplayCountDown == Config.GrangeDisplayCountDownConfig + 5 && !_startedFestivalEnd)
                {
                    _monitor.Log("Grange display finished, triggering festival end");
                    Game1.CurrentEvent.TryStartEndFestivalDialogue(Game1.player);
                    _startedFestivalEnd = true;
                }
            }

            // Golden pumpkin maze event
            else if (goldenPumpkinAvailable)
            {
                goldenPumpkinCountDown += 1;
                festivalTicksForReset += 1;

                // Festival timeout code
                if (festivalTicksForReset == Config.SpiritsEveTimeOut - 120)
                {
                    _helper.SendPublicMessage("2 minutes to the exit or");
                    _helper.SendPublicMessage("everyone will be kicked.");
                }

                if (festivalTicksForReset >= Config.SpiritsEveTimeOut)
                {
                    Game1.options.setServerMode("offline");
                }

                ///////////////////////////////////////////////
                if (goldenPumpkinCountDown == 10 && !_startedFestivalEnd)
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
                    iceFishingCountDown = Config.IceFishingCountDownConfig;
                    eventCommandUsed = false;
                }

                iceFishingCountDown += 1;

                float chatIceFish = Config.IceFishingCountDownConfig / 60f;
                if (iceFishingCountDown == 1)
                {
                    _helper.SendPublicMessage($"The Ice Fishing Contest will begin in {chatIceFish:0.#} minutes.");
                    _helper.SendPublicMessage(StartNowText);
                }

                if (iceFishingCountDown == Config.IceFishingCountDownConfig + 1)
                {
                    var festivalHost = GetFestivalHost();
                    if (festivalHost != null)
                    {
                        Game1.CurrentEvent.answerDialogueQuestion(festivalHost, "yes");
                    }
                }

                if (iceFishingCountDown >= Config.IceFishingCountDownConfig + 5)
                {
                    if (Game1.activeClickableMenu != null)
                    {
                        //_helper.Reflection.GetMethod(Game1.activeClickableMenu, "receiveLeftClick").Invoke(10, 10, true);
                    }

                    //festival timeout
                    festivalTicksForReset += 1;
                    if (festivalTicksForReset >= Config.FestivalOfIceTimeOut + 180)
                    {
                        Game1.options.setServerMode("offline");
                    }
                    ///////////////////////////////////////////////
                }
            }

            // Feast of the Winter event
            else if (winterFeastAvailable)
            {
                winterFeastCountDown += 1;
                festivalTicksForReset += 1;
                //festival timeout code
                if (festivalTicksForReset == Config.WinterStarTimeOut - 120)
                {
                    _helper.SendPublicMessage("2 minutes to the exit or");
                    _helper.SendPublicMessage("everyone will be kicked.");
                }

                if (festivalTicksForReset >= Config.WinterStarTimeOut)
                {
                    Game1.options.setServerMode("offline");
                }

                ///////////////////////////////////////////////
                if (winterFeastCountDown == 10 && !_startedFestivalEnd)
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

            // Debug: log festivalEnd ready state
            var endReady = Game1.netReady.GetNumberReady("festivalEnd");
            var endRequired = Game1.netReady.GetNumberRequired("festivalEnd");
            if (_logThrottle % 60 == 0)
            {
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
