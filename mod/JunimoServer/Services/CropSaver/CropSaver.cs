using System.Collections.Generic;
using HarmonyLib;
using JunimoServer.Util;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace JunimoServer.Services.CropSaver
{
    public class CropSaver : ModService
    {
        public static CropSaver? Instance { get; private set; }

        public CropSaverDataLoader DataLoader => _cropSaverDataLoader;

        private readonly CropWatcher _cropWatcher;
        private readonly CropSaverDataLoader _cropSaverDataLoader;

        private readonly IMonitor _monitor;
        private readonly IModHelper _helper;

        public CropSaver(IModHelper helper, Harmony harmony, IMonitor monitor)
        {
            _monitor = monitor;
            _helper = helper;
            _cropWatcher = new CropWatcher(helper, OnCropAdded, OnCropRemoved);
            _cropSaverDataLoader = new CropSaverDataLoader(helper);
            CropSaverOverrides.Initialize(_monitor, _cropSaverDataLoader);
            Instance = this;
            harmony.Patch(
                original: AccessTools.Method(typeof(Crop), nameof(Crop.Kill)),
                prefix: new HarmonyMethod(
                    typeof(CropSaverOverrides),
                    nameof(CropSaverOverrides.KillCrop_Prefix)
                )
            );

            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayEnding += OnDayEnd;
        }

        private void OnDayEnd(object sender, DayEndingEventArgs e)
        {
            //prolong crops
            var onlineIds = new HashSet<long>();
            foreach (var farmer in Game1.getOnlineFarmers())
            {
                onlineIds.Add(farmer.UniqueMultiplayerID);
            }

            _cropSaverDataLoader
                .GetSaverCrops()
                .ForEach(saverCrop =>
                {
                    var dirt = saverCrop.TryGetCoorespondingDirt();
                    if (dirt != null)
                    {
                        if (
                            saverCrop.ownerId != 0
                            && !onlineIds.Contains(saverCrop.ownerId)
                            && dirt.state.Value != HoeDirt.watered
                        )
                        {
                            saverCrop.IncrementExtraDays();
                        }
                    }
                });

            //remove crops
            for (var i = _cropSaverDataLoader.GetSaverCrops().Count - 1; i >= 0; i--)
            {
                var saverCrop = _cropSaverDataLoader.GetSaverCrops()[i];
                var crop = saverCrop.TryGetCoorespondingCrop();
                if (crop == null)
                {
                    _monitor.Log(
                        $"Crop at {saverCrop.cropLocationTile.X}, {saverCrop.cropLocationTile.Y} was still "
                            + $"being managed by CropSaver after death."
                            + $"\nRemoving from managed crops...",
                        LogLevel.Warn
                    );
                    _cropSaverDataLoader.RemoveCrop(
                        saverCrop.cropLocationName,
                        saverCrop.cropLocationTile
                    );
                    continue;
                }

                // A tracked entry whose seed id no longer resolves in cropData
                // (e.g. a mod was uninstalled between sessions, or a legacy entry
                // from a watcher version that didn't filter on data availability)
                // can't be processed by CalculateDateOfDeath. Drop it.
                if (crop.GetData() == null)
                {
                    _monitor.Log(
                        $"Crop at {saverCrop.cropLocationName} {saverCrop.cropLocationTile.X},{saverCrop.cropLocationTile.Y} "
                            + $"has no resolvable CropData. Removing from managed crops...",
                        LogLevel.Warn
                    );
                    _cropSaverDataLoader.RemoveCrop(
                        saverCrop.cropLocationName,
                        saverCrop.cropLocationTile
                    );
                    continue;
                }

                var nightOfDeath = CalculateDateOfDeath(crop, saverCrop);
                var fullyGrown = CalculateFullyGrown(crop);
                var earliestFullyGrownDate = CalculateEarliestPossibleFullyGrownDate(
                    crop,
                    saverCrop
                );
                var now = SDate.Now();
                var isAfterDateOfDeath = now >= nightOfDeath;

                if (!fullyGrown && now.Day == 28 && nightOfDeath < earliestFullyGrownDate)
                {
                    KillCrop(saverCrop, crop);
                }
                else if (
                    isAfterDateOfDeath && !(fullyGrown && onlineIds.Contains(saverCrop.ownerId))
                )
                {
                    KillCrop(saverCrop, crop);
                }
            }
        }

        private void KillCrop(SaverCrop saverCrop, Crop crop)
        {
            _cropSaverDataLoader.RemoveCrop(saverCrop.cropLocationName, saverCrop.cropLocationTile);
            var dead = _helper.Reflection.GetField<NetBool>(crop, "dead").GetValue();
            var raisedSeeds = _helper.Reflection.GetField<NetBool>(crop, "raisedSeeds").GetValue();

            dead.Value = true;
            raisedSeeds.Value = false;

            _monitor.Log($"Killing crop owned by {saverCrop.ownerId}");
        }

        private bool CalculateFullyGrown(Crop crop)
        {
            var currentPhase = _helper
                .Reflection.GetField<NetInt>(crop, "currentPhase")
                .GetValue()
                .Value;
            var phaseDays = _helper.Reflection.GetField<NetIntList>(crop, "phaseDays").GetValue();

            var fullyGrown = (currentPhase >= phaseDays.Count - 1);
            return fullyGrown;
        }

        private SDate CalculateEarliestPossibleFullyGrownDate(Crop crop, SaverCrop saverCrop)
        {
            if (CalculateFullyGrown(crop))
                return SDate.Now();

            var dirt = saverCrop.TryGetCoorespondingDirt();
            if (dirt == null)
                return SDate.Now();

            var extraDayForUnwatered = 1;
            if (dirt.state.Value == HoeDirt.watered)
            {
                extraDayForUnwatered = 0;
            }

            var phaseDays = _helper.Reflection.GetField<NetIntList>(crop, "phaseDays").GetValue();
            var currentPhase = _helper
                .Reflection.GetField<NetInt>(crop, "currentPhase")
                .GetValue()
                .Value;

            var daysOfCurrentPhase = _helper
                .Reflection.GetField<NetInt>(crop, "dayOfCurrentPhase")
                .GetValue()
                .Value;

            var daysLeftOfCurrentPhase = phaseDays[currentPhase] - daysOfCurrentPhase;
            var daysLeftOfPhasesUntilGrown = 0;

            for (int i = currentPhase + 1; i < phaseDays.Count - 1; i++)
            {
                daysLeftOfPhasesUntilGrown += phaseDays[i];
            }

            return SDate
                .Now()
                .AddDays(
                    daysLeftOfCurrentPhase + daysLeftOfPhasesUntilGrown + extraDayForUnwatered
                );
        }

        private static SDate CalculateDateOfDeath(Crop crop, SaverCrop saverCrop)
        {
            var numSeasons =
                crop.GetData().Seasons.Count
                - (crop.GetData().Seasons.IndexOf(saverCrop.datePlanted.Season));
            var numDaysToLive = saverCrop.extraDays + (28 * numSeasons) - saverCrop.datePlanted.Day;
            var dateOfDeath = saverCrop.datePlanted.AddDays(numDaysToLive);

            return dateOfDeath;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            _cropSaverDataLoader.LoadDataFromDisk();
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            _cropSaverDataLoader.SaveDataToDisk();
        }

        private void OnCropAdded(CropLocation cropLoc)
        {
            // Dedupe against existing entries — the watcher fires this on first
            // observation of a crop-bearing HoeDirt, which races against
            // OnSaveLoaded re-populating CropSaverData from disk.
            if (_cropSaverDataLoader.GetSaverCrop(cropLoc.LocationName, cropLoc.Tile) != null)
                return;

            var closestFarmer = FarmerUtil.GetClosestFarmer(
                cropLoc.Location,
                cropLoc.Tile,
                _helper.GetServerHostId()
            );
            var ownerId = closestFarmer?.UniqueMultiplayerID ?? 0;
            _cropSaverDataLoader.AddCrop(
                new SaverCrop(cropLoc.LocationName, cropLoc.Tile, ownerId, SDate.Now())
            );
        }

        private void OnCropRemoved(CropLocation cropLoc)
        {
            _cropSaverDataLoader.RemoveCrop(cropLoc.LocationName, cropLoc.Tile);
        }
    }
}
