using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JunimoServer.Services.NetworkTweaks
{
    public class DesyncKicker : ModService
    {
        private const int barrierDesyncMaxTime = 20;
        private const int endOfDayDesyncMaxTime = 60;

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;

        private CancellationTokenSource _currentEndOfDayCancelToken;
        private CancellationTokenSource _currentNewDayBarrierCancelToken;

        public DesyncKicker(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
            _helper.Events.GameLoop.DayEnding += OnDayEnding;
            _helper.Events.GameLoop.Saving += OnSaving;
            _helper.Events.GameLoop.Saved += OnSaved;

            // TODO: Add ping check
        }

        private void OnSaved(object sender, SavedEventArgs e)
        {
            _currentEndOfDayCancelToken.Cancel();
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            _currentNewDayBarrierCancelToken.Cancel();
            _monitor.Log("Saving");

            _currentEndOfDayCancelToken = new CancellationTokenSource();
            var token = _currentEndOfDayCancelToken.Token;

            Task.Run(async () => {
                _monitor.Log($"waiting {endOfDayDesyncMaxTime} sec to kick non-ready players");

                await Task.Delay(endOfDayDesyncMaxTime * 1000);
                if (token.IsCancellationRequested)
                {
                    _monitor.Log($"waited {endOfDayDesyncMaxTime} sec to kick non-ready players. Was Canceled.");
                    return;
                }

                _monitor.Log($"waited {endOfDayDesyncMaxTime} sec to kick non-ready players");
                KickDesyncedPlayers();
            });
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            if (SDate.Now().IsDayZero()) return;
            _monitor.Log("DayEnding");

            _currentNewDayBarrierCancelToken = new CancellationTokenSource();
            var token = _currentNewDayBarrierCancelToken.Token;

            Task.Run(async () => {
                _monitor.Log($"waiting {barrierDesyncMaxTime} sec to kick barrier");

                await Task.Delay(barrierDesyncMaxTime * 1000);
                if (token.IsCancellationRequested)
                {
                    _monitor.Log($"waited {barrierDesyncMaxTime} sec to kick barrier. Was Canceled");
                    return;
                }
                _monitor.Log($"waited {barrierDesyncMaxTime} sec to kick barrier");


                _monitor.Log("still stuck in barrier, going to try kicking");

                var readyPlayers = _helper.Reflection.GetMethod(Game1.newDaySync, "barrierPlayers").Invoke<HashSet<long>>("sleep");
                foreach (var key in (IEnumerable<long>)Game1.otherFarmers.Keys)
                {
                    if (!readyPlayers.Contains(key))
                    {
                        Game1.server.kick(key);
                        _monitor.Log("kicking due to not making past barrier: " + key);
                    }
                }

            });
        }

        private void KickDesyncedPlayers()
        {
            foreach (var farmer in Game1.otherFarmers.Values.ToArray().Where(farmer =>
                Game1.player.team.endOfNightStatus.GetStatusText(farmer.UniqueMultiplayerID) != "ready"
            ))
            {
                _monitor.Log($"Kicking {farmer.Name} because they aren't ready");
                Game1.server.kick(farmer.UniqueMultiplayerID);
            }
        }
    }
}
