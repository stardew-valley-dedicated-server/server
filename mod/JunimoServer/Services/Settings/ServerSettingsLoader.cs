using JunimoServer.Services.CabinManager;
using Newtonsoft.Json;
using StardewModdingAPI;
using System;
using System.IO;

namespace JunimoServer.Services.Settings
{
    /// <summary>
    /// Loads server settings from a JSON file and exposes parsed, typed values.
    /// If the settings file does not exist, creates one with all defaults.
    /// </summary>
    public class ServerSettingsLoader
    {
        private readonly ServerSettings _settings;
        private readonly string _settingsPath;
        private readonly IMonitor _monitor;

        public ServerSettingsLoader(IModHelper helper, IMonitor monitor)
        {
            _monitor = monitor;
            _settingsPath = ResolveSettingsPath(helper);
            _settings = LoadOrCreate();
        }

        public ServerSettings Raw => _settings;

        #region Typed accessors — game creation settings (immutable after game created)

        public string FarmName => _settings.Game.FarmName;
        public int FarmType => _settings.Game.FarmType;
        public float ProfitMargin => _settings.Game.ProfitMargin;
        public int StartingCabins => _settings.Game.StartingCabins;

        /// <summary>
        /// Nullable bool: null means "auto" (true only for Wilderness farm type 4).
        /// </summary>
        public bool? SpawnMonstersAtNight => ParseNullableBool(_settings.Game.SpawnMonstersAtNight);

        #endregion

        #region Typed accessors — runtime settings (applied on every startup)

        public int MaxPlayers => _settings.Server.MaxPlayers;

        public CabinStrategy CabinStrategy => ParseCabinStrategy(_settings.Server.CabinStrategy);

        public bool SeparateWallets => _settings.Server.SeparateWallets;

        public ExistingCabinBehavior ExistingCabinBehavior =>
            ParseExistingCabinBehavior(_settings.Server.ExistingCabinBehavior);

        public bool VerboseLogging => _settings.Server.VerboseLogging;

        public bool AllowIpConnections => _settings.Server.AllowIpConnections;

        #endregion

        #region Runtime setters

        public void SetVerboseLogging(bool value)
        {
            _settings.Server.VerboseLogging = value;
            Save();
        }

        /// <summary>
        /// Persists current settings to the config file.
        /// </summary>
        public void Save()
        {
            SaveToFile(_settings);
        }

        #endregion

        #region File I/O

        private static string ResolveSettingsPath(IModHelper helper)
        {
            var envPath = Environment.GetEnvironmentVariable("SETTINGS_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                return envPath;
            }

            // Default: inside the mod's own directory (SMAPI-conventional)
            return Path.Combine(helper.DirectoryPath, "server-settings.json");
        }

        private ServerSettings LoadOrCreate()
        {
            if (File.Exists(_settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = JsonConvert.DeserializeObject<ServerSettings>(json);
                    if (settings != null)
                    {
                        _monitor.Log($"Loaded settings from {_settingsPath}", LogLevel.Info);
                        return settings;
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Failed to read settings file ({_settingsPath}): {ex.Message}", LogLevel.Error);
                    _monitor.Log("Using default settings.", LogLevel.Warn);
                }
            }
            else
            {
                _monitor.Log($"Settings file not found at {_settingsPath}, creating defaults.", LogLevel.Info);
            }

            var defaults = new ServerSettings();
            SaveToFile(defaults);
            return defaults;
        }

        private void SaveToFile(ServerSettings settings)
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
                _monitor.Log($"Settings file saved to {_settingsPath}", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to save settings file: {ex.Message}", LogLevel.Error);
            }
        }

        #endregion

        #region Parsers

        private static CabinStrategy ParseCabinStrategy(string value)
        {
            if (Enum.TryParse<CabinStrategy>(value, ignoreCase: true, out var result))
            {
                return result;
            }
            return CabinManager.CabinStrategy.CabinStack;
        }

        private static bool? ParseNullableBool(string value)
        {
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (bool.TryParse(value, out var result))
            {
                return result;
            }
            return null;
        }

        private static ExistingCabinBehavior ParseExistingCabinBehavior(string value)
        {
            if (Enum.TryParse<ExistingCabinBehavior>(value, ignoreCase: true, out var result))
            {
                return result;
            }
            return CabinManager.ExistingCabinBehavior.KeepExisting;
        }

        #endregion
    }
}
