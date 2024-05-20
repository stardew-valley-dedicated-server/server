using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
using Microsoft.Xna.Framework;
using StardewValley.Pathfinding;
using StardewValley.Locations;
using System.Linq;
using System.Threading;

namespace JunimoBot
{
    public class PathfindService
    {
        private int _steps = 0;
        private int _maxSteps = 15;
        private bool _isMoving = false;
        private bool _isInBed = false;

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;

        public PathfindService(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;

            // Utility.getDefaultWarpLocation
            //_schedule.Add(GetLocationRoute("Cabin", "Farm"));
            //_schedule.Add(GetLocationRoute("Farm", "Cabin"));
            _helper.Events.GameLoop.UpdateTicked += OnOneSecondUpdateTicked;
            _helper.Events.Input.ButtonPressed += OnButtonPressed;
            _helper.Events.GameLoop.DayStarted += DayStarted;
            _helper.Events.GameLoop.ReturnedToTitle += ReturnedToTitle;
            _helper.Events.Display.Rendered += OnRendered;
        }

        private void OnRendered(object sender, RenderedEventArgs e)
        {
            if (!AutomateService.IsAutomating)
            {
                return;
            }

            // Draw status information in the top left corner
            Util.DrawTextBox(5, 260, Game1.dialogueFont, I18n.AutoModeStepsLeft(steps: _steps, maxSteps: _maxSteps));
            Util.DrawTextBox(5, 340, Game1.dialogueFont, I18n.AutoModeStepsChangeHotkeyLabel(buttonDecrease: SButton.F7, buttonIncrease: SButton.F8));
        }

        private void ReturnedToTitle(object sender, EventArgs e)
        {
            ResetState();
        }

        private void DayStarted(object sender, EventArgs e)
        {
            ResetState();
        }


        private void OnOneSecondUpdateTicked(object sender, EventArgs e)
        {
            if (!AutomateService.IsAutomating || !Context.IsWorldReady)
            {
                return;
            }

            HandleAutoSleep();
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            switch(e.Button)
            {
                case SButton.F7:
                    _maxSteps--;
                    break;

                case SButton.F8:
                    _maxSteps++;
                    break;
            }
        }

        private void ResetState()
        {
            _isMoving = false;
            _isInBed = false;
            _steps = 0;
            Game1.player.controller = null;
        }

        private void HandleAutoSleep()
        {
            if (_isMoving || _isInBed)
            {
                return;
            }

            bool hasStepsLeft = _steps < _maxSteps;
            Point positionTarget = hasStepsLeft ? Game1.currentLocation.getRandomTile().ToPoint() : Util.GetBedSpot(Game1.player);
            _monitor.Log($"Route from ({Game1.player.TilePoint}) to ({positionTarget}), iteration ({_steps}/{_maxSteps}) until sleep", LogLevel.Info);

            WalkTo(positionTarget, (Character c, GameLocation location) => {
                _steps++;

                if (!hasStepsLeft)
                {
                    _isInBed = true;
                }
            });
        }

        private void WalkTo(Point position, PathFindController.endBehavior endBehaviourFunction)
        {
            PathFindController pfc = new PathFindController(
                Game1.player,
                Game1.currentLocation,
                position,
                Game1.player.facingDirection.Value,
                (Character c, GameLocation location) => {
                    _monitor.Log("Finished moving to the target position.", LogLevel.Info);
                    Game1.player.controller = null;
                    _isMoving = false;

                    endBehaviourFunction.Invoke(c, location);
                }
            );

            if (pfc.pathToEndPoint == null)
            {
                _monitor.Log("No path to the target position.", LogLevel.Warn);
            }
            else
            {
                _monitor.Log("Moving to the target position.", LogLevel.Info);
                Game1.player.controller = pfc;
                _isMoving = true;
            }
        }

        //private string[] GetLocationRoute(string locationSrc, string locationDest)
        //{
        //    var route = WarpPathfindingCache.GetLocationRoute(locationSrc, locationDest, Game1.player.Gender);

        //    if (locationDest == "Farm")
        //    {
        //        // Farm not included in WarpPathfindingCache, so we search for BusStop and then manually add Farm
        //        route = WarpPathfindingCache.GetLocationRoute(locationSrc, "BusStop", Game1.player.Gender);

        //        if (route != null)
        //        {
        //            route = route.Append("Farm").ToArray();
        //        }
        //    }
        //    else if (locationDest == "Cabin")
        //    {
        //        // Farm not included in WarpPathfindingCache, so we search for BusStop and then manually add Farm
        //        route = WarpPathfindingCache.GetLocationRoute(locationSrc, "BusStop", Game1.player.Gender);

        //        if (route != null)
        //        {
        //            route = route.Append("Farm").ToArray();
        //            route = route.Append("Cabin").ToArray();
        //        }
        //    }


        //    return route;
        //}

        //private void MovePlayer()
        //{
        //    //if (_isMoving)
        //    //{
        //    //    return;
        //    //}

        //    //Monitor.Log("MovePlayerToPosition", LogLevel.Info);

        //    //if (Game1.currentLocation is Cabin)
        //    //{
        //    //    var route = GetLocationRoute("Cabin", "Farm");
        //    //    LogJson(route);

        //    //    foreach (string routeLocation in route)
        //    //    {
        //    //        MoveToLocation("Cabin", routeLocation);
        //    //    }
        //    //}
        //    //else if (Game1.currentLocation is Farm)
        //    //{
        //    //    var route = GetLocationRoute("Farm", "Cabin");
        //    //    LogJson(route);

        //    //    foreach (string routeLocation in route)
        //    //    {
        //    //        MoveToLocation("Farm", routeLocation);
        //    //    }
        //    //}
        //}

        //private void MoveToFarm()
        //{
        //    // Already in Farm
        //    if (Game1.currentLocation is Farm)
        //    {
        //        return;
        //    }

        //    // Farm and Cabin is not included in WarpPathfindingCache,
        //    // so we manually move to Farm before moving to Cabin
        //    if (Game1.currentLocation is not Cabin)
        //    {
        //        MoveToLocation(Game1.currentLocation.Name, "BusStop");
        //    }


        //    MoveToLocation(Game1.currentLocation.Name, "Farm");
        //}

        //private void MoveToCabin()
        //{
        //    // Already in Cabin
        //    if (Game1.currentLocation is Cabin)
        //    {
        //        return;
        //    }


        //    // Farm and Cabin is not included in WarpPathfindingCache,
        //    // so we manually move to Farm before moving to Cabin
        //    if (Game1.currentLocation is not Farm)
        //    {
        //        MoveToFarm();
        //    }


        //    MoveToLocation(Game1.currentLocation.Name, "Cabin");
        //}

        //private void MoveToLocation(string locationNameSrc, string locationNameDest)
        //{
        //    _monitor.Log($"MoveToLocation: {locationNameSrc} -> {locationNameDest}");
        //    GameLocation locationSrc = Game1.RequireLocation(locationNameSrc, true);
        //    //GameLocation locationDest = Game1.RequireLocation(locationNameDest, true);

        //    Point warpPoint = locationSrc.getWarpPointTo(locationNameDest, Game1.player);
        //}

    }
}