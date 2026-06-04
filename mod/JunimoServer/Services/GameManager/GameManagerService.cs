using JunimoServer.Services.GameCreator;
using JunimoServer.Services.GameLoader;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Services.Settings;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Threading.Tasks;

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

        public GameManagerService(GameCreatorService gameCreator, GameLoaderService gameLoader, ServerSettingsLoader settings, PersistentOptions options, IModHelper helper, IMonitor monitor) : base(helper, monitor)
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
            IsNewGamePending = true;
            _pendingNewGameConfig = config;
            _newGameCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gameStarted = false;
            _titleLaunched = false;
            _healthCheckTimer = 0;
            _lastNullCodeTime = null;
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
            _pendingReload = true;
            _reloadCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gameStarted = false;
            _titleLaunched = false;
            _healthCheckTimer = 0;
            _lastNullCodeTime = null;
            Game1.ExitToTitle();
            return _reloadCompletion.Task;
        }

        public override void Entry()
        {
            // Unsubscribe first to avoid duplicate bindings
            Helper.Events.Display.RenderedActiveMenu -= OnRenderedActiveMenu;
            Helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondTicked;

            // Subscribe to the events
            Helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            Helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondTicked;
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
                try
                {
                    Monitor.Log("Reloading current world from API request", LogLevel.Info);
                    _settings.Reload();
                    _options.RecaptureAndSync(_settings);
                    if (!_gameLoaderService.LoadSave())
                    {
                        throw new InvalidOperationException("LoadSave returned false (no loadable save found)");
                    }
                    _reloadCompletion?.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"World reload failed: {ex}", LogLevel.Error);
                    _reloadCompletion?.TrySetException(ex);
                }
                _reloadCompletion = null;
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
                    _newGameCompletion?.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"New game creation failed: {ex}", LogLevel.Error);
                    _newGameCompletion?.TrySetException(ex);
                }
                _newGameCompletion = null;
                return;
            }

            if (Env.ForceNewDebugGame)
            {
                var config = NewGameConfig.FromSettings(_settings);
                _gameCreatorService.CreateNewGame(config);
                return;
            }

            if (_gameLoaderService.HasLoadableSave())
                _gameLoaderService.LoadSave();
            else
                _gameCreatorService.CreateNewGameFromConfig();
        }

        private bool HasDurationPassedSinceLastNullCode(TimeSpan duration)
        {
            return _lastNullCodeTime.HasValue && (DateTime.Now - _lastNullCodeTime.Value) >= duration;
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
                            "Network unreachable for 2+ minutes; exiting for container restart. " +
                            "Mid-day state since last sleep will be lost; restart will reload last save.",
                            LogLevel.Warn);
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
