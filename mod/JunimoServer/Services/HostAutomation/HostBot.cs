using JunimoServer.Services.HostAutomation.Activities;
using StardewModdingAPI;

namespace JunimoServer.Services.HostAutomation
{
    public class HostBot : ModService
    {
        private readonly ActivityList _activities;

        public HostBot(IModHelper helper, IMonitor monitor)
        {
            _activities = new ActivityList(helper, monitor)
            {
                new HideHostActivity(),
                new MatchFarmhouseToOwnerCabinLevelActivity(),
            };
        }
    }
}
