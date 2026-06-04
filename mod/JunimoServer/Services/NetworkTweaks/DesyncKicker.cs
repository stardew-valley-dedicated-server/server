using JunimoServer.Services.Lobby;
using JunimoServer.Shared;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using System;
using System.Collections.Concurrent;
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
        private readonly LobbyService _lobbyService;

        private CancellationTokenSource _currentEndOfDayCancelToken;
        private CancellationTokenSource _currentNewDayBarrierCancelToken;

        // Game-state mutations (kick, reflection on Game1.newDaySync, reads of
        // Game1.otherFarmers / Game1.player.team) must run on the game thread.
        // The Task.Run continuations below resume on the thread pool, so they
        // enqueue work here and the UpdateTicked drain executes it inline.
        private readonly ConcurrentQueue<Action> _pendingGameThreadActions = new();

        public DesyncKicker(IModHelper helper, IMonitor monitor, LobbyService lobbyService)
        {
            _helper = helper;
            _monitor = monitor;
            _lobbyService = lobbyService;
            _helper.Events.GameLoop.DayEnding += OnDayEnding;
            _helper.Events.GameLoop.Saving += OnSaving;
            _helper.Events.GameLoop.Saved += OnSaved;
            _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            // TODO: Add ping check
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            while (_pendingGameThreadActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Pending game-thread action failed: {ex}", LogLevel.Warn);
                }
            }
        }

        private void OnSaved(object sender, SavedEventArgs e)
        {
            _currentEndOfDayCancelToken?.Cancel();
            _currentEndOfDayCancelToken?.Dispose();
            _currentEndOfDayCancelToken = null;

            _currentNewDayBarrierCancelToken?.Cancel();
            _currentNewDayBarrierCancelToken?.Dispose();
            _currentNewDayBarrierCancelToken = null;
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            _currentNewDayBarrierCancelToken?.Cancel();
            _currentNewDayBarrierCancelToken?.Dispose();
            _currentNewDayBarrierCancelToken = null;

            _monitor.Log("Saving");

            _currentEndOfDayCancelToken?.Cancel();
            _currentEndOfDayCancelToken?.Dispose();
            _currentEndOfDayCancelToken = new CancellationTokenSource();
            var token = _currentEndOfDayCancelToken.Token;
            var capturedRequestId = Diagnostics.ModRequestContext.RequestId;

            Task.Run(async () =>
            {
                _monitor.Log($"waiting {endOfDayDesyncMaxTime} sec to kick non-ready players");

                try
                {
                    await Task.Delay(endOfDayDesyncMaxTime * 1000, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _monitor.Log($"waited {endOfDayDesyncMaxTime} sec to kick non-ready players");

                _pendingGameThreadActions.Enqueue(() =>
                {
                    using var _scope = Diagnostics.ModRequestContext.Bind(capturedRequestId);
                    if (token.IsCancellationRequested) return;
                    if (Game1.server == null) return;

                    var excludedIds = _lobbyService?.GetExcludedPlayerIds() ?? new HashSet<long>();
                    foreach (var farmer in Game1.otherFarmers.Values.ToArray().Where(farmer =>
                        !excludedIds.Contains(farmer.UniqueMultiplayerID) &&
                        Game1.player.team.endOfNightStatus.GetStatusText(farmer.UniqueMultiplayerID) != "ready"
                    ))
                    {
                        _monitor.Log($"Kicking {ChatRedaction.MaskValue(farmer.Name)} because they aren't ready");
                        Game1.server.kick(farmer.UniqueMultiplayerID);
                    }
                });
            });
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            if (SDate.Now().IsDayZero()) return;
            _monitor.Log("DayEnding");

            _currentNewDayBarrierCancelToken?.Cancel();
            _currentNewDayBarrierCancelToken?.Dispose();
            _currentNewDayBarrierCancelToken = new CancellationTokenSource();
            var token = _currentNewDayBarrierCancelToken.Token;
            var capturedRequestId = Diagnostics.ModRequestContext.RequestId;

            Task.Run(async () =>
            {
                _monitor.Log($"waiting {barrierDesyncMaxTime} sec to kick barrier");

                try
                {
                    await Task.Delay(barrierDesyncMaxTime * 1000, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _monitor.Log($"waited {barrierDesyncMaxTime} sec to kick barrier");
                _monitor.Log("still stuck in barrier, going to try kicking");

                _pendingGameThreadActions.Enqueue(() =>
                {
                    using var _scope = Diagnostics.ModRequestContext.Bind(capturedRequestId);
                    if (token.IsCancellationRequested) return;
                    if (Game1.server == null) return;

                    var readyPlayers = _helper.Reflection.GetMethod(Game1.newDaySync, "barrierPlayers").Invoke<HashSet<long>>("sleep");
                    var excludedIds = _lobbyService?.GetExcludedPlayerIds() ?? new HashSet<long>();
                    // Use ToArray() to create snapshot - avoids collection modified exception
                    // if a player disconnects during iteration
                    foreach (var key in Game1.otherFarmers.Keys.ToArray())
                    {
                        if (excludedIds.Contains(key))
                            continue; // Skip lobby/editor players, excluded from barriers by design
                        if (!readyPlayers.Contains(key))
                        {
                            Game1.server.kick(key);
                            _monitor.Log("kicking due to not making past barrier: " + key);
                        }
                    }
                });
            });
        }
    }
}
