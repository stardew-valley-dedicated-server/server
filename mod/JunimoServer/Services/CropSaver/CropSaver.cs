using HarmonyLib;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.TerrainFeatures;
using System.Collections.Generic;

namespace JunimoServer.Services.CropSaver
{
    public class CropSaver : ModService
    {
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
            CropSaverOverrides.Initialize(helper, _monitor, _cropSaverDataLoader);
            harmony.Patch(
                original: AccessTools.Method(typeof(Crop), nameof(Crop.Kill)),
                prefix: new HarmonyMethod(typeof(CropSaverOverrides), nameof(CropSaverOverrides.KillCrop_Prefix))
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


            _cropSaverDataLoader.GetSaverCrops().ForEach(saverCrop => {
                var dirt = saverCrop.TryGetCoorespondingDirt();
                if (dirt != null)
                {
                    if (!onlineIds.Contains(saverCrop.ownerId) && dirt.state.Value != HoeDirt.watered)
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
                        $"Crop at {saverCrop.cropLocationTile.X}, {saverCrop.cropLocationTile.Y} was still " +
                        $"being managed by CropSaver after death." +
                        $"\nRemoving from managed crops...", LogLevel.Warn);
                    _cropSaverDataLoader.RemoveCrop(saverCrop.cropLocationName, saverCrop.cropLocationTile);
                    continue;
                }


                var nightOfDeath = CalculateDateOfDeath(crop, saverCrop);
                var fullyGrown = CalculateFullyGrown(crop);
                var earliestFullyGrownDate = CalculateEarliestPossibleFullyGrownDate(crop, saverCrop);
                var now = SDate.Now();
                var isAfterDateOfDeath = now >= nightOfDeath;

                if (!fullyGrown && now.Day == 28 && nightOfDeath < earliestFullyGrownDate)
                {
                    KillCrop(saverCrop, crop);
                }
                else if (isAfterDateOfDeath && !(fullyGrown && onlineIds.Contains(saverCrop.ownerId)))
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
            var currentPhase = _helper.Reflection.GetField<NetInt>(crop, "currentPhase").GetValue().Value;
            var phaseDays = _helper.Reflection.GetField<NetIntList>(crop, "phaseDays").GetValue();


            var fullyGrown = (currentPhase >= phaseDays.Count - 1);
            return fullyGrown;
        }


        private SDate CalculateEarliestPossibleFullyGrownDate(Crop crop, SaverCrop saverCrop)
        {
            if (CalculateFullyGrown(crop)) return SDate.Now();

            var dirt = saverCrop.TryGetCoorespondingDirt();
            if (dirt == null) return SDate.Now();

            var extraDayForUnwatered = 1;
            if (dirt.state.Value == HoeDirt.watered)
            {
                extraDayForUnwatered = 0;
            }

            var phaseDays = _helper.Reflection.GetField<NetIntList>(crop, "phaseDays").GetValue();
            var currentPhase = _helper.Reflection.GetField<NetInt>(crop, "currentPhase").GetValue().Value;

            var daysOfCurrentPhase = _helper.Reflection.GetField<NetInt>(crop, "dayOfCurrentPhase").GetValue().Value;

            var daysLeftOfCurrentPhase = phaseDays[currentPhase] - daysOfCurrentPhase;
            var daysLeftOfPhasesUntilGrown = 0;

            for (int i = currentPhase + 1; i < phaseDays.Count - 1; i++)
            {
                daysLeftOfPhasesUntilGrown += phaseDays[i];
            }

            return SDate.Now().AddDays(daysLeftOfCurrentPhase + daysLeftOfPhasesUntilGrown + extraDayForUnwatered);
        }
        private static SDate CalculateDateOfDeath(Crop crop, SaverCrop saverCrop)
        {
            var numSeasons = crop.GetData().Seasons.Count -
                             (crop.GetData().Seasons.IndexOf(saverCrop.datePlanted.Season));
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


        private void OnCropAdded(TerrainFeature feature)
        {
            var closestFarmer = FarmerUtil.GetClosestFarmer(feature.Location, feature.Tile);
            _cropSaverDataLoader.AddCrop(new SaverCrop(
                    feature.Location.Name,
                    feature.Tile,
                    closestFarmer.UniqueMultiplayerID,
                    SDate.Now()
                )
            );

            // _monitor.Log(
            //     $"Added crop planted at: {feature.currentLocation} on: {feature.currentTileLocation} by: {closestFarmer.Name}");
        }

        private void OnCropRemoved(TerrainFeature feature)
        {
            _cropSaverDataLoader.RemoveCrop(feature.Location.Name, feature.Tile);
            // _monitor.Log(
            //     $"Removed crop at: {feature.currentLocation} on: {feature.currentTileLocation}");
        }
    }
}
