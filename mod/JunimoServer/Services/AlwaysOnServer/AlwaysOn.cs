using JunimoServer.Services.ChatCommands;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Network;
using StardewValley.Objects;
using System.Linq;

namespace JunimoServer.Services.AlwaysOn
{
    public class AlwaysOnServer : ModService
    {
        // TODO: Find a way to replace this static stuff.
        public static bool PlayerIsHidden = true;

        /// <summary>
        /// Whether the main player is currently being automated.
        /// </summary>
        public bool IsAutomating;

        public bool clientPaused;

        /// <summary>
        /// Set to true when ShippingMenu should be active, then used
        /// </summary>
        private bool _isShippingMenuActive;

        private bool doWarpRoutineToGetToNextDay = false;
        private int _warpTickCounter = 0;

        // Shipping menu timeout reset, causes menu to be closed when bigger than `Config.EndOfDayTimeOut`
        private int shippingMenuTimeoutTicks;

        private AlwaysOnServerFestivals alwaysOnServerFestivals;

        private readonly AlwaysOnConfig Config;

        public AlwaysOnServer(ChatCommandsService chatCommandService, AlwaysOnConfig config, IModHelper helper, IMonitor monitor) : base(helper, monitor)
        {
            Config = config;

            // Register chat commands
            helper.ConsoleCommands.Add("host-auto", "Toggles host auto mode on/off", ToggleAutoModeCommand);
            helper.ConsoleCommands.Add("host-visibility", "Toggles host visibility on/off", ToggleVisibilityCommand);

            // Extracted festival logic
            alwaysOnServerFestivals = new AlwaysOnServerFestivals(helper, monitor, chatCommandService, config);
        }

        public override void Entry()
        {
            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            Helper.Events.GameLoop.Saving += OnSaving;
            Helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
            Helper.Events.GameLoop.TimeChanged += OnTimeChanged;
            Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            Helper.Events.Input.ButtonPressed += OnButtonPressed;
            Helper.Events.Display.Rendered += OnRendered;
            Helper.Events.Specialized.UnvalidatedUpdateTicked += OnUnvalidatedUpdateTick;
            Helper.Events.GameLoop.DayEnding += OnDayEnd;
            Helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            // TODO: Restart the server if we happen to return to the title, this should not happen during regular operations
            IsAutomating = false;
        }

        private void OnDayEnd(object sender, DayEndingEventArgs e)
        {
            doWarpRoutineToGetToNextDay = false;
            _warpTickCounter = 0;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            if (Game1.IsServer)
            {
                ToggleAutoMode();

                if (Game1.player != null)
                {
                    Game1.player.Equip(ItemRegistry.Create<Hat>($"{ItemRegistry.type_hat}JunimoHat"), Game1.player.hat);
                    Game1.player.ignoreCollisions = true;
                }
            }
        }

        private void OnRendered(object sender, RenderedEventArgs e)
        {
            if (!IsAutomating || !Game1.options.enableServer)
            {
                return;
            }

            // Draw server status information in the top left corner
            AlwaysOnUtil.DrawTextBox(5, 100, Game1.dialogueFont, "Auto Mode On");
            AlwaysOnUtil.DrawTextBox(5, 180, Game1.dialogueFont, $"Press {Config.HotKeyToggleAutoMode} On/Off");
            AlwaysOnUtil.DrawTextBox(5, 300, Game1.dialogueFont, "Visibility On");
            AlwaysOnUtil.DrawTextBox(5, 380, Game1.dialogueFont, $"Press {Config.HotKeyToggleVisibility} On/Off");
            AlwaysOnUtil.DrawTextBox(5, 540, Game1.dialogueFont, $"{Game1.server.connectionsCount} Players Online");
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady)
            {
                return;
            }

            if (e.Button == Config.HotKeyToggleAutoMode)
            {
                ToggleAutoMode();
            }
            else if (e.Button == Config.HotKeyToggleVisibility)
            {
                ToggleVisibility();
            }
        }

