using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace JunimoServer.Util
{
    /// <summary>
    /// Utility to configure SMAPI logging at runtime via reflection.
    /// </summary>
    public static class SmapiLogConfig
    {
        /// <summary>
        /// Enables or disables verbose logging for a mod by adding/removing
        /// its ID from SMAPI's ForceVerboseLogging static HashSet.
        /// </summary>
        /// <param name="modId">The mod's unique ID (e.g., "JunimoHost.Server")</param>
        /// <param name="enabled">True to enable verbose logging, false to disable</param>
        /// <param name="monitor">Monitor for logging warnings on failure</param>
        /// <returns>True if successful, false if reflection failed</returns>
        public static bool SetVerboseLogging(string modId, bool enabled, IMonitor monitor)
        {
            try
            {
                var monitorType = Type.GetType("StardewModdingAPI.Framework.Monitor, StardewModdingAPI");
                if (monitorType == null)
                {
                    monitor.Log("Could not find SMAPI Monitor type for verbose logging config", LogLevel.Warn);
                    return false;
                }

                var property = monitorType.GetProperty("ForceVerboseLogging",
                    BindingFlags.Public | BindingFlags.Static);
                if (property == null)
                {
                    monitor.Log("Could not find ForceVerboseLogging property", LogLevel.Warn);
                    return false;
                }

                var hashSet = property.GetValue(null) as HashSet<string>;
                if (hashSet == null)
                {
                    monitor.Log("ForceVerboseLogging is null", LogLevel.Warn);
                    return false;
                }

                if (enabled)
                    hashSet.Add(modId);
                else
                    hashSet.Remove(modId);

                monitor.Log($"SMAPI ForceVerboseLogging updated: {modId}={enabled}", LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed to set verbose logging: {ex.Message}", LogLevel.Warn);
                return false;
            }
        }
    }
}
