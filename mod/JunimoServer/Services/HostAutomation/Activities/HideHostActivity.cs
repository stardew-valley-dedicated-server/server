using JunimoServer.Services.AlwaysOn;
using StardewValley;

namespace JunimoServer.Services.HostAutomation.Activities
{
    public class HideHostActivity : Activity
    {
        protected override void OnDayStart()
        {
            AutomationUtil.WarpToHidingSpot();
        }

        protected override void OnTick()
        {
            Game1.displayFarmer = !AlwaysOnServer.PlayerIsHidden;
        }

        protected override void OnEnabled()
        {
            Game1.displayFarmer = !AlwaysOnServer.PlayerIsHidden;
        }
    }
}
