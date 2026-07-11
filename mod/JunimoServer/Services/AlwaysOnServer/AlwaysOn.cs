using System;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using JunimoServer.Services.ChatCommands;
using JunimoServer.Services.GameManager;
using JunimoServer.Services.ServerOptim;
using JunimoServer.Shared;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;

namespace JunimoServer.Services.AlwaysOn;

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

    private readonly AlwaysOnModCompat modCompat;

    private readonly AlwaysOnConfig Config;

    public AlwaysOnServer(
        ChatCommandsService chatCommandService,
        AlwaysOnConfig config,
        IModHelper helper,
        IMonitor monitor,
        Harmony harmony
    )
        : base(helper, monitor)
    {
        Config = config;

        // Register chat commands
        helper.ConsoleCommands.Add(
            "host-auto",
            "Toggles host auto mode on/off",
            ToggleAutoModeCommand
        );
        helper.ConsoleCommands.Add(
            "host-visibility",
            "Toggles host visibility on/off",
            ToggleVisibilityCommand
        );

        // Extracted festival logic
        alwaysOnServerFestivals = new AlwaysOnServerFestivals(
            helper,
            monitor,
            chatCommandService,
            config
        );

        // Third-party mod compatibility workarounds (kept separate from base-game automation)
        modCompat = new AlwaysOnModCompat(helper, monitor);

        // Re-claim Cabin.inventoryMutex inside vanilla's update method so it can
        // never be observed unlocked by peers — replaces the 60Hz polling that
        // used to live in HandleLockPlayersChests for the inventory case.
        CabinOverrides.Initialize(config);
        harmony.Patch(
            original: AccessTools.Method(typeof(Cabin), nameof(Cabin.updateEvenIfFarmerIsntHere)),
            postfix: new HarmonyMethod(
                typeof(CabinOverrides),
                nameof(CabinOverrides.UpdateEvenIfFarmerIsntHere_Postfix)
            )
        );

        // Force-complete the Mr. Qi mystery-box overnight cutscene on the host. Its completion gate
        // lives in draw() rather than tickUpdate(), so on a headless host (draws gated/desynced) it
        // never converges and the new day never starts. See QiPlaneEventOverrides.
        QiPlaneEventOverrides.Initialize();
        harmony.Patch(
            original: AccessTools.Method(
                typeof(StardewValley.Events.QiPlaneEvent),
                nameof(StardewValley.Events.QiPlaneEvent.tickUpdate)
            ),
            postfix: new HarmonyMethod(
                typeof(QiPlaneEventOverrides),
                nameof(QiPlaneEventOverrides.TickUpdate_Postfix)
            )
        );

        // Guard against a vanilla null-deref in getAvailableWeddingEvent when a farmer is queued for a
        // wedding but has neither an NPC spouse nor a player spouse at fire time — it would crash the
        // host's day-start update loop. See WeddingEventGuard.
        WeddingEventGuard.Initialize(monitor);
        harmony.Patch(
            original: AccessTools.Method(typeof(Game1), nameof(Game1.getAvailableWeddingEvent)),
            prefix: new HarmonyMethod(
                typeof(WeddingEventGuard),
                nameof(WeddingEventGuard.GetAvailableWeddingEvent_Prefix)
            )
        );

        // Keep the host's main farmhouse at level 0 (internal-only). The only way to upgrade it on
        // a dedicated server is an admin debug command at the console/host chat; block those when
        // they target the host farmhouse. See HostFarmhouseUpgradeGuard (#346).
        HostFarmhouseUpgradeGuard.Initialize(monitor);
        harmony.Patch(
            original: AccessTools.Method(
                typeof(DebugCommands.DefaultHandlers),
                nameof(DebugCommands.DefaultHandlers.HouseUpgrade)
            ),
            prefix: new HarmonyMethod(
                typeof(HostFarmhouseUpgradeGuard),
                nameof(HostFarmhouseUpgradeGuard.BlockHostHouseUpgrade_Prefix)
            )
        );
        harmony.Patch(
            original: AccessTools.Method(
                typeof(DebugCommands.DefaultHandlers),
                nameof(DebugCommands.DefaultHandlers.UpgradeHouse)
            ),
            prefix: new HarmonyMethod(
                typeof(HostFarmhouseUpgradeGuard),
                nameof(HostFarmhouseUpgradeGuard.BlockHostHouseUpgrade_Prefix)
            )
        );
        harmony.Patch(
            original: AccessTools.Method(
                typeof(DebugCommands.DefaultHandlers),
                nameof(DebugCommands.DefaultHandlers.ThisHouseUpgrade)
            ),
            prefix: new HarmonyMethod(
                typeof(HostFarmhouseUpgradeGuard),
                nameof(HostFarmhouseUpgradeGuard.BlockThisHouseUpgrade_Prefix)
            )
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
            Monitor.Log(
                "CRITICAL: Server unexpectedly returned to title screen. Automation disabled.",
                LogLevel.Error
            );
            Monitor.Log(
                "Container may need restart. Check game logs for crash details.",
                LogLevel.Error
            );
        }

        // Reset all automation state regardless (OnSaveLoaded re-enables automation)
        IsAutomating = false;
        clientPaused = false;
        _isShippingMenuActive = false;
        _warpingSleep = false;
        shippingMenuTimeoutTicks = 0;
        _weddingStartTime = null;
        _handledWeddingGate = null;
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
                Game1.player.Equip(
                    ItemRegistry.Create<Hat>($"{ItemRegistry.type_hat}JunimoHat"),
                    Game1.player.hat
                );
                Game1.player.ignoreCollisions = true;

                PreSeedEarlyGameEvents();
                HealUpgradedHostFarmhouse();
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
        AlwaysOnUtil.DrawTextBox(
            5,
            180,
            Game1.dialogueFont,
            $"Press {Config.HotKeyToggleAutoMode} On/Off"
        );
        AlwaysOnUtil.DrawTextBox(5, 300, Game1.dialogueFont, "Visibility On");
        AlwaysOnUtil.DrawTextBox(
            5,
            380,
            Game1.dialogueFont,
            $"Press {Config.HotKeyToggleVisibility} On/Off"
        );
        AlwaysOnUtil.DrawTextBox(
            5,
            540,
            Game1.dialogueFont,
            $"{Game1.server.connectionsCount} Players Online"
        );
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
        HandleOvernightNamingMenu();
        HandleSkippableEvent();
        HandleMinigame();
        alwaysOnServerFestivals.HandleFestivalEvents();
        HandleLevelUpMenu();
        // Third-party mod compatibility (see AlwaysOnModCompat)
        modCompat.HandleSpaceCoreLevelUpMenu();
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
        HandleStuckFarmEvent();
        // Per-tick (not OnOneSecondUpdateTicked) so the host readies the wedding gate promptly: time is
        // frozen during a wedding, which throttles the once-per-second tick to many seconds apart.
        HandleWeddingEvent();

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
            Diagnostics.ModEventLog.Emit(
                "console_command_invoked",
                new
                {
                    command,
                    args,
                    result,
                    error,
                    durationMs = sw.ElapsedMilliseconds,
                }
            );
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

        if (!Config.ShouldCreatePet)
        {
            Monitor.Log($"[Automation] Not creating a pet", LogLevel.Info);
            _petChoiceHandled = true;
            return;
        }

        // Call hostActionNamePet directly via reflection, avoiding the throwaway
        // Event() + namePet() pattern which calls Game1.exitActiveMenu() and
        // would destroy any active menu (e.g. ReadyCheckDialog from sleep).
        Monitor.Log($"[Automation] Creating pet '{Config.PetName}'", LogLevel.Info);
        Helper
            .Reflection.GetMethod(typeof(Event), "hostActionNamePet")
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
        }

        if (!Game1.MasterPlayer.mailReceived.Contains("ccDoorUnlock"))
        {
            Game1.MasterPlayer.mailReceived.Add("ccDoorUnlock");
        }
    }

    /// <summary>
    /// Skip random dialogs (e.g. "no mails").
    /// </summary>
    private void HandleDialogueBox()
    {
        if (
            !IsAutomating
            || Game1.activeClickableMenu == null
            || Game1.activeClickableMenu is not DialogueBox
        )
        {
            return;
        }

        Monitor.Log("Clicking DialogBox", LogLevel.Info);
        Game1.activeClickableMenu.receiveLeftClick(10, 10);
    }

    /// <summary>
    /// Skip events to keep the server unblocked. On a dedicated server no one is watching most
    /// cutscenes, so we skip them — including non-skippable intro events that would otherwise block
    /// clients from joining.
    ///
    /// Two deliberate exceptions, each handled elsewhere so players can actually experience them:
    /// festivals (<see cref="AlwaysOnServerFestivals"/>) and weddings (<see cref="HandleWeddingEvent"/>).
    /// A wedding plays a ceremony for everyone in the session and ends with a "wait for all players"
    /// ready gate that counts the host; force-skipping it jumps the host past that gate so it never
    /// readies, and the clients hang at "Waiting for players (N/M)" forever (the reported freeze).
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

        // Weddings are non-skippable and play through for everyone; HandleWeddingEvent lets the host
        // participate in the ceremony's wait gate instead of skipping past it.
        if (Game1.CurrentEvent.isWedding)
        {
            return;
        }

        bool skipped = Game1.CurrentEvent.skipped;
        bool finished = Helper
            .Reflection.GetField<bool>(Game1.CurrentEvent, "eventFinished")
            .GetValue();

        if (!skipped && !finished)
        {
            Monitor.Log(
                $"Skipping event (skippable={Game1.CurrentEvent.skippable})",
                LogLevel.Info
            );
            Game1.CurrentEvent.skipEvent();
            Game1.CurrentEvent.receiveMouseClick(1, 2);
        }
    }

    // Wall-clock start of the CURRENT ceremony's wait gate (null when none). Wall-clock, NOT game-time:
    // time is frozen during a wedding, so a game-time accumulator would never reach the backstop.
    private DateTime? _weddingStartTime;

    // The wait gate of the ceremony the host has already readied+ended, so it isn't re-handled on the
    // ticks before the event tears down. Keyed on the gate id (weddingEnd<farmerId>), NOT a bare bool:
    // on a multi-wedding day the host fires the next ceremony immediately, often before CurrentEvent
    // ever reads null, so a bool latch would suppress every wedding after the first. A new gate id is a
    // new ceremony to handle. Null when no wedding is active or none handled yet this ceremony.
    private string? _handledWeddingGate;

    // Backstop: if the other players never ready the gate (e.g. a client stuck on its own copy), end
    // the host's ceremony anyway after this long so it can't hold the server open. Only fires once a
    // co-participant has joined the gate (see HandleWeddingEvent).
    private const double WeddingEndBackstopSeconds = 20.0;

    /// <summary>
    /// Complete a wedding ceremony on the host when the other players are ready, instead of skipping it
    /// — the fix for the deadlock where the host raced past the ceremony's "wait for players" step and
    /// clients hung at "Waiting for players (N/M)" forever (time is frozen during a wedding, so the whole
    /// server appeared stuck).
    ///
    /// <para>
    /// A wedding is non-skippable and ends with a <c>waitForOtherPlayers weddingEnd&lt;farmerId&gt;</c>
    /// step that counts the host. The clients run and watch their own ceremony copies (the wedding isn't
    /// a synced event — each instance runs its own via <c>checkForEvents</c>) and ready the gate when
    /// they reach the step. We don't make the host *play through* the dialogue-heavy cutscene: its
    /// <c>DialogueBox</c> typing/safetyTimer advance in <c>update()</c> (the Update path,
    /// <c>Game1._update</c> → <c>updateActiveMenu</c> — NOT draw-coupled), but the safetyTimer is 750ms
    /// (not 0 — <c>IsDedicatedHost</c> is false here, <c>DialogueBox.cs:462</c>) and each <c>speak</c>
    /// box has its own pacing, so a full ~10-box ceremony takes tens of seconds — too slow/fragile to be
    /// the host's job. So the host instead readies its slot the moment the other
    /// players are ready — the same "continue when others do" move <see cref="AlwaysOnServerFestivals"/>
    /// makes for <c>festivalStart</c>/<c>festivalEnd</c> — then ends its own copy via
    /// <c>endBehaviors(["End","wedding"])</c>.
    /// </para>
    ///
    /// <para>
    /// <c>endBehaviors(["End","wedding"])</c> is the engine's own end-the-wedding idiom: the script's
    /// <c>end wedding</c> command runs exactly this (<c>Event.DefaultCommands.End</c> → <c>endBehaviors</c>),
    /// and <c>skipEvent</c> uses the same <c>endBehaviors(["End",&lt;tag&gt;])</c> form for every other
    /// special-ending event. It runs the wedding's real end behaviours — warps the spouse NPC to the
    /// couple's home and queues the after-wedding dialogue (Event.cs <c>endBehaviors</c> "wedding" case)
    /// — which a bare <c>skipEvent()</c> (the wedding's default branch passes no tag) would skip.
    /// </para>
    ///
    /// <para>
    /// Disconnecting watchers don't block the gate (<c>ServerReadyCheck</c> excludes them), and
    /// lobby/unauthenticated players are excluded from every ready-check by
    /// <c>LobbyService.IsFarmerRequired_Postfix</c>, so the wait step needs no special-casing here.
    /// </para>
    ///
    /// <para>
    /// <b>Multiple weddings on one day.</b> The game queues every eligible couple into
    /// <c>Game1.weddingsToday</c> and fires them one ceremony at a time (<c>getAvailableWeddingEvent</c>
    /// pops one per <c>checkForEvents</c>). Vanilla only runs <c>checkForEvents</c> on location entry, so
    /// after the host finishes ceremony 1 and warps to the Farm porch, the single post-warp
    /// <c>resetForPlayerEntry</c> can miss the next wedding (it races the teardown that clears
    /// <c>eventUp</c>/<c>CurrentEvent</c>), leaving the host standing still with a queued wedding it never
    /// starts — and the clients hang on ceremony 2's gate forever. So when no wedding is active but one is
    /// still queued, the host re-runs <c>checkForEvents</c> itself to deterministically start the next one
    /// (idempotent — vanilla's own guard prevents a double-start). The handled-gate latch is keyed on the
    /// gate id, not a bool, because the host can flip straight from one ceremony to the next without
    /// <c>CurrentEvent</c> ever reading null.
    /// </para>
    /// </summary>
    private void HandleWeddingEvent()
    {
        var ev = Game1.CurrentEvent;
        if (!IsAutomating || ev == null || !ev.isWedding)
        {
            _weddingStartTime = null;
            _handledWeddingGate = null;
            // No wedding playing, but a queued one may be waiting to fire. Vanilla starts weddings only
            // on location entry, so after one ceremony ends the next can stall unstarted (see method
            // summary); kick checkForEvents so the host deterministically begins the next one.
            StartNextQueuedWeddingIfIdle();
            return;
        }

        var gate = WeddingEvent.ExtractWaitGate(ev);
        if (gate == null)
        {
            return; // wait step not in this event's commands (unexpected) — nothing to ready
        }

        // Already readied+ended this exact ceremony — wait for it to tear down (or for the next
        // wedding's distinct gate to appear). Keying on the gate, not a bool, is what lets the host
        // hand off to the next same-day wedding: its weddingEnd<farmerId> differs, so it's not skipped.
        if (gate == _handledWeddingGate)
        {
            return;
        }

        // A new ceremony (first, or the next one on a multi-wedding day): start its own backstop window.
        if (_weddingStartTime == null)
        {
            _weddingStartTime = DateTime.UtcNow;
        }

        // Complete the ceremony once the other players are ready on the gate (the festival "continue
        // when others do" pattern), or once the wall-clock backstop elapses so a client that never
        // readies can't stall us. The backstop must NOT fire until a co-participant has joined the gate
        // (required > 1): readying while the host is the only required farmer would lock the gate at 1/1,
        // and a client reaching its wait step later would find it already finished without it.
        var othersReady = OthersReadyForWedding(gate);
        var backstopElapsed =
            Game1.netReady.GetNumberRequired(gate) > 1
            && (DateTime.UtcNow - _weddingStartTime.Value).TotalSeconds
                >= WeddingEndBackstopSeconds;

        if (!othersReady && !backstopElapsed)
        {
            return;
        }

        // Ready the host's slot of the gate so the clients' wait completes...
        Game1.netReady.SetLocalReady(gate, true);

        // ...then end the host's own copy. endBehaviors(["End","wedding"]) runs the real wedding end
        // behaviours — warps the spouse NPC to the couple's Farm porch, queues the after-wedding marriage
        // dialogue (Event.cs "wedding" case), and calls exitEvent (sets eventOver, arms a fade, records
        // the event's exit warp). The explicit "wedding" tag is required: a bare skipEvent() hits the
        // wedding's default branch (endBehaviors() with no tag) and skips the spouse warp.
        ev.endBehaviors(new[] { "End", "wedding" }, Game1.currentLocation);

        // exitEvent only ARMS that fade + warp; the engine resolves them later via onFadeToBlackComplete.
        // On the render-suppressed host that handler stalls — the queued marriage dialogue surfaces as a
        // DialogueBox and the fade sits at full alpha — so the host strands on the ceremony's Town temp
        // map, fully black, dialogue open (the reported "stuck in the black wedding fadeout"). Tear the
        // ceremony down here instead, the way vanilla skipEvent() cleans up an event no one watches: drop
        // the menu/dialogue/fade, then eventFinished() to end the event and warp the host off the temp
        // map. Clearing the fade BEFORE eventFinished() matters — eventFinished()'s warp resolves in place
        // when no fade is pending; left armed, on a back-to-back multi-wedding day it would defer to the
        // fade-complete handler and re-enter it on the next ceremony's half-torn-down event, NREing the
        // update loop. The marriage data is already recorded, so dropping the after-wedding dialogue is
        // lossless.
        Game1.exitActiveMenu();
        Game1.dialogueUp = false;
        Game1.dialogueTyping = false;
        Game1.pauseTime = 0f;
        Game1.player.Halt();
        Game1.fadeClear();
        if (Game1.eventOver)
        {
            Game1.eventFinished();
        }
        // eventFinished() leaves the screen black (fadeToBlackAlpha = 1) expecting a fade-in; force it
        // clear so the host doesn't sit on a black screen.
        Game1.fadeClear();
        Game1.fadeToBlackAlpha = 0f;

        // eventFinished() warped the host onto the open Farm map: the wedding's endBehaviors "wedding"
        // case sets the exit warp from getHomeOfFarmer(Game1.player).getPorchStandingSpot() (Event.cs
        // "wedding"), and on the host Game1.player's home is the main FarmHouse, so the host lands at the
        // farmhouse-porch tile on the Farm rather than in its FarmHouse — out of its normal hidden idle
        // spot. Return it home, but ONLY once no wedding is still queued: on a multi-wedding day the next
        // ceremony is started by StartNextQueuedWeddingIfIdle, which needs the host on a non-temporary
        // location (FarmHouse won't trigger the queued Farm/Town wedding), so warping home between
        // ceremonies would strand the next wedding unstarted. getAvailableWeddingEvent pops the running
        // ceremony's farmer from weddingsToday before the event runs (Game1.cs:6096), so a remaining
        // Count > 0 reliably means "another wedding still queued".
        if (Game1.weddingsToday is not { Count: > 0 })
        {
            WarpHostHomeAfterWeddings();
        }

        // Mark this ceremony handled and clear the timer so the next wedding's gate starts a fresh
        // backstop window (CurrentEvent may flip straight to it without ever reading null).
        _handledWeddingGate = gate;
        _weddingStartTime = null;
        Monitor.Log(
            $"Readied wedding wait gate [{gate}] "
                + $"({(othersReady ? "other players ready" : "wall-clock backstop")}) and finished the "
                + "host's ceremony copy (spouse warped home, host event ended, fade cleared).",
            LogLevel.Info
        );
    }

    /// <summary>
    /// Return the host to its FarmHouse idle spot after the day's last wedding. <c>eventFinished()</c>
    /// leaves the host on the open Farm map (the wedding exit warp targets the host's farmhouse porch via
    /// <c>getHomeOfFarmer(Game1.player)</c>), but the host is meant to stay hidden in its FarmHouse — the
    /// same place <see cref="HandleAutoSleep"/> keeps it. Reuses that handler's home-warp idiom:
    /// <c>getLocationRequest(home)</c> + <c>warpFarmer</c> into the FarmHouse, then snap to the bed spot
    /// and <c>Halt()</c>. Master-only and FarmHouse-targeted (no-ops if home doesn't resolve to one).
    /// </summary>
    private void WarpHostHomeAfterWeddings()
    {
        if (!Game1.IsMasterGame)
        {
            return;
        }

        var home = Game1.player.homeLocation.Value;
        if (string.IsNullOrEmpty(home))
        {
            return;
        }

        // Already home (a single-tile-warp engine edge case): just snap to the bed and stop.
        if (
            Game1.currentLocation is FarmHouse here
            && string.Equals(here.NameOrUniqueName, home, StringComparison.OrdinalIgnoreCase)
        )
        {
            Game1.player.position.Set(Utility.PointToVector2(here.GetPlayerBedSpot()) * 64f);
            Game1.player.Halt();
            return;
        }

        var req = Game1.getLocationRequest(home);
        req.OnWarp += () =>
        {
            if (Game1.currentLocation is FarmHouse fh)
            {
                Game1.player.position.Set(Utility.PointToVector2(fh.GetPlayerBedSpot()) * 64f);
            }
            Game1.player.Halt();
            // warpFarmer arms a fade-to-black; on the render-suppressed host the fade-in handler is
            // draw-coupled and stalls (same reason the teardown above clears the fade by hand), so clear
            // it on arrival or the host sits on a black screen in its FarmHouse.
            Game1.fadeClear();
            Game1.fadeToBlackAlpha = 0f;
        };
        // Coords are any valid tile inside the target FarmHouse; the OnWarp callback snaps to the bed
        // spot. Mirrors HandleAutoSleep's home-warp (and DedicatedServer.cs:415). Issued right after
        // eventFinished()'s own Farm-exit warpFarmer — performWarpFarmer just overwrites the single
        // Game1.locationRequest, so this supersedes the Farm exit and the host warps straight home
        // without first landing on the open Farm.
        Game1.warpFarmer(req, 5, 9, Game1.player.FacingDirection);
        Monitor.Log($"Warped host home to {home} after the day's last wedding.", LogLevel.Info);
    }

    /// <summary>
    /// When no event is running but a wedding is still queued for today, re-run the host's
    /// <c>checkForEvents</c> to start the next ceremony. Vanilla only fires weddings on location entry
    /// (<c>resetForPlayerEntry</c>), so on a multi-wedding day the second ceremony can stall unstarted
    /// after the first ends — see <see cref="HandleWeddingEvent"/>. Guard conditions mirror vanilla's
    /// own wedding-start branch (GameLocation.checkForEvents, decompiled GameLocation.cs:15620) so this
    /// only fires exactly when vanilla would: no event up, a wedding queued, host on a non-temporary
    /// location. <c>checkForEvents</c> is idempotent here — its guard no-ops if a wedding is already up.
    /// </summary>
    private static void StartNextQueuedWeddingIfIdle()
    {
        if (
            !Game1.eventUp
            && Game1.weddingsToday is { Count: > 0 }
            && Game1.CurrentEvent == null
            && Game1.currentLocation is { IsTemporary: false }
        )
        {
            Game1.currentLocation.checkForEvents();
        }
    }

    /// <summary>
    /// True once every other connected player has readied the given wedding wait gate (ready =
    /// required − 1, host is the only one missing), mirroring <c>DedicatedServer.CheckOthersReady</c>.
    /// The <c>ready &gt; 0</c> guard prevents excluded players trivially satisfying it.
    /// </summary>
    private static bool OthersReadyForWedding(string gate)
    {
        int ready = Game1.netReady.GetNumberReady(gate);
        if (ready <= 0 || Game1.netReady.IsReady(gate))
        {
            return false;
        }

        return ready >= Game1.netReady.GetNumberRequired(gate) - 1;
    }

    /// <summary>
    /// Auto-name the baby during the overnight BirthingEvent / PlayerCoupleBirthingEvent. Those
    /// events open a <see cref="NamingMenu"/> and only complete once a name is submitted; the menu
    /// is not a DialogueBox, so <see cref="HandleDialogueBox"/> can't dismiss it, and a headless
    /// host has no one to type a name — the overnight transition hangs forever. We supply a vanilla
    /// random name (the same source the menu pre-fills), which runs the event's real side effects
    /// (creates the Child NPC) and lets the new day proceed. Scoped to an active overnight farm
    /// event so no other NamingMenu use is touched.
    /// </summary>
    private void HandleOvernightNamingMenu()
    {
        if (!IsAutomating || Game1.farmEvent == null)
        {
            return;
        }

        if (Game1.activeClickableMenu is not NamingMenu naming)
        {
            return;
        }

        var name = Dialogue.randomName();
        naming.textBox.Text = name;
        naming.textBoxEnter(naming.textBox);
        Monitor.Log($"Auto-named overnight baby '{name}' to unblock the host.", LogLevel.Info);
    }

    // Tracks how long the current Game1.farmEvent has been active (game-time, framerate-independent).
    // Reset when farmEvent clears. Used by HandleStuckFarmEvent to surface modded/unknown overnight
    // events that never complete on a headless host.
    private double _farmEventActiveMs;
    private bool _stuckFarmEventLogged;

    /// <summary>
    /// Watchdog for overnight farm events that never complete on a headless host. The known
    /// headless-hangers are handled directly — <see cref="QiPlaneEventOverrides"/> completes the Mr.
    /// Qi mystery box, and <see cref="HandleOvernightNamingMenu"/> auto-names births — so those
    /// resolve before this fires. For an unknown/modded <c>FarmEvent</c> still stuck well past any
    /// vanilla event's natural duration, log once at Warn with its type so we can see what's hanging.
    /// We deliberately do NOT force-complete unknown types: skipping their tickUpdate would bypass
    /// their makeChangesToLocation side effects and could corrupt the save.
    /// </summary>
    private void HandleStuckFarmEvent()
    {
        if (Game1.farmEvent == null)
        {
            _farmEventActiveMs = 0.0;
            _stuckFarmEventLogged = false;
            return;
        }

        if (!Game1.IsMasterGame)
        {
            return;
        }

        _farmEventActiveMs += Game1.currentGameTime?.ElapsedGameTime.TotalMilliseconds ?? 0.0;

        // Well past any vanilla overnight event's natural duration — anything still stuck here is an
        // event we don't auto-complete.
        const double StuckThresholdMs = 25000.0;
        if (_stuckFarmEventLogged || _farmEventActiveMs < StuckThresholdMs)
        {
            return;
        }

        _stuckFarmEventLogged = true;
        var type = Game1.farmEvent.GetType().Name;
        Monitor.Log(
            $"Overnight farm event '{type}' has not completed after {_farmEventActiveMs / 1000.0:0}s "
                + "of game-time — the host may be stuck. Not force-completing it, since skipping its "
                + "tickUpdate would bypass any makeChangesToLocation side effects.",
            LogLevel.Warn
        );
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
        if (Game1.otherFarmers.Count == 0)
        {
            return;
        }

        if (_warpingSleep)
        {
            return;
        }

        if (!OthersReadyForSleep())
        {
            return;
        }

        var home = Game1.player.homeLocation.Value;

        if (
            Game1.currentLocation is FarmHouse here
            && string.Equals(here.NameOrUniqueName, home, StringComparison.OrdinalIgnoreCase)
        )
        {
            Monitor.Log(
                $"Host is home, sleeping in place ({here.NameOrUniqueName})",
                LogLevel.Info
            );
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
                Monitor.Log(
                    $"Sleep warp landed in unexpected location: {Game1.currentLocation?.NameOrUniqueName}",
                    LogLevel.Warn
                );
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
        if (ready <= 0)
        {
            return false;
        }

        if (Game1.netReady.IsReady("sleep"))
        {
            return false;
        }

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
        if (AlwaysOnUtil.IsReadyCheckActive())
        {
            return;
        }

        // Nothing to collect — don't open the "mailbox is empty" dialog. It
        // briefly steals activeClickableMenu for no benefit and widens the race
        // window if a ReadyCheckDialog shows up a tick later.
        if (Game1.mailbox.Count == 0)
        {
            return;
        }

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

        bool hasCommunityCenterConditions =
            Game1.player.eventsSeen.Contains("191393")
            || !Game1.player.mailReceived.Contains("ccCraftsRoom")
            || !Game1.player.mailReceived.Contains("ccVault")
            || !Game1.player.mailReceived.Contains("ccFishTank")
            || !Game1.player.mailReceived.Contains("ccBoilerRoom")
            || !Game1.player.mailReceived.Contains("ccPantry")
            || !Game1.player.mailReceived.Contains("ccBulletin");

        if (hasCommunityCenterConditions)
        {
            return;
        }

        CommunityCenter communityCenter =
            Game1.getLocationFromName("CommunityCenter") as CommunityCenter;
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
    /// Reset a host farmhouse that a prior build left upgraded back to level 0 (the host farmhouse
    /// is internal-only, see <see cref="HostFarmhouseUpgradeGuard"/>). No-op for new/healthy saves.
    ///
    /// TRANSITIONAL: remove after 2026-09-01 once affected saves have self-healed (#346).
    /// </summary>
    private void HealUpgradedHostFarmhouse()
    {
        if (Game1.player.HouseUpgradeLevel == 0)
        {
            return;
        }

        HostFarmhouseUpgradeGuard.ResetHostFarmhouseToLevelZero();
        Monitor.Log(
            "Reset host farmhouse to level 0 (internal-only); cleared a stale upgrade from #346.",
            LogLevel.Info
        );
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
        if (!Config.LockPlayerChests)
        {
            return;
        }

        if (!Game1.IsMasterGame)
        {
            return;
        }

        var farm = Game1.getFarm();
        if (farm == null)
        {
            return;
        }

        var onlineFarmers = Game1.getOnlineFarmers();

        foreach (var building in farm.buildings)
        {
            if (!building.isCabin)
            {
                continue;
            }

            if (building.GetIndoors() is not Cabin cabin)
            {
                continue;
            }

            var owner = cabin.owner;
            if (owner != null && !owner.isUnclaimedFarmhand && onlineFarmers.Contains(owner))
            {
                continue;
            }

            if (cabin.fridge.Value is { } fridge && !fridge.mutex.IsLocked())
            {
                fridge.mutex.RequestLock();
            }
            foreach (var chest in cabin.objects.Values.OfType<Chest>())
            {
                if (!chest.mutex.IsLocked())
                {
                    chest.mutex.RequestLock();
                }
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
        if (!Config.LockPlayerChests)
        {
            return;
        }

        if (!Game1.IsMasterGame)
        {
            return;
        }

        if (owner == null)
        {
            return;
        }

        var farm = Game1.getFarm();
        if (farm == null)
        {
            return;
        }

        foreach (var building in farm.buildings)
        {
            if (!building.isCabin)
            {
                continue;
            }

            if (building.GetIndoors() is not Cabin cabin)
            {
                continue;
            }

            if (cabin.owner != owner)
            {
                continue;
            }

            if (cabin.inventoryMutex.IsLockHeld())
            {
                cabin.inventoryMutex.ReleaseLock();
            }

            if (cabin.fridge.Value is { } fridge && fridge.mutex.IsLockHeld())
            {
                fridge.mutex.ReleaseLock();
            }

            foreach (var chest in cabin.objects.Values.OfType<Chest>())
            {
                if (chest.mutex.IsLockHeld())
                {
                    chest.mutex.ReleaseLock();
                }
            }
            return;
        }
    }

    private void OnPeerConnected(object sender, PeerConnectedEventArgs e)
    {
        if (!Config.LockPlayerChests)
        {
            return;
        }

        if (!Game1.IsMasterGame)
        {
            return;
        }

        var farmer = Game1.GetPlayer(e.Peer.PlayerID, onlyOnline: true);
        if (farmer != null)
        {
            ReleaseOnlineFarmhandStorage(farmer);
        }
    }

    private void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)
    {
        // Re-seed across all cabins; a disconnect can leave any subset offline,
        // and the iteration is cheap (one pass over farm.buildings).
        LockOfflineFarmhandStorage();
    }

    private void OnObjectListChanged(object sender, ObjectListChangedEventArgs e)
    {
        if (!Config.LockPlayerChests)
        {
            return;
        }

        if (!Game1.IsMasterGame)
        {
            return;
        }

        if (e.Location is not Cabin cabin)
        {
            return;
        }

        var owner = cabin.owner;
        var ownerOffline =
            owner == null || owner.isUnclaimedFarmhand || !Game1.getOnlineFarmers().Contains(owner);
        if (!ownerOffline)
        {
            return;
        }

        foreach (var added in e.Added)
        {
            if (added.Value is Chest chest && !chest.mutex.IsLocked())
            {
                chest.mutex.RequestLock();
            }
        }
    }
}
