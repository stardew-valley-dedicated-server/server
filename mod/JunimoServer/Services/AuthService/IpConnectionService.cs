using JunimoServer.Services.Settings;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// Controls whether IP connections are allowed.
    /// Disabled by default because IP connections don't send user IDs needed for farmhand ownership.
    /// Configure via Server.AllowIpConnections in server-settings.json.
    /// </summary>
    public class IpConnectionService : ModService
    {
        private readonly IModHelper _helper;
        private readonly ServerSettingsLoader _settings;

        public IpConnectionService(IMonitor monitor, IModHelper helper, ServerSettingsLoader settings)
            : base(monitor)
        {
            _helper = helper;
            _settings = settings;

            _helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            ApplyIpConnectionSetting();
        }

        private void ApplyIpConnectionSetting()
        {
            Game1.options.ipConnectionsEnabled = _settings.AllowIpConnections;

            if (_settings.AllowIpConnections)
            {
                Monitor.Log("IP connections enabled (AllowIpConnections=true)", LogLevel.Info);
                Monitor.Log("Warning: IP clients don't provide user IDs - farmhand ownership may not work correctly.", LogLevel.Warn);
            }
            else
            {
                Monitor.Log("IP connections disabled (default). Players must use invite codes to join.", LogLevel.Debug);
            }
        }
    }
}
