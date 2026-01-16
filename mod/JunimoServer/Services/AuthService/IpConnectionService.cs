using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// Controls whether IP connections are allowed.
    /// Disabled by default because IP connections don't send user IDs needed for farmhand ownership.
    /// Set ALLOW_IP_CONNECTIONS=true to enable.
    /// </summary>
    public class IpConnectionService : ModService
    {
        private readonly IModHelper _helper;
        private readonly bool _allowIpConnections;

        public IpConnectionService(IMonitor monitor, IModHelper helper)
            : base(monitor)
        {
            _helper = helper;
            _allowIpConnections = GetAllowIpConnectionsSetting();

            _helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        }

        private static bool GetAllowIpConnectionsSetting()
        {
            var envValue = Environment.GetEnvironmentVariable("ALLOW_IP_CONNECTIONS");
            return string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(envValue, "1", StringComparison.OrdinalIgnoreCase);
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            ApplyIpConnectionSetting();
        }

        private void ApplyIpConnectionSetting()
        {
            Game1.options.ipConnectionsEnabled = _allowIpConnections;

            if (_allowIpConnections)
            {
                Monitor.Log("IP connections enabled (ALLOW_IP_CONNECTIONS=true)", LogLevel.Info);
                Monitor.Log("Warning: IP clients don't provide user IDs - farmhand ownership may not work correctly.", LogLevel.Warn);
            }
            else
            {
                Monitor.Log("IP connections disabled (default). Players must use invite codes to join.", LogLevel.Debug);
                Monitor.Log("Set ALLOW_IP_CONNECTIONS=true to enable IP-based connections.", LogLevel.Debug);
            }
        }
    }
}
