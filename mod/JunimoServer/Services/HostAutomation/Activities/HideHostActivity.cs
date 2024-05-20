using StardewValley;

namespace JunimoServer.Services.HostAutomation.Activities
{
    public class HideHostActivity : Activity
    {
        protected override void OnDayStart()
        {
            AutomationUtil.WarpToHidingSpot();
        }

        protected override void OnTick(int tickNum)
        {
            Game1.displayFarmer = false;
        }

        protected override void OnDisabled()
        {
            Game1.displayFarmer = true;
        }
    }
}