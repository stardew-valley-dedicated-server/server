using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using System;
using System.Reflection;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Controller for in-game actions like sleeping.
/// </summary>
public class ActionsController
{
    private readonly IMonitor _monitor;

    private static readonly MethodInfo? StartSleepMethod =
        typeof(GameLocation).GetMethod("startSleep", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? typeof(FarmHouse).GetMethod("startSleep", BindingFlags.NonPublic | BindingFlags.Instance);

    public ActionsController(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Make the current player go to sleep by triggering the sleep sequence
    /// at their home location (Cabin for farmhands, Farmhouse for host).
    /// </summary>
    public SleepResult GoToSleep()
    {
        try
        {
            if (!Context.IsWorldReady)
            {
                return new SleepResult { Success = false, Error = "Not in a game world" };
            }

            // Get the player's home location (FarmHouse for host, Cabin for farmhands)
            var home = Utility.getHomeOfFarmer(Game1.player);
            if (home == null)
            {
                return new SleepResult { Success = false, Error = "Could not find player's home location" };
            }

            if (StartSleepMethod == null)
            {
                return new SleepResult { Success = false, Error = "Could not find startSleep method via reflection" };
            }

            // Set sleep location data (mirrors what the server host bot does)
            Game1.player.lastSleepLocation.Value = home.NameOrUniqueName;
            Game1.player.lastSleepPoint.Value = home.GetPlayerBedSpot();

            // Trigger sleep via reflection (startSleep is private)
            StartSleepMethod.Invoke(home, null);

            _monitor.Log($"Triggered sleep at {home.NameOrUniqueName}", LogLevel.Info);

            return new SleepResult
            {
                Success = true,
                Message = $"Going to sleep at {home.NameOrUniqueName}",
                Location = home.NameOrUniqueName
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to go to sleep: {ex.Message}", LogLevel.Error);
            return new SleepResult { Success = false, Error = ex.Message };
        }
    }
}

public class SleepResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Location { get; set; }
    public string? Error { get; set; }
}
