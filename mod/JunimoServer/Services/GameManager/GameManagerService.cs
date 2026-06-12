using System;
using System.Threading.Tasks;
using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Settings;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace JunimoServer.Services.GameManager
{
    class GameManagerService : ModService
    {
        private readonly GameCreatorService _gameCreatorService;
        private readonly GameLoaderService _gameLoaderService;
        private readonly ServerSettingsLoader _settings;
        private readonly PersistentOptions _options;

        private bool _titleLaunched = false;
        private bool _gameStarted = false;
        private int _healthCheckTimer = 0;
        private DateTime? _lastNullCodeTime = null;

        // New game creation via API
        private NewGameConfig? _pendingNewGameConfig;
        private TaskCompletionSource<bool>? _newGameCompletion;

        // Reload-current-world via API (apply settings + reload, no process restart)
        private bool _pendingReload;
        private TaskCompletionSource<bool>? _reloadCompletion;

        // Set by OnSaveLoaded; consumed on the next UpdateTicked to resolve a pending reload/
        // newgame completion after all SaveLoaded handlers have run. See OnUpdateTicked.
        private bool _saveLoadedSinceRequest;

        /// <summary>
        /// Whether a new game creation has been requested via the API.
        /// Checked by AlwaysOnServer to distinguish intentional returns to title.
        /// </summary>
        public static bool IsNewGamePending { get; private set; }

        /// <summary>
        /// Static instance for access from ApiService without changing visibility.
        /// Set in the constructor.
        /// </summary>
        internal static GameManagerService? Instance { get; private set; }

        public GameManagerService(
            GameCreatorService gameCreator,
            GameLoaderService gameLoader,
            ServerSettingsLoader settings,
            PersistentOptions options,
            IModHelper helper,
            IMonitor monitor
        )
            : base(helper, monitor)
        {
            _gameCreatorService = gameCreator;
            _gameLoaderService = gameLoader;
            _settings = settings;
            _options = options;
            Instance = this;
        }

        /// <summary>
        /// Requests a new game creation with the specified config.
        /// MUST be called on the game thread (via RunOnGameThreadAsync).
        /// Returns a Task that completes when the new game is ready.
        /// </summary>
        public Task RequestNewGame(NewGameConfig config)
        {
            // One load operation in flight at a time: reject if a /reload is already pending.
            // Without this, both completion TCSs would be armed at once and OnUpdateTicked's
            // first SaveLoaded would resolve both, returning a false success to whichever
            // operation never ran. Game-thread serialized, so the check needs no lock.
            if (_reloadCompletion != null)
            {
                return Task.FromException(
                    new InvalidOperationException(
                        "A reload is already in progress; cannot start a new game."
                    )
                );
            }

            IsNewGamePending = true;
            _pendingNewGameConfig = config;
            _newGameCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _gameStarted = false;
            _titleLaunched = false;
            _healthCheckTimer = 0;
            _lastNullCodeTime = null;
            // Clear any stale flag from a prior load (e.g. boot), so the completion resolves
            // only after THIS request's SaveLoaded — not on the first tick from an old one.
            _saveLoadedSinceRequest = false;
            Game1.ExitToTitle();
            return _newGameCompletion.Task;
        }

        /// <summary>
        /// Re-reads server-settings.json and reloads the current world in-process
        /// (no container restart). Applies any runtime settings change — notably a
        /// CabinStrategy switch, which the cabin manager migrates on the next save load.
        /// MUST be called on the game thread (via RunOnGameThreadAsync).
        /// Returns a Task that completes when the world has finished reloading.
        /// </summary>
        public Task RequestReloadSave()
        {
            // One load operation in flight at a time: reject if a /newgame is already pending
            // (see RequestNewGame for why a second concurrent op would falsely succeed).
            if (_newGameCompletion != null)
            {
                return Task.FromException(
                    new InvalidOperationException(
                        "A new game is already in progress; cannot reload."
                    )
                );
            }

            // Coalesce overlapping requests: a second /reload while one is in flight would
            // otherwise replace _reloadCompletion and orphan the first caller until its 120s
            // timeout. Always invoked on the game thread (via RunOnGameThreadAsync), so this
            // check-and-act needs no lock. Returns the in-flight task to the late caller.
            if (_pendingReload && _reloadCompletion != null)
            {
                return _reloadCompletion.Task;
            }

            // Reload returns to title intentionally. Flag it so AlwaysOn.OnReturnedToTitle
            // treats this as expected instead of logging a critical (test-poisoning) error.
            IsNewGamePending = true;
            _pendingReload = true;
            _reloadCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _gameStarted = false;
            _titleLaunched = false;
            _healthCheckTimer = 0;
            _lastNullCodeTime = null;
            // Clear any stale flag from a prior load, so the completion resolves only after
            // THIS reload's SaveLoaded — not on the first tick from an old one.
            _saveLoadedSinceRequest = false;
            Game1.ExitToTitle();
            return _reloadCompletion.Task;
        }

        public override void Entry()
        {
            // Unsubscribe first to avoid duplicate bindings
            Helper.Events.Display.RenderedActiveMenu -= OnRenderedActiveMenu;
            Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondTicked;
            Helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
            Helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;

            // Subscribe to the events
            Helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            Helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondTicked;
            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            _saveLoadedSinceRequest = true;
        }

        // Resolve a pending /reload or /newgame completion only AFTER SaveLoaded has fired —
        // by then every SaveLoaded handler (cabin migration/sync/sweep, EnsureAtLeastXCabins)
        // has run this tick, so a post-reload snapshot reflects the final world. LoadSave()
        // /CreateNewGame() only arm the loader (SaveGame.Load sets Game1.currentLoader; the
        // world loads over later ticks), so resolving when they return would race that work.
        // Next-tick (not in OnSaveLoaded) so the resolve never depends on SaveLoaded subscriber
        // order — all handlers run synchronously in the firing tick. The completion TCSs are
        // non-null only while a request is pending, so a stray SaveLoaded is a no-op here.
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!_saveLoadedSinceRequest)
            {
                return;
            }

            _saveLoadedSinceRequest = false;

            _reloadCompletion?.TrySetResult(true);
            _reloadCompletion = null;
            _newGameCompletion?.TrySetResult(true);
            _newGameCompletion = null;
        }

        private void OnOneSecondTicked(object sender, OneSecondUpdateTickedEventArgs e)
        {
            ConditionallyStartGame();

            // Only run healthcheck after game has started (prevents false negatives during init)
            if (_gameStarted)
            {
                RunHealthCheck();
            }
        }

        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            if (Game1.activeClickableMenu is TitleMenu && !_titleLaunched)
            {
                _titleLaunched = true;
            }
        }

        private void ConditionallyStartGame()
        {
            if (_gameStarted)
            {
                return;
            }

            // Also check the menu directly. RenderedActiveMenu doesn't fire when
            // rendering is disabled (SuppressDraw), so _titleLaunched stays false
            // after ExitToTitle. Without this, /newgame always times out (504).
            if (!_titleLaunched && Game1.activeClickableMenu is TitleMenu)
            {
                _titleLaunched = true;
            }

            if (!_titleLaunched)
            {
                return;
            }

            _gameStarted = true;

            // If a reload was requested via the API, re-read settings and reload the
            // current world. RecaptureAndSync makes a CabinStrategy change detectable
            // by the cabin manager on the SaveLoaded that LoadSave triggers.
            if (_pendingReload)
            {
                _pendingReload = false;
                // Clear here (not only on success) so a failed reload doesn't leave the
                // intent flag stuck true, which would mask a later genuine crash-to-title.
                IsNewGamePending = false;
                try
                {
                    Monitor.Log("Reloading current world from API request", LogLevel.Info);
                    _settings.Reload();
                    _options.RecaptureAndSync(_settings);
                    if (!_gameLoaderService.LoadSave())
                    {
                        throw new InvalidOperationException(
                            "LoadSave returned false (no loadable save found)"
                        );
                    }
                    // Success is signalled from OnUpdateTicked once SaveLoaded has fired and run
                    // the migration/sync/sweep — not here, where the loader has only been armed.
                }
                catch (Exception ex)
                {
                    // Reset startup so the next tick retries ConditionallyStartGame instead
                    // of early-returning on _gameStarted and parking the server at title.
                    _gameStarted = false;
                    Monitor.Log($"World reload failed: {ex}", LogLevel.Warn);
                    // No SaveLoaded fires on a failed load, so fault the TCS here or the
                    // /reload HTTP call hangs to its 120s timeout.
                    _reloadCompletion?.TrySetException(ex);
                    _reloadCompletion = null;
                }
                return;
            }

            // If a new game was requested via the API, use the provided config
            if (_pendingNewGameConfig != null)
            {
                var config = _pendingNewGameConfig;
                _pendingNewGameConfig = null;
                IsNewGamePending = false;
                try
                {
                    Monitor.Log($"Creating new game from API request: {config}", LogLevel.Info);
                    _gameCreatorService.CreateNewGame(config);
                    // Success is signalled from OnUpdateTicked once SaveLoaded has fired — see
                    // the reload branch. CreateNewGame's loadForNewGame also loads over later ticks.
                }
                catch (Exception ex)
                {
                    // Warn, not Error: LogLevel.Error trips ServerContainer's ERROR/FATAL test-
                    // poison detector. The faulted TCS already surfaces the failure as a 500.
                    Monitor.Log($"New game creation failed: {ex}", LogLevel.Warn);
                    // No SaveLoaded fires on a failed create, so fault the TCS here.
                    _newGameCompletion?.TrySetException(ex);
                    _newGameCompletion = null;
                }
                return;
            }

            if (Env.ForceNewDebugGame)
            {
                var config = NewGameConfig.FromSettings(_settings);
                _gameCreatorService.CreateNewGame(config);
                return;
            }

            if (_gameLoaderService.HasLoadableSave())
            {
                _gameLoaderService.LoadSave();
            }
            else
            {
                _gameCreatorService.CreateNewGameFromConfig();
            }
        }

        private bool HasDurationPassedSinceLastNullCode(TimeSpan duration)
        {
            return _lastNullCodeTime.HasValue
                && (DateTime.Now - _lastNullCodeTime.Value) >= duration;
        }

        private void RunHealthCheck()
        {
            if (_healthCheckTimer > 0)
            {
                _healthCheckTimer--;
                return;
            }

            _healthCheckTimer = Env.HealthCheckSeconds;

            if (Game1.server != null)
            {
                if (Game1.server.canAcceptIPConnections())
                {
                    Monitor.Log("Healthcheck ✓", LogLevel.Info);
                    _lastNullCodeTime = null;
                }
                else
                {
                    Monitor.Log("Healthcheck ✗", LogLevel.Info);
                    _lastNullCodeTime ??= DateTime.Now;

                    if (HasDurationPassedSinceLastNullCode(TimeSpan.FromMinutes(2)))
                    {
                        Monitor.Log(
                            "Network unreachable for 2+ minutes; exiting for container restart. "
                                + "Mid-day state since last sleep will be lost; restart will reload last save.",
                            LogLevel.Warn
                        );
                        Environment.Exit(1);
                    }
                }
            }
            else
            {
                Monitor.Log("Healthcheck ✗", LogLevel.Info);
            }
        }
    }
}
