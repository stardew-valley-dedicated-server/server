using JunimoServer.Services.CabinManager;
using JunimoServer.Services.Settings;

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