        private void OnOneSecondUpdateTicked(object sender, OneSecondUpdateTickedEventArgs e)
        {
            if (!IsAutomating)
            {
                return;
            }

            HandleNextDayWarp();
            HandleDialogueBox();
            HandleSkippableEvent();
            alwaysOnServerFestivals.HandleFestivalEvents();
            HandleLevelUpMenu();
            HandlePetChoice();
            HandleCaveChoice();
            HandleCommunityCenterUnlock();
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // Automate choices
            if (!IsAutomating)
            {
                Game1.paused = false;
                return;
            }

            HandleAutoPause();
            HandleAutoSleep();

            alwaysOnServerFestivals.HandleLeaveFestival();

            HandleLockPlayersChests();
        }

        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            if (!IsAutomating)
            {
                return;
            }

            alwaysOnServerFestivals.UpdateFestivalStatus();

            // Skip handlers on festival days
            if (SDateHelper.IsFestivalToday())
            {
                return;
            }

            // Run various handlers
            if (Game1.timeOfDay == 620)
            {
                HandleMailbox();
            }
            else if (Game1.timeOfDay == 630)
            {
                HandleSewersKey();
                HandleCommunityCenter();
                HandleJojaMarket();
            }
            else if (Game1.timeOfDay == 900)
            {
                HandleFishingRod();
            }
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            if (!IsAutomating)
            {
                return;
            }

            // Starts a timeout in OnUnvalidatedUpdateTick
            _isShippingMenuActive = true;

