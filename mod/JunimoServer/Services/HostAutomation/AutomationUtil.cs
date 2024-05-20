using StardewValley;

namespace JunimoServer.Services.HostAutomation
{
    public static class AutomationUtil
    {
        public static void WarpToHidingSpot()
        {
            Game1.warpFarmer("Farm", 64, 15, false);
        }
    }
}