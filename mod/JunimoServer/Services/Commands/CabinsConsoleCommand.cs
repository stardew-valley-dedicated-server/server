using JunimoServer.Services.CabinManager;
using JunimoServer.Services.PersistentOption;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using System.Linq;

namespace JunimoServer.Services.Commands
{
    internal static class CabinsConsoleCommand
    {
        private static IMonitor _monitor;
        private static CabinManagerService _cabinManager;
        private static PersistentOptions _options;

        public static void Register(
            IModHelper helper,
            IMonitor monitor,
            CabinManagerService cabinManager,
            PersistentOptions options)
        {
            _monitor = monitor;
            _cabinManager = cabinManager;
            _options = options;

            helper.ConsoleCommands.Add("cabins",
                "Cabin status and management. Run 'cabins' for list, 'cabins add' to create.",
                (cmd, args) => HandleCommand(args));
        }

        private static void HandleCommand(string[] args)
        {
            if (args.Length > 0 && args[0].ToLowerInvariant() == "add")
            {
                AddCabin();
                return;
            }

            ShowCabins();
        }

        private static void ShowCabins()
        {
            if (!Game1.hasLoadedGame)
            {
                _monitor.Log("No game loaded yet.", LogLevel.Warn);
                return;
            }

            var strategy = _options.Data.CabinStrategy;
            var farm = Game1.getFarm();
            var cabins = farm.buildings.Where(b => b.isCabin).ToList();

            _monitor.Log($"Cabin Status (Strategy: {strategy})", LogLevel.Info);

            int index = 1;
            int assignedCount = 0;
            int availableCount = 0;

            foreach (var building in cabins)
            {
                var cabin = building.GetIndoors<Cabin>();
                var isHidden = building.IsInHiddenStack();
                var posLabel = isHidden
                    ? $"Hidden ({CabinManagerService.HiddenCabinLocation.X},{CabinManagerService.HiddenCabinLocation.Y})"
                    : $"Visible ({building.tileX.Value},{building.tileY.Value})";

                var ownerId = cabin?.owner?.UniqueMultiplayerID ?? 0;
                string ownerLabel;
                if (ownerId == 0)
                {
                    ownerLabel = "Unassigned (available)";
                    availableCount++;
                }
                else
                {
                    var ownerName = cabin?.owner?.Name ?? "Unknown";
                    ownerLabel = $"{ownerName} (ID: {ownerId})";
                    assignedCount++;
                }

                _monitor.Log($"  #{index,-3} {posLabel,-30} {ownerLabel}", LogLevel.Info);
                index++;
            }

            if (strategy != CabinStrategy.None)
            {
                var stackPos = StackLocation.Create(_cabinManager.Data);
                _monitor.Log($"", LogLevel.Info);
                _monitor.Log($"  Stack position: ({stackPos.Location.X}, {stackPos.Location.Y})", LogLevel.Info);
            }

            _monitor.Log($"", LogLevel.Info);
            _monitor.Log($"  Total: {cabins.Count} | Assigned: {assignedCount} | Available: {availableCount}", LogLevel.Info);
        }

        private static void AddCabin()
        {
            if (!Game1.hasLoadedGame)
            {
                _monitor.Log("No game loaded yet.", LogLevel.Warn);
                return;
            }

            var farm = Game1.getFarm();
            bool success = _options.IsNone
                ? _cabinManager.BuildNewCabinVisible(farm)
                : _cabinManager.BuildNewCabin(farm);

            if (success)
            {
                var totalCabins = farm.buildings.Count(b => b.isCabin);
                var available = farm.buildings
                    .Where(b => b.isCabin)
                    .Count(b =>
                    {
                        var cabin = b.GetIndoors<Cabin>();
                        return cabin?.owner == null || cabin.owner.UniqueMultiplayerID == 0;
                    });
                _monitor.Log($"Cabin created. Total: {totalCabins} | Available: {available}", LogLevel.Info);
            }
            else
            {
                _monitor.Log("Failed to create cabin.", LogLevel.Error);
            }
        }
    }
}
