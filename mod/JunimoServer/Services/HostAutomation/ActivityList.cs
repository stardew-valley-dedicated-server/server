using System.Collections.Generic;

namespace JunimoServer.Services.HostAutomation
{
    public class ActivityList : List<Activity>
    {
        public void EnableAll()
        {
            foreach (var activity in this)
            {
                activity.Enable();
            }
        }
        
        public void DisableAll()
        {
            foreach (var activity in this)
            {
                activity.Disable();
            }
        }
        
        public void TickAll()
        {
            foreach (var activity in this)
            {
                activity.HandleTick();
            }
        }
        
        public void DayStartAll()
        {
            foreach (var activity in this)
            {
                activity.HandleDayStart();
            }
        }
    }
}