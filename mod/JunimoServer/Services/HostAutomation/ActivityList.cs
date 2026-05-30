using StardewModdingAPI;
using StardewModdingAPI.Events;
using System.Collections.Generic;

namespace JunimoServer.Services.HostAutomation
{
    public class ActivityList : List<Activity>
    {
        public ActivityList(IModHelper helper, IMonitor monitor)
        {
            helper.Events.GameLoop.SaveLoaded += EnableAll;
            helper.Events.GameLoop.ReturnedToTitle += DisableAll;
            helper.Events.GameLoop.DayStarted += DayStartAll;
            helper.Events.GameLoop.UpdateTicked += TickAll;
        }

        public void EnableAll(object sender, SaveLoadedEventArgs e)
        {
            foreach (var activity in this)
            {
                activity.Enable();
            }
        }

        public void DisableAll(object sender, ReturnedToTitleEventArgs e)
        {
            foreach (var activity in this)
            {
                activity.Disable();
            }
        }

        public void TickAll(object sender, UpdateTickedEventArgs e)
        {
            foreach (var activity in this)
            {
                activity.HandleTick();
            }
        }

        public void DayStartAll(object sender, DayStartedEventArgs e)
        {
            foreach (var activity in this)
            {
                activity.HandleDayStart();
            }
        }
    }
}
