using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Services.GameTweaks
{
    public class GameTweaker : ModService
    {
        public GameTweaker(IModHelper helper)
        {
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // Allows everyone to move farm buildings
            Game1.options.setMoveBuildingPermissions("on");
        }
    }
}
