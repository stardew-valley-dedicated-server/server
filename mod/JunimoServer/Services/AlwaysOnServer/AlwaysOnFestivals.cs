using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
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

        protected readonly AlwaysOnServer _alwaysOnServer;

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

        public void UpdateFestivalStatus()
        {
            if (Game1.otherFarmers.Count == 0)
            {
                return;
            }

            if (SDateHelper.IsEggFestivalToday())
            {
                EggFestival();
            }
            else if (SDateHelper.IsFlowerDanceToday())
            {
                FlowerDance();
            }
            else if (SDateHelper.IsLuauToday())
            {
                Luau();
            }
            else if (SDateHelper.IsDanceOfJelliesToday())
            {
                DanceOfTheMoonlightJellies();
            }
            else if (SDateHelper.IsStardewValleyFairToday())
            {
                StardewValleyFair();
            }
            else if (SDateHelper.IsSpiritsEveToday())
            {
                SpiritsEve();
            }
            else if (SDateHelper.IsFestivalOfIceToday())
            {
                FestivalOfIce();
            }
            else if (SDateHelper.IsFeastOfWinterStarToday())
            {
                FeastOfWinterStar();
            }
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
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "answerDialogueQuestion")
                        .Invoke(Game1.getCharacterFromName("Lewis"), "yes");
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
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "answerDialogueQuestion")
                        .Invoke(Game1.getCharacterFromName("Lewis"), "yes");
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
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "answerDialogueQuestion")
                        .Invoke(Game1.getCharacterFromName("Lewis"), "yes");
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
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "answerDialogueQuestion")
                        .Invoke(Game1.getCharacterFromName("Lewis"), "yes");
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
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "answerDialogueQuestion")
                        .Invoke(Game1.getCharacterFromName("Lewis"), "yes");
                }

                if (grangeDisplayCountDown == Config.GrangeDisplayCountDownConfig + 5)
                {
                    LeaveFestival();
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
                if (goldenPumpkinCountDown == 10)
                {
                    LeaveFestival();
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
                    _helper.Reflection.GetMethod(Game1.CurrentEvent, "answerDialogueQuestion")
                        .Invoke(Game1.getCharacterFromName("Lewis"), "yes");
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
                if (winterFeastCountDown == 10)
                {
                    LeaveFestival();
                }
            }
        }

        public void HandleLeaveFestival()
        {
            if (Game1.otherFarmers.Count == 0)
            {
                return;
            }

            var numReady = Game1.netReady.GetNumberReady("festivalEnd");
            var numReq = Game1.netReady.GetNumberRequired("festivalEnd");

            if (numReq - numReady == 1)
            {
                LeaveFestival();
            }
        }

        private void EggFestival()
        {
            var currentTime = Game1.timeOfDay;

            if (currentTime is >= 900 and <= 1400)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Town", 1, 20, 1);
                });

                eggHuntAvailable = true;
            }
            else if (currentTime >= 1410)
            {
                eggHuntAvailable = false;
                Game1.options.setServerMode("online");
                eggHuntCountDown = 0;
                festivalTicksForReset = 0;
            }
        }

        private void FlowerDance()
        {
            var currentTime = Game1.timeOfDay;

            if (currentTime is >= 900 and <= 1400)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Forest", 1, 20, 1);
                });

                flowerDanceAvailable = true;
            }
            else if (currentTime >= 1410)
            {
                flowerDanceAvailable = false;
                Game1.options.setServerMode("online");
                flowerDanceCountDown = 0;
                festivalTicksForReset = 0;
            }
        }

        private void Luau()
        {
            var currentTime = Game1.timeOfDay;
            if (currentTime is >= 900 and <= 1400)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Beach", 1, 20, 1);
                });

                luauSoupAvailable = true;
            }
            else if (currentTime >= 1410)
            {
                luauSoupAvailable = false;
                Game1.options.setServerMode("online");
                luauSoupCountDown = 0;
                festivalTicksForReset = 0;
            }
        }

        private void DanceOfTheMoonlightJellies()
        {
            var currentTime = Game1.timeOfDay;

            if (currentTime >= 2200 && currentTime <= 2400)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Beach", 1, 20, 1);
                });

                jellyDanceAvailable = true;
            }
            else if (currentTime >= 2410)
            {
                jellyDanceAvailable = false;
                Game1.options.setServerMode("online");
                jellyDanceCountDown = 0;
                festivalTicksForReset = 0;
            }
        }

        private void StardewValleyFair()
        {
            var currentTime = Game1.timeOfDay;

            if (currentTime >= 900 && currentTime <= 1500)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Town", 1, 20, 1);
                });

                grangeDisplayAvailable = true;
            }
            else if (currentTime >= 1510)
            {
                Game1.displayHUD = true;
                grangeDisplayAvailable = false;
                Game1.options.setServerMode("online");
                grangeDisplayCountDown = 0;
                festivalTicksForReset = 0;
            }
        }

        private void SpiritsEve()
        {
            var currentTime = Game1.timeOfDay;

            if (currentTime is >= 2200 and <= 2350)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Town", 1, 20, 1);
                });

                goldenPumpkinAvailable = true;
            }
            else if (currentTime >= 2400)
            {
                Game1.displayHUD = true;
                goldenPumpkinAvailable = false;
                Game1.options.setServerMode("online");
                goldenPumpkinCountDown = 0;
                festivalTicksForReset = 0;
            }
        }

        private void FestivalOfIce()
        {
            var currentTime = Game1.timeOfDay;

            if (currentTime >= 900 && currentTime <= 1400)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Forest", 1, 20, 1);
                });

                iceFishingAvailable = true;
            }
            else if (currentTime >= 1410)
            {
                iceFishingAvailable = false;
                Game1.options.setServerMode("online");
                iceFishingCountDown = 0;
                festivalTicksForReset = 0;
            }
        }

        private void FeastOfWinterStar()
        {
            var currentTime = Game1.timeOfDay;

            if (currentTime >= 900 && currentTime <= 1400)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
                Game1.activeClickableMenu = new ReadyCheckDialog("festivalStart", true, who => {
                    Game1.exitActiveMenu();
                    Game1.warpFarmer("Town", 1, 20, 1);
                });

                winterFeastAvailable = true;
            }
            else if (currentTime >= 1410)
            {
                winterFeastAvailable = false;
                Game1.options.setServerMode("online");
                winterFeastCountDown = 0;
                festivalTicksForReset = 0;
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

        /// <summary>
        /// Leave the current festival if there is any.
        /// </summary>
        private void LeaveFestival()
        {
            _monitor.Log("Leaving festival");

            Game1.netReady.SetLocalReady("festivalEnd", true);
            Game1.activeClickableMenu = new ReadyCheckDialog("festivalEnd", true, who => {
                Game1.exitActiveMenu();
                AlwaysOnUtil.WarpToHidingSpot();

                Game1.timeOfDay = SDateHelper.IsSpiritsEveToday() ? 2400 : 2200;
            });
        }
    }
}
