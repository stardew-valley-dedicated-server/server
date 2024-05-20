using JunimoServer.Services.HostAutomation.Activities;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace JunimoServer.Services.HostAutomation
{
    public class HostBot
    {
        private readonly ActivityList _activities;

        public HostBot(IModHelper helper, IMonitor monitor)
        {

            _activities = new ActivityList
            {
                new HideHostActivity(),
                new MatchFarmhouseToOwnerCabinLevelActivity(),
            };
            
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.UpdateTicked += OnTick;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            _activities.DayStartAll();
        }

        private void OnTick(object sender, UpdateTickedEventArgs e)
        {
            _activities.TickAll();
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            _activities.EnableAll();
        }
    }
}