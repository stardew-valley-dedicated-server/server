using JunimoServer.Services.PersistentOption;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Services.NetworkTweaks
{
    public class NetworkTweaker : ModService
    {
        private readonly PersistentOptions _options;

        public NetworkTweaker(IModHelper helper, PersistentOptions options)
        {
            _options = options;

            helper.Events.GameLoop.UpdateTicked += OnTick;
        }

        private void OnTick(object sender, UpdateTickedEventArgs e)
        {
            if (Game1.netWorldState.Value == null || !Game1.hasLoadedGame)
            {
                return;
            }

            HandleNetworkSettings();
            HandlePlayerLimit();
        }

        private void HandleNetworkSettings()
        {
            Game1.Multiplayer.defaultInterpolationTicks = 7;        // Default: 15
            Game1.Multiplayer.farmerDeltaBroadcastPeriod = 1;       // Default: 3
            Game1.Multiplayer.locationDeltaBroadcastPeriod = 1;     // Default: 3
            Game1.Multiplayer.worldStateDeltaBroadcastPeriod = 1;   // Default: 3
        }

        private void HandlePlayerLimit()
        {
            var maxPlayers = _options.Data.MaxPlayers;
            Game1.Multiplayer.playerLimit = maxPlayers;

            if (Game1.netWorldState.Value.CurrentPlayerLimit != maxPlayers)
            {
                Game1.netWorldState.Value.CurrentPlayerLimit = maxPlayers;
            }

            if (Game1.netWorldState.Value.HighestPlayerLimit != maxPlayers)
            {
                Game1.netWorldState.Value.HighestPlayerLimit = maxPlayers;
            }
        }
    }
}
