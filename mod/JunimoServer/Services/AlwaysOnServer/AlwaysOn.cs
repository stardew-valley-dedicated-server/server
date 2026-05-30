using HarmonyLib;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.GameManager;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Minigames;
using StardewValley.Objects;
using System;
using System.Diagnostics;
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

        private bool _warpingSleep;

        // Shipping menu timeout reset, causes menu to be closed when bigger than `Config.EndOfDayTimeOut`
        private int shippingMenuTimeoutTicks;

        private AlwaysOnServerFestivals alwaysOnServerFestivals;

        private readonly AlwaysOnConfig Config;

        public AlwaysOnServer(ChatCommandsService chatCommandService, AlwaysOnConfig config, IModHelper helper, IMonitor monitor, Harmony harmony) : base(helper, monitor)
        {
            Config = config;

            // Register chat commands
            helper.ConsoleCommands.Add("host-auto", "Toggles host auto mode on/off", ToggleAutoModeCommand);
            helper.ConsoleCommands.Add("host-visibility", "Toggles host visibility on/off", ToggleVisibilityCommand);

            // Extracted festival logic
            alwaysOnServerFestivals = new AlwaysOnServerFestivals(helper, monitor, chatCommandService, config);

            // Re-claim Cabin.inventoryMutex inside vanilla's update method so it can
            // never be observed unlocked by peers — replaces the 60Hz polling that
            // used to live in HandleLockPlayersChests for the inventory case.
            CabinOverrides.Initialize(config);
            harmony.Patch(
                original: AccessTools.Method(typeof(Cabin), nameof(Cabin.updateEvenIfFarmerIsntHere)),
                postfix: new HarmonyMethod(typeof(CabinOverrides), nameof(CabinOverrides.UpdateEvenIfFarmerIsntHere_Postfix))
            );
        }

        // TODO: Rename to OnStart, OnLoad or whatever fits best.. need to double-check
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
            Helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
            Helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
            Helper.Events.World.ObjectListChanged += OnObjectListChanged;
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            if (GameManagerService.IsNewGamePending)
            {
                Monitor.Log("Returning to title for new game creation.", LogLevel.Info);
            }
            else
            {
                Monitor.Log("CRITICAL: Server unexpectedly returned to title screen. Automation disabled.", LogLevel.Error);
                Monitor.Log("Container may need restart. Check game logs for crash details.", LogLevel.Error);
            }

            // Reset all automation state regardless (OnSaveLoaded re-enables automation)
            IsAutomating = false;
            clientPaused = false;
            _isShippingMenuActive = false;
            _warpingSleep = false;
            shippingMenuTimeoutTicks = 0;
            _petChoiceHandled = false;
            ServerOptimizerOverrides.SetAutomationInputSuppression(false);
        }

        private void OnDayEnd(object sender, DayEndingEventArgs e)
        {
            _warpingSleep = false;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            if (Game1.IsServer)
            {
                // NOTE: The game has a built-in dedicated host mode (hasDedicatedHost)
                // that we deliberately do NOT use. Our mod handles automation independently.
                // Game1.player.team.hasDedicatedHost.Value = true;

                EnableAutoMode();

                if (Game1.player != null)
                {
                    Game1.player.Equip(ItemRegistry.Create<Hat>($"{ItemRegistry.type_hat}JunimoHat"), Game1.player.hat);
                    Game1.player.ignoreCollisions = true;

                    PreSeedEarlyGameEvents();
                }

                // Seed locks for cabins whose farmhands are already offline at load time.
                // The Cabin.updateEvenIfFarmerIsntHere postfix handles per-frame
                // inventoryMutex; this seeds fridge + chest mutexes once.
                LockOfflineFarmhandStorage();

                // Print startup banner after a delay (fallback if auth is skipped)
                if (!_bannerSubscribed)
                {
                    _bannerSubscribed = true;
                    Helper.Events.GameLoop.UpdateTicked += PrintBannerAfterDelay;
                }
            }
        }

        private bool _bannerSubscribed;
        private int _bannerDelayTicks = 0;
        private const int BannerDelaySeconds = 5;

        private void PrintBannerAfterDelay(object sender, UpdateTickedEventArgs e)
        {
            _bannerDelayTicks++;

            // Wait ~5 seconds (Env.ServerTps ticks per second)
            if (_bannerDelayTicks >= BannerDelaySeconds * Env.ServerTps)
            {
                Helper.Events.GameLoop.UpdateTicked -= PrintBannerAfterDelay;
                ServerBanner.Print(Monitor, Helper);
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
            if (Game1.gameMode != 3)
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

            HandleDialogueBox();
            HandleSkippableEvent();
            HandleMinigame();
            alwaysOnServerFestivals.HandleFestivalEvents();
            HandleLevelUpMenu();
            HandleShippingMenu();
            HandlePetChoice();
            HandleCaveChoice();
            HandleCommunityCenterUnlock();
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // Automate choices
            if (!IsAutomating)
            {
                Game1.netWorldState.Value.IsPaused = false;
                return;
            }

            HandleAutoPause();
            HandleAutoSleep();

            alwaysOnServerFestivals.HandleFestivalStart();
            alwaysOnServerFestivals.HandleFestivalLeave();
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
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            if (!IsAutomating)
            {
                return;
            }

            // Starts a timeout counter in OnUnvalidatedUpdateTick.
            // Note: The actual ShippingMenu clicking is handled by HandleShippingMenu()
            // in OnOneSecondUpdateTicked, which properly waits for the intro animation.
            _isShippingMenuActive = true;
        }

        private void OnUnvalidatedUpdateTick(object sender, UnvalidatedUpdateTickedEventArgs e)
        {
            // Waiting for ShippingMenu/Save/EndOfDay to timeout
            if (_isShippingMenuActive && Config.EndOfDayTimeOut != 0)
            {
                shippingMenuTimeoutTicks += 1;

                if (shippingMenuTimeoutTicks >= Config.EndOfDayTimeOut * Env.ServerTps)
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
        }

        /// <summary>
        /// Toggles auto mode on/off with console command "server"
        /// </summary>
        /// <param name="command"></param>
        /// <param name="args"></param>
        private void ToggleAutoModeCommand(string command, string[] args)
        {
            InvokeConsoleCommand(command, args, ToggleAutoMode);
        }

        /// <summary>
        /// Toggles host farmhand visibility on/off with console command "server"
        /// </summary>
        /// <param name="command"></param>
        /// <param name="args"></param>
        private void ToggleVisibilityCommand(string command, string[] args)
        {
            InvokeConsoleCommand(command, args, ToggleVisibility);
        }

        private static void InvokeConsoleCommand(string command, string[] args, Action handler)
        {
            var sw = Stopwatch.StartNew();
            string result = "ok";
            string? error = null;
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                result = "error";
                error = $"{ex.GetType().Name}: {ex.Message}";
                throw;
            }
            finally
            {
                sw.Stop();
                Diagnostics.ModEventLog.Emit("console_command_invoked", new
                {
                    command,
                    args,
                    result,
                    error,
                    durationMs = sw.ElapsedMilliseconds
                });
            }
        }

        /// <summary>
        /// Unconditionally enables host automation. Called on save load to ensure
        /// automation is always active regardless of prior state (e.g., rapid /newgame
        /// calls that can cause double OnSaveLoaded without intervening OnReturnedToTitle).
        /// </summary>
        private void EnableAutoMode()
        {
            if (Game1.gameMode != 3)
            {
                return;
            }

            IsAutomating = true;
            Game1.displayHUD = true;

            Game1.chatBox.addInfoMessage("Host automation: Enabled");
            Game1.addHUDMessage(new HUDMessage("Host automation: Enabled"));
            Monitor.Log("Host automation: Enabled", LogLevel.Info);

            ServerOptimizerOverrides.SetAutomationInputSuppression(true);
            Game1.player.Halt();
        }

        private void ToggleAutoMode()
        {
            if (Game1.gameMode != 3)
            {
                return;
            }

            var message = $"Host automation: {(!IsAutomating ? "Enabled" : "Disabled")}";

            Game1.chatBox.addInfoMessage(message);
            Game1.addHUDMessage(new HUDMessage(message));
            Monitor.Log(message, LogLevel.Info);

            IsAutomating = !IsAutomating;
            Game1.displayHUD = true;

            ServerOptimizerOverrides.SetAutomationInputSuppression(IsAutomating);
            if (IsAutomating)
            {
                Game1.player.Halt();
            }
        }

        private void ToggleVisibility()
        {
            if (Game1.gameMode != 3)
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

        private bool _petChoiceHandled;

        private void HandlePetChoice()
        {
            if (!IsAutomating || _petChoiceHandled)
            {
                return;
            }

            if (Game1.player.hasPet())
            {
                // Pet already exists (loaded from save). Just rename and mark done.
                var pet = Game1.player.getPet();
                pet.Name = Config.PetName;
                pet.displayName = Config.PetName;
                _petChoiceHandled = true;
                return;
            }

            // Call hostActionNamePet directly via reflection, avoiding the throwaway
            // Event() + namePet() pattern which calls Game1.exitActiveMenu() and
            // would destroy any active menu (e.g. ReadyCheckDialog from sleep).
            Monitor.Log($"[Automation] Creating pet '{Config.PetName}'", LogLevel.Info);
            Helper.Reflection.GetMethod(typeof(Event), "hostActionNamePet")
                .Invoke(Game1.player, CreateBinaryReader(Config.PetName));
            _petChoiceHandled = true;
        }

        private static System.IO.BinaryReader CreateBinaryReader(string value)
        {
            var stream = new System.IO.MemoryStream();
            var writer = new System.IO.BinaryWriter(stream);
            writer.Write(value);
            writer.Flush();
            stream.Position = 0;
            return new System.IO.BinaryReader(stream);
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
        /// Skip all events to keep the server unblocked.
        /// On a dedicated server, no one is watching. Skip everything,
        /// including non-skippable intro events that would block clients.
        /// EXCEPTION: Festivals are handled separately and should NOT be skipped here.
        /// </summary>
        private void HandleSkippableEvent()
        {
            if (!IsAutomating || Game1.CurrentEvent == null)
            {
                return;
            }

            // Festivals are technically skippable, but we want
            // to let players participate in them, so we have
            // special logic inside AlwaysOnFestivals
            if (Game1.CurrentEvent.isFestival)
            {
                return;
            }

            bool skipped = Game1.CurrentEvent.skipped;
            bool finished = Helper.Reflection.GetField<bool>(Game1.CurrentEvent, "eventFinished").GetValue();

            if (!skipped && !finished)
            {
                Monitor.Log($"Skipping event (skippable={Game1.CurrentEvent.skippable})", LogLevel.Info);
                Game1.CurrentEvent.skipEvent();
                Game1.CurrentEvent.receiveMouseClick(1, 2);
            }
        }

        /// <summary>
        /// Clear any active minigame (e.g. the Intro bus ride on a new save).
        /// Minigames block isGameAvailable() and prevent clients from connecting.
        /// </summary>
        private void HandleMinigame()
        {
            if (!IsAutomating || Game1.currentMinigame == null)
            {
                return;
            }

            Monitor.Log($"Clearing minigame: {Game1.currentMinigame.GetType().Name}", LogLevel.Info);
            Game1.currentMinigame.forceQuit();
        }

        /// <summary>
        /// Pause the game when there are no clients connected.
        /// After 2500 (1:00 AM), always unpause to allow the end-of-day pass-out sequence.
        /// </summary>
        private void HandleAutoPause()
        {
            var numPlayers = Game1.otherFarmers.Count;
            var isFestivalDay = SDateHelper.IsFestivalToday();

            if (numPlayers >= 1)
            {
                Game1.netWorldState.Value.IsPaused = clientPaused;
            }
            else if (numPlayers == 0 && !isFestivalDay)
            {
                // Pause during normal hours (610-2500), but unpause after 2500 to allow
                // the forced pass-out sequence at 2600 (2:00 AM) to proceed.
                Game1.netWorldState.Value.IsPaused = Game1.timeOfDay is >= 610 and <= 2500;
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
        /// Handle the ShippingMenu that appears when items were shipped.
        /// The menu has a 3.5s intro animation before OK can be clicked.
        /// This handler continuously checks and clicks OK once the intro is done.
        /// </summary>
        private void HandleShippingMenu()
        {
            if (!IsAutomating || Game1.activeClickableMenu is not ShippingMenu menu)
            {
                return;
            }

            // The ShippingMenu has an introTimer (starts at 3500ms) that must reach <= 0
            // before the OK button becomes clickable. We also need to ensure we're on the
            // main page (currentPage == -1) and not in the outro phase.
            int introTimer = Helper.Reflection.GetField<int>(menu, "introTimer").GetValue();

            // Force-skip the 3.5s intro animation, irrelevant for automated server
            if (introTimer > 0)
            {
                Helper.Reflection.GetField<int>(menu, "introTimer").SetValue(0);
                return; // Let the next tick handle the OK click
            }

            int currentPage = Helper.Reflection.GetField<int>(menu, "currentPage").GetValue();
            bool outro = Helper.Reflection.GetField<bool>(menu, "outro").GetValue();

            if (currentPage == -1 && !outro)
            {
                Monitor.Log("[Automation] Clicking OK on ShippingMenu", LogLevel.Info);
                Helper.Reflection.GetMethod(menu, "okClicked").Invoke();
            }
        }

        /// <summary>
        /// When every other connected player is ready to sleep, warp the host to
        /// their home (if not already there) and trigger Sleep_Yes. Ported from
        /// the game's own DedicatedServer.Tick() sleep branch
        /// (decompiled Network/Dedicated/DedicatedServer.cs:401-467). One-shot;
        /// <see cref="_warpingSleep"/> latches the warp phase to prevent re-entry.
        /// </summary>
        private void HandleAutoSleep()
        {
            if (Game1.otherFarmers.Count == 0) return;
            if (_warpingSleep) return;
            if (!OthersReadyForSleep()) return;

            var home = Game1.player.homeLocation.Value;

            if (Game1.currentLocation is FarmHouse here
                && string.Equals(here.NameOrUniqueName, home, StringComparison.OrdinalIgnoreCase))
            {
                Monitor.Log($"Host is home, sleeping in place ({here.NameOrUniqueName})", LogLevel.Info);
                HostSleepInBed(here);
                return;
            }

            Monitor.Log($"Warping host to {home} for sleep", LogLevel.Info);
            _warpingSleep = true;
            var req = Game1.getLocationRequest(home);
            req.OnWarp += () =>
            {
                if (Game1.currentLocation is FarmHouse fh)
                {
                    HostSleepInBed(fh);
                }
                else
                {
                    _warpingSleep = false;
                    Monitor.Log($"Sleep warp landed in unexpected location: {Game1.currentLocation?.NameOrUniqueName}", LogLevel.Warn);
                }
            };
            // Coords are any valid tile inside the target FarmHouse; HostSleepInBed
            // then snaps position to the actual bed spot. Matches DedicatedServer.cs:415.
            Game1.warpFarmer(req, 5, 9, Game1.player.FacingDirection);
        }

        private void HostSleepInBed(FarmHouse fh)
        {
            Game1.player.position.Set(Utility.PointToVector2(fh.GetPlayerBedSpot()) * 64f);
            fh.answerDialogueAction("Sleep_Yes", null);
            _warpingSleep = false;
        }

        /// <summary>
        /// Mirrors DedicatedServer.CheckOthersReady("sleep"): true once every other
        /// connected player has readied (ready = required - 1, host is the missing
        /// one), and only while the ready-check itself hasn't fired yet.
        /// LobbyService.UpdateSleepReadyCheckExclusion keeps excluded (lobby /
        /// unauthenticated) players out of GetNumberRequired, so this formula
        /// stays correct across Steam, LAN, and password-protected flows.
        /// </summary>
        private static bool OthersReadyForSleep()
        {
            int ready = Game1.netReady.GetNumberReady("sleep");
            if (ready <= 0) return false;
            if (Game1.netReady.IsReady("sleep")) return false;
            return ready >= Game1.netReady.GetNumberRequired("sleep") - 1;
        }

        private void HandleMailbox()
        {
            // GameLocation.mailbox() calls Game1.drawObjectDialogue even on an empty
            // mailbox, which replaces activeClickableMenu. If a ReadyCheckDialog is
            // live (e.g. HandleAutoSleep just fired), replacing it wedges the day
            // transition — see AlwaysOnUtil.IsReadyCheckActive for the full rationale.
            // Skipping is safe: mail persists across days, no downstream logic gates
            // on "mail was collected this day", and OnTimeChanged(620) tomorrow is
            // the retry. If that day is also blocked, we defer again.
            if (AlwaysOnUtil.IsReadyCheckActive()) return;

            // Nothing to collect — don't open the "mailbox is empty" dialog. It
            // briefly steals activeClickableMenu for no benefit and widens the race
            // window if a ReadyCheckDialog shows up a tick later.
            if (Game1.mailbox.Count == 0) return;

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
            if (Config.IsCommunityCenterRun)
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

        /// <summary>
        /// Pre-seed early-game events to prevent cutscenes and warps on the host.
        /// </summary>
        private void PreSeedEarlyGameEvents()
        {
            PreSeedFishingRodEvent();
            PreSeedTrainingRodEvent();
        }

        /// <summary>
        /// Event 739330: Willy's fishing rod intro (Beach, Spring Day 2).
        /// Grants the backpack slot without warping to the beach.
        /// </summary>
        private void PreSeedFishingRodEvent()
        {
            if (!Game1.player.eventsSeen.Contains("739330"))
            {
                Game1.player.eventsSeen.Add("739330");
                Game1.player.increaseBackpackSize(1);
            }
        }

        /// <summary>
        /// Event 980559: Training Rod follow-up (Farm, requires 739330 seen).
        /// </summary>
        private void PreSeedTrainingRodEvent()
        {
            if (!Game1.player.eventsSeen.Contains("980559"))
            {
                Game1.player.eventsSeen.Add("980559");
            }
        }

        /// <summary>
        /// Lock fridge + chests in every farmhand cabin whose owner is offline.
        /// Inventory mutex is handled separately by CabinOverrides postfix because
        /// vanilla auto-releases it every frame the host holds it. Fridge and chest
        /// mutexes don't auto-release (their Update only runs while the cabin is
        /// inhabited), so a one-shot acquire on lifecycle transitions is enough.
        /// </summary>
        private void LockOfflineFarmhandStorage()
        {
            if (!Config.LockPlayerChests) return;
            if (!Game1.IsMasterGame) return;

            var farm = Game1.getFarm();
            if (farm == null) return;

            var onlineFarmers = Game1.getOnlineFarmers();

            foreach (var building in farm.buildings)
            {
                if (!building.isCabin) continue;
                if (building.GetIndoors() is not Cabin cabin) continue;

                var owner = cabin.owner;
                if (owner != null && !owner.isUnclaimedFarmhand && onlineFarmers.Contains(owner)) continue;

                if (cabin.fridge.Value is { } fridge && !fridge.mutex.IsLocked())
                {
                    fridge.mutex.RequestLock();
                }
                foreach (var chest in cabin.objects.Values.OfType<Chest>())
                {
                    if (!chest.mutex.IsLocked()) chest.mutex.RequestLock();
                }
            }
        }

        /// <summary>
        /// Release host-held locks on the cabin owned by the farmhand who just
        /// came online, so they can open their own inventory / fridge / chests.
        /// inventoryMutex is included even though CabinOverrides will stop
        /// re-acquiring it next frame — releasing now avoids a one-frame stutter
        /// where the owner sees their inventory as locked.
        /// </summary>
        private void ReleaseOnlineFarmhandStorage(Farmer owner)
        {
            if (!Config.LockPlayerChests) return;
            if (!Game1.IsMasterGame) return;
            if (owner == null) return;

            var farm = Game1.getFarm();
            if (farm == null) return;

            foreach (var building in farm.buildings)
            {
                if (!building.isCabin) continue;
                if (building.GetIndoors() is not Cabin cabin) continue;
                if (cabin.owner != owner) continue;

                if (cabin.inventoryMutex.IsLockHeld()) cabin.inventoryMutex.ReleaseLock();
                if (cabin.fridge.Value is { } fridge && fridge.mutex.IsLockHeld()) fridge.mutex.ReleaseLock();
                foreach (var chest in cabin.objects.Values.OfType<Chest>())
                {
                    if (chest.mutex.IsLockHeld()) chest.mutex.ReleaseLock();
                }
                return;
            }
        }

        private void OnPeerConnected(object sender, PeerConnectedEventArgs e)
        {
            if (!Config.LockPlayerChests) return;
            if (!Game1.IsMasterGame) return;

            var farmer = Game1.GetPlayer(e.Peer.PlayerID, onlyOnline: true);
            if (farmer != null) ReleaseOnlineFarmhandStorage(farmer);
        }

        private void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)
        {
            // Re-seed across all cabins; a disconnect can leave any subset offline,
            // and the iteration is cheap (one pass over farm.buildings).
            LockOfflineFarmhandStorage();
        }

        private void OnObjectListChanged(object sender, ObjectListChangedEventArgs e)
        {
            if (!Config.LockPlayerChests) return;
            if (!Game1.IsMasterGame) return;
            if (e.Location is not Cabin cabin) return;

            var owner = cabin.owner;
            var ownerOffline = owner == null
                               || owner.isUnclaimedFarmhand
                               || !Game1.getOnlineFarmers().Contains(owner);
            if (!ownerOffline) return;

            foreach (var added in e.Added)
            {
                if (added.Value is Chest chest && !chest.mutex.IsLocked())
                {
                    chest.mutex.RequestLock();
                }
            }
        }
    }
}
