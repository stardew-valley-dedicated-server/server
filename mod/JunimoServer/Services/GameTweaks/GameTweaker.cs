using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoServer.Services.GameTweaks
{
    public class GameTweaker
    {
        public GameTweaker(IModHelper helper)
        {
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // TODO: What *exactly* does this? Currently assuming it allows everyone to move every building (necessary for multiplayer, otherwise only host can move buildings which is sadge)
            Game1.options.setMoveBuildingPermissions("on");
        }
    }
}