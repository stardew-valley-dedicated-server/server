using StardewModdingAPI;
using StardewModdingAPI.Events;
using System.Collections.Generic;

namespace JunimoServer.Services.HostAutomation
{
  public class ActivityList : List<Activity>
  {
    private IModHelper _helper;
    private List<Activity> _activities;

    public ActivityList(IModHelper helper, IMonitor monitor)
    {
      _helper = helper;

      _helper.Events.GameLoop.SaveLoaded += EnableAll;
      _helper.Events.GameLoop.DayStarted += DayStartAll;
      _helper.Events.GameLoop.UpdateTicked += TickAll;
    }

    public void EnableAll(object sender, SaveLoadedEventArgs e)
    {
      foreach (var activity in this)
      {
        activity.Enable();
      }
    }

    // public void DisableAll()
    // {
    //   foreach (var activity in this)
    //   {
    //     activity.Disable();
    //   }
    // }

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

    ~ActivityList()
    {
      _helper.Events.GameLoop.SaveLoaded -= EnableAll;
      _helper.Events.GameLoop.DayStarted -= DayStartAll;
      _helper.Events.GameLoop.UpdateTicked -= TickAll;
    }
  }
}
