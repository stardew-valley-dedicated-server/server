using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.GameCreator;
using JunimoServer.Util;
using Newtonsoft.Json;
using StardewModdingAPI;

namespace JunimoServer.Services.Settings;

/// <summary>
/// Loads server settings from a JSON file and exposes parsed, typed values.
/// If the settings file does not exist, creates one with all defaults.
/// </summary>
public class ServerSettingsLoader
{
    private ServerSettings _settings;
    private readonly string _settingsPath;
    private readonly IMonitor _monitor;

    public ServerSettingsLoader(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        _settingsPath = ResolveSettingsPath(helper);
        _settings = LoadOrCreate();
    }

    public ServerSettings Raw => _settings;

    #region Typed accessors: game creation settings (immutable after game created)

    public string FarmName => _settings.Game.FarmName;
    public FarmTypeSetting FarmType => _settings.Game.FarmType;
    public float ProfitMargin => _settings.Game.ProfitMargin;
    public int StartingCabins => _settings.Game.StartingCabins;

    public bool RemixBundles => _settings.Game.RemixBundles;
    public bool RemixMines => _settings.Game.RemixMines;
    public bool CommunityCenterYear1 => _settings.Game.CommunityCenterYear1;
    public bool CabinLayoutNearby => _settings.Game.CabinLayoutNearby;
    public bool UseLegacyRandom => _settings.Game.UseLegacyRandom;
    public ulong? RandomSeed => _settings.Game.RandomSeed;
    public int PetBreed => _settings.Game.PetBreed;
    public string PetName => _settings.Game.PetName;
    public bool MushroomCave => _settings.Game.MushroomCave;
    public bool BuyJoja => _settings.Game.BuyJoja;

    /// <summary>
    /// Nullable bool: null means "auto" (true only for Wilderness farm type 4).
    /// </summary>
    public bool? SpawnMonstersAtNight => ParseNullableBool(_settings.Game.SpawnMonstersAtNight);

    #endregion

    #region Typed accessors: runtime settings (applied on every startup)

    public int MaxPlayers => _settings.Server.MaxPlayers;

    public CabinStrategy CabinStrategy => _settings.Server.CabinStrategy;

    public bool SeparateWallets => _settings.Server.SeparateWallets;

    public ExistingCabinBehavior ExistingCabinBehavior => _settings.Server.ExistingCabinBehavior;

    public bool VerboseLogging => _settings.Server.VerboseLogging;

    public bool AllowIpConnections => _settings.Server.AllowIpConnections;

    /// <summary>
    /// Whether the farmhand ownership gate and transport-scoped visibility narrowing are
    /// enforced (see <see cref="ServerRuntimeSettings.EnforceFarmhandOwnership"/>).
    /// </summary>
    public bool EnforceFarmhandOwnership => _settings.Server.EnforceFarmhandOwnership;

    /// <summary>
    /// Lobby mode for password protection: Shared or Individual.
    /// </summary>
    public LobbyMode LobbyMode => _settings.Server.LobbyMode;

    /// <summary>
    /// Name of the active lobby layout for new players.
    /// </summary>
    public string ActiveLobbyLayout => _settings.Server.ActiveLobbyLayout;

    /// <summary>
    /// Steam IDs that are automatically granted admin on join.
    /// </summary>
    public string[] AdminSteamIds => _settings.Server.AdminSteamIds ?? Array.Empty<string>();

    /// <summary>
    /// Broadcast period (in ticks) for farmer/location/world-state deltas.
    /// Out-of-range values clamp into [1, 60] with a warning.
    /// </summary>
    public int NetworkBroadcastPeriod =>
        ClampBroadcastPeriod(_settings.Server.NetworkBroadcastPeriod);

    #endregion

    #region Runtime setters

    /// <summary>
    /// Re-reads the settings file from disk, picking up any changes made since
    /// startup. Used by the in-process reload path so an operator can edit
    /// server-settings.json and apply it without a process restart.
    /// </summary>
    public void Reload()
    {
        _settings = LoadOrCreate();
    }

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
                var rejects = new List<SettingReject>();
                var settings = JsonConvert.DeserializeObject<ServerSettings>(
                    json,
                    new JsonSerializerSettings
                    {
                        // Reject sink for the settings converters, which have no IMonitor — they
                        // record unparseable values here for the loader to warn about.
                        Context = new StreamingContext(StreamingContextStates.Other, rejects),
                    }
                );
                if (settings != null)
                {
                    _monitor.Log($"Loaded settings from {_settingsPath}", LogLevel.Info);
                    LogSettingRejects(rejects);
                    return settings;
                }
            }
            catch (Exception ex)
            {
                _monitor.Log(
                    $"Failed to read settings file ({_settingsPath}): {ex.Message}",
                    LogLevel.Error
                );
                _monitor.Log("Using default settings.", LogLevel.Warn);
            }
        }
        else
        {
            _monitor.Log(
                $"Settings file not found at {_settingsPath}, creating defaults.",
                LogLevel.Info
            );
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

    /// <summary>
    /// Warns (naming the file) about each value a settings converter could not parse. Warn, not
    /// Error — an Error line is E2E test poison (see rules/debugging.md).
    /// </summary>
    private void LogSettingRejects(IReadOnlyList<SettingReject> rejects)
    {
        foreach (var reject in rejects)
        {
            _monitor.Log(
                $"Invalid {reject.Path} in {_settingsPath}: '{reject.RejectedValue}' is not recognized; using '{reject.FallbackUsed}'.",
                LogLevel.Warn
            );
        }
    }

    private int ClampBroadcastPeriod(int value)
    {
        const int min = 1;
        const int max = 60;
        if (value < min || value > max)
        {
            var clamped = Math.Clamp(value, min, max);
            _monitor.Log(
                $"NetworkBroadcastPeriod={value} out of range [{min},{max}]; clamped to {clamped}.",
                LogLevel.Warn
            );
            return clamped;
        }
        return value;
    }

    #endregion
}