            if (Game1.activeClickableMenu is ShippingMenu)
            {
                Monitor.Log("Clicking OK on ShippingMenu");
                Helper.Reflection.GetMethod(Game1.activeClickableMenu, "okClicked").Invoke();
            }
        }

        private void OnUnvalidatedUpdateTick(object sender, UnvalidatedUpdateTickedEventArgs e)
        {
            // Waiting for ShippingMenu/Save/EndOfDay to timeout
            if (_isShippingMenuActive && Config.EndOfDayTimeOut != 0)
            {
                shippingMenuTimeoutTicks += 1;

                if (shippingMenuTimeoutTicks >= Config.EndOfDayTimeOut * 60)
                {
                    // Prevent others from joining after timeout
                    Game1.options.setServerMode("offline");
                }
            }

            if (Game1.timeOfDay == 610)
            {
                // Reset the ShippingMenu timeout since the game obviously progressed
                _isShippingMenuActive = false;
                shippingMenuTimeoutTicks = 0;

                // Set online again in case the game kept going after timing out prematurely
                Game1.options.setServerMode("online");
            }

            // TODO: Why do we do this?
            if (Game1.timeOfDay == 2600)
            {
                Game1.paused = false;
            }
        }

        /// <summary>
        /// Toggles auto mode on/off with console command "server"
        /// </summary>
        /// <param name="command"></param>
        /// <param name="args"></param>
        private void ToggleAutoModeCommand(string command, string[] args)
        {
            ToggleAutoMode();
        }

        /// <summary>
        /// Toggles host farmhand visibility on/off with console command "server"
        /// </summary>
        /// <param name="command"></param>
        /// <param name="args"></param>
        private void ToggleVisibilityCommand(string command, string[] args)
        {
            ToggleVisibility();
        }

        private void ToggleAutoMode()
        {
            if (!Context.IsWorldReady)
            {
                return;
            }

            if (!IsAutomating)
            {
                Game1.chatBox.addInfoMessage("The host is in automatic mode!");
                var hudMessage = new HUDMessage("Auto Mode On!");
                Game1.addHUDMessage(hudMessage);
                Monitor.Log(hudMessage.message, LogLevel.Info);
            }
            else
            {
                Game1.chatBox.addInfoMessage("The host has returned!");
                var hudMessage = new HUDMessage("Auto Mode Off!");
                Game1.addHUDMessage(hudMessage);
                Monitor.Log(hudMessage.message, LogLevel.Info);
            }

            IsAutomating = !IsAutomating;
            Game1.displayHUD = true;
        }

        private void ToggleVisibility()
        {
            if (!Context.IsWorldReady)
            {
                return;
            }

            if (!Game1.displayFarmer)
            {
                Monitor.Log("Host farmhand is now visible!", LogLevel.Info);
                Game1.addHUDMessage(new HUDMessage("Host farmhand is now visible!"));
            }
            else
            {
                Monitor.Log("Host farmhand is now invisible!", LogLevel.Info);
                Game1.addHUDMessage(new HUDMessage("Host farmhand is now invisible!"));
            }

            PlayerIsHidden = !PlayerIsHidden;
        }

        private void HandlePetChoice()
        {
            if (!IsAutomating)
            {
                return;
            }

            // Initial pet choice
            if (!Game1.player.hasPet())
            {
                Helper.Reflection.GetMethod(new Event(), "namePet").Invoke(this.Config.PetName.Substring(0));
            }

            // Update pet name
            if (Game1.player.hasPet())
            {
                var pet = Game1.player.getPet();
                pet.Name = this.Config.PetName.Substring(0);
                pet.displayName = this.Config.PetName.Substring(0);
            }

        }

        private void HandleCaveChoice()
        {
            if (!IsAutomating)
            {
                return;
            }

            // Cave choice unlock
            if (!Game1.player.eventsSeen.Contains("65"))
            {
                Game1.player.eventsSeen.Add("65");


                if (this.Config.FarmCaveChoiceIsMushrooms)
                {
                    Game1.MasterPlayer.caveChoice.Value = 2;
                    (Game1.getLocationFromName("FarmCave") as FarmCave).setUpMushroomHouse();
                }
                else
                {
                    Game1.MasterPlayer.caveChoice.Value = 1;
                }
            }
        }

        private void HandleCommunityCenterUnlock()
        {
            if (!IsAutomating)
            {
                return;
            }

            // Community center unlock
            if (!Game1.player.eventsSeen.Contains("611439"))
            {
                Game1.player.eventsSeen.Add("611439");
                Game1.MasterPlayer.mailReceived.Add("ccDoorUnlock");
            }
        }

        /// <summary>
        /// Skip random dialogs (e.g. "no mails").
        /// </summary>
        private void HandleDialogueBox()
        {
            if (!IsAutomating || Game1.activeClickableMenu == null || Game1.activeClickableMenu is not DialogueBox)
            {
                return;
            }

            Monitor.Log("Clicking DialogBox", LogLevel.Info);
            Game1.activeClickableMenu.receiveLeftClick(10, 10);
        }

        /// <summary>
        /// Skip events that have a skip button.
        /// </summary>
        private void HandleSkippableEvent()
        {
            // Left click menu spammer and event skipper to get through random events happening
            // also moves player around, this seems to free host from random bugs sometimes
            if (!IsAutomating || Game1.CurrentEvent == null || !Game1.CurrentEvent.skippable)
            {
                return;
            }

            bool skipped = Game1.CurrentEvent.skipped;
            bool finished = Helper.Reflection.GetField<bool>(Game1.CurrentEvent, "eventFinished").GetValue();

            if (!skipped && !finished)
            {
                Monitor.Log("Skipping event", LogLevel.Info);
                Game1.CurrentEvent.skipEvent();
                Game1.CurrentEvent.receiveMouseClick(1, 2);
            }
        }

        /// <summary>
        /// Pause the game when there are no clients connected.
        /// </summary>
        private void HandleAutoPause()
        {
            var numPlayers = Game1.otherFarmers.Count;
            var isFestivalDay = SDateHelper.IsFestivalToday();

            if (numPlayers >= 1)
            {
                if (clientPaused)
                {
                    Game1.netWorldState.Value.IsPaused = true;
                }
                else
                {
                    Game1.paused = false;
                }
            }
            else if (numPlayers == 0 && Game1.timeOfDay is >= 610 and <= 2500 && !isFestivalDay)
            {
                Game1.paused = true;
            }
        }

        /// <summary>
        /// Close the level up menu that pops up at the end of a day.
        /// </summary>
        private void HandleLevelUpMenu()
        {
            if (!IsAutomating || Game1.activeClickableMenu is not LevelUpMenu menu)
            {
                return;
            }

            // Taken from LevelUpMenu.cs:504
            Monitor.Log("[Automation] Skipping level up menu", LogLevel.Info);
            menu.isActive = false;
            menu.informationUp = false;
            menu.isProfessionChooser = false;
            menu.RemoveLevelFromLevelList();
        }

        /// <summary>
        /// Start sleep once the last online player went to bed.
        /// </summary>
        private void HandleAutoSleep()
        {
            var numPlayers = Game1.otherFarmers.Count;

            // Skip sleeping without other players
            if (numPlayers == 0)
            {
                return;
            }

            var numReadySleep = Game1.netReady.GetNumberReady("sleep");
            var numberRequiredSleep = Game1.netReady.GetNumberRequired("sleep");

            // Go to sleep once server is the last player awake
            if (numberRequiredSleep - numReadySleep == 1)
            {
                Monitor.Log("Called StartSleep");

                FarmHouse farmHouse = Game1.getLocationFromName("Farmhouse") as FarmHouse;
                Game1.player.lastSleepLocation.Value = farmHouse.NameOrUniqueName;
                Game1.player.lastSleepPoint.Value = farmHouse.GetPlayerBedSpot();
                Helper.Reflection.GetMethod(farmHouse, "startSleep").Invoke();

                doWarpRoutineToGetToNextDay = true;
            }
        }

        /// <summary>
        /// Warp in an attempt to skip the day faster due to bypassing certain animations.
        /// </summary>
        private void HandleNextDayWarp()
        {
            if (!doWarpRoutineToGetToNextDay)
            {
                return;
            }

            Monitor.Log("Attempting to warp to next day");


            if (_warpTickCounter % 10 == 1)
            {
                AlwaysOnUtil.WarpToHouse();
            }
            else if (_warpTickCounter % 10 == 5)
            {
                AlwaysOnUtil.WarpToHidingSpot();
            }

            _warpTickCounter++;
        }

        private void HandleMailbox()
        {
            // Checking mails once more closes the last one
            int mailboxCount = Game1.mailbox.Count + 1;

            // Check all mails
            for (int i = 0; i < mailboxCount; i++)
            {
                Helper.Reflection.GetMethod(Game1.currentLocation, "mailbox").Invoke();
            }
        }

        private void HandleSewersKey()
        {
            if (!Game1.player.hasRustyKey)
            {
                if (LibraryMuseum.totalArtifacts >= 60)
                {
                    Game1.player.eventsSeen.Add("295672");
                    Game1.player.eventsSeen.Add("66");
                    Game1.player.hasRustyKey = true;
                }
            }
        }

        private void HandleCommunityCenter()
        {
            if (!Config.IsCommunityCenterRun)
            {
                return;
            }

            bool hasCommunityCenterConditions = Game1.player.eventsSeen.Contains("191393") ||
                !Game1.player.mailReceived.Contains("ccCraftsRoom") ||
                !Game1.player.mailReceived.Contains("ccVault") ||
                !Game1.player.mailReceived.Contains("ccFishTank") ||
                !Game1.player.mailReceived.Contains("ccBoilerRoom") ||
                !Game1.player.mailReceived.Contains("ccPantry") ||
                !Game1.player.mailReceived.Contains("ccBulletin");

            if (hasCommunityCenterConditions)
            {
                return;
            }

            CommunityCenter communityCenter = Game1.getLocationFromName("CommunityCenter") as CommunityCenter;
            for (int index = 0; index < communityCenter.areasComplete.Count; ++index)
            {
                communityCenter.areasComplete[index] = true;
            }
            Game1.player.eventsSeen.Add("191393");
        }

        private bool CheckJojaMarketProgress(int money, string mail)
        {
            return Game1.player.Money >= money && !Game1.player.mailReceived.Contains(mail);
        }

        private void HandleJojaMarket()
        {
            if (!Config.IsCommunityCenterRun)
            {
                return;
            }

            if (CheckJojaMarketProgress(10000, "JojaMember"))
            {
                Game1.player.Money -= 5000;
                Game1.player.mailReceived.Add("JojaMember");
                Helper.SendPublicMessage("Buying Joja Membership");
            }

            if (CheckJojaMarketProgress(30000, "jojaBoilerRoom"))
            {
                Game1.player.Money -= 15000;
                Game1.player.mailReceived.Add("ccBoilerRoom");
                Game1.player.mailReceived.Add("jojaBoilerRoom");
                Helper.SendPublicMessage("Buying Joja Minecarts");
            }

            if (CheckJojaMarketProgress(40000, "jojaFishTank"))
            {
                Game1.player.Money -= 20000;
                Game1.player.mailReceived.Add("ccFishTank");
                Game1.player.mailReceived.Add("jojaFishTank");
                Helper.SendPublicMessage("Buying Joja Panning");
            }

            if (CheckJojaMarketProgress(50000, "jojaCraftsRoom"))
            {
                Game1.player.Money -= 25000;
                Game1.player.mailReceived.Add("ccCraftsRoom");
                Game1.player.mailReceived.Add("jojaCraftsRoom");
                Helper.SendPublicMessage("Buying Joja Bridge");
            }

            if (CheckJojaMarketProgress(70000, "jojaPantry"))
            {
                Game1.player.Money -= 35000;
                Game1.player.mailReceived.Add("ccPantry");
                Game1.player.mailReceived.Add("jojaPantry");
                Helper.SendPublicMessage("Buying Joja Greenhouse");
            }

            if (CheckJojaMarketProgress(80000, "jojaVault"))
            {
                Game1.player.Money -= 40000;
                Game1.player.mailReceived.Add("ccVault");
                Game1.player.mailReceived.Add("jojaVault");
                Helper.SendPublicMessage("Buying Joja Bus");
                Game1.player.eventsSeen.Add("502261");
            }
        }

        private void HandleFishingRod()
        {
            if (Game1.player.eventsSeen.Contains("739330"))
            {
                return;
            }

            Game1.player.increaseBackpackSize(1);
            Game1.warpFarmer("Beach", 44, 35, 1);
        }

        /// <summary>
        /// Lock players chests.
        /// </summary>
        /// <see cref="https://github.com/funny-snek/Always-On-Server-for-Multiplayer/blob/master/Always%20On%20Server/ModEntry.cs#L987">Original code seems to come from here</see>
        private void HandleLockPlayersChests()
        {
            if (!Config.LockPlayerChests)
            {
                return;
            }

            foreach (var farmer in Game1.otherFarmers.Values)
            {
                if (farmer.currentLocation is FarmHouse house && farmer != house.owner)
                {
                    // Lock offline player inventory
                    if (house is Cabin cabin)
                    {
                        NetMutex inventoryMutex = Helper.Reflection.GetField<NetMutex>(cabin, "inventoryMutex").GetValue();
                        inventoryMutex.RequestLock();
                    }

                    // Lock fridge & chests
                    house.fridge.Value.mutex.RequestLock();
                    foreach (var chest in house.objects.Values.OfType<Chest>())
                    {
                        chest.mutex.RequestLock();
                    }
                }
            }
        }
    }
}
