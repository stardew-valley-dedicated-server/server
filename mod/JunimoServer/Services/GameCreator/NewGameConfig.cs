using JunimoServer.Services.CabinManager;
using JunimoServer.Services.Settings;
using System;

namespace JunimoServer.Services.GameCreator
{
    public class NewGameConfig
    {
        public int WhichFarm { get; set; } = 0;
        public bool UseSeparateWallets { get; set; } = false;
        public int StartingCabins { get; set; } = 1;
        public string FarmName { get; set; } = "Junimo";
        public int MaxPlayers { get; set; } = 10;
        public CabinStrategy CabinStrategy { get; set; } = CabinStrategy.CabinStack;

        /// <summary>
        /// Nullable: null means "auto" (true only for Wilderness farm type 4).
        /// </summary>
        public bool? SpawnMonstersAtNight { get; set; } = null;

        /// <summary>
        /// Profit margin multiplier for sell prices (1.0 = normal).
        /// </summary>
        public float ProfitMargin { get; set; } = 1.0f;

        public static NewGameConfig FromSettings(ServerSettingsLoader settings)
        {
            return new NewGameConfig
            {
                FarmName = settings.FarmName,
                WhichFarm = settings.FarmType,
                MaxPlayers = settings.MaxPlayers,
                CabinStrategy = settings.CabinStrategy,
                UseSeparateWallets = settings.SeparateWallets,
                SpawnMonstersAtNight = settings.SpawnMonstersAtNight,
                ProfitMargin = settings.ProfitMargin,
                StartingCabins = settings.StartingCabins,
            };
        }

        /// <summary>
        /// Creates a NewGameConfig from API request parameters with sensible defaults.
        /// </summary>
        public static NewGameConfig FromRequest(
            int farmType = 0, string farmName = "Junimo", int startingCabins = 1,
            string cabinStrategy = "CabinStack", int maxPlayers = 10,
            float profitMargin = 1.0f, bool? spawnMonstersAtNight = null,
            bool separateWallets = false)
        {
            if (!Enum.TryParse<CabinStrategy>(cabinStrategy, ignoreCase: true, out var strategy))
            {
                strategy = CabinManager.CabinStrategy.CabinStack;
            }

            return new NewGameConfig
            {
                WhichFarm = farmType,
                FarmName = farmName,
                StartingCabins = startingCabins,
                CabinStrategy = strategy,
                MaxPlayers = maxPlayers,
                ProfitMargin = profitMargin,
                SpawnMonstersAtNight = spawnMonstersAtNight,
                UseSeparateWallets = separateWallets,
            };
        }

        public override string ToString()
        {
            return $"{nameof(FarmName)}: {FarmName}, {nameof(WhichFarm)}: {WhichFarm}, " +
                   $"{nameof(MaxPlayers)}: {MaxPlayers}, {nameof(CabinStrategy)}: {CabinStrategy}, " +
                   $"{nameof(UseSeparateWallets)}: {UseSeparateWallets}, " +
                   $"{nameof(SpawnMonstersAtNight)}: {SpawnMonstersAtNight?.ToString() ?? "auto"}, " +
                   $"{nameof(ProfitMargin)}: {ProfitMargin}, {nameof(StartingCabins)}: {StartingCabins}";
        }
    }
}
