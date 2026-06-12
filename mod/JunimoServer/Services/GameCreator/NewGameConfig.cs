using System;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.Settings;

namespace JunimoServer.Services.GameCreator;

public class NewGameConfig
{
    public FarmTypeSetting WhichFarm { get; set; } = FarmTypeSetting.Default;
    public bool UseSeparateWallets { get; set; } = false;
    public int StartingCabins { get; set; } = 1;
    public string FarmName { get; set; } = "Junimo";
    public int MaxPlayers { get; set; } = 10;
    public CabinStrategy CabinStrategy { get; set; } = CabinStrategy.CabinStack;

    // advanced creation options
    public bool BundlesRemix { get; set; } = false;
    public bool MinesRemix { get; set; } = false;
    public bool CommunityCenterYear1 { get; set; } = false;
    public bool CabinLayoutNearby { get; set; } = false;
    public bool UseLegacyRandom { get; set; } = false;
    public ulong? RandomSeed { get; set; } = null;
    public int PetBreed { get; set; } = 1;

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

            BundlesRemix = settings.RemixBundles,
            MinesRemix = settings.RemixMines,
            CommunityCenterYear1 = settings.CommunityCenterYear1,
            CabinLayoutNearby = settings.CabinLayoutNearby,
            UseLegacyRandom = settings.UseLegacyRandom,
            RandomSeed = settings.RandomSeed,
            PetBreed = settings.PetBreed,
        };
    }

    /// <summary>
    /// Creates a NewGameConfig from API request parameters with sensible defaults.
    /// </summary>
    public static NewGameConfig FromRequest(
        FarmTypeSetting farmType,
        string farmName = "Junimo",
        int startingCabins = 1,
        string cabinStrategy = "CabinStack",
        int maxPlayers = 10,
        float profitMargin = 1.0f,
        bool? spawnMonstersAtNight = null,
        bool separateWallets = false
    )
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
        return $"{nameof(FarmName)}: {FarmName}, {nameof(WhichFarm)}: {WhichFarm}, "
            + $"{nameof(MaxPlayers)}: {MaxPlayers}, {nameof(CabinStrategy)}: {CabinStrategy}, "
            + $"{nameof(UseSeparateWallets)}: {UseSeparateWallets}, "
            + $"{nameof(SpawnMonstersAtNight)}: {SpawnMonstersAtNight?.ToString() ?? "auto"}, "
            + $"{nameof(ProfitMargin)}: {ProfitMargin}, {nameof(StartingCabins)}: {StartingCabins}, "
            + $"{nameof(BundlesRemix)}: {BundlesRemix}, "
            + $"{nameof(MinesRemix)}: {MinesRemix}, "
            + $"{nameof(CommunityCenterYear1)}: {CommunityCenterYear1}, "
            + $"{nameof(CabinLayoutNearby)}: {CabinLayoutNearby}, "
            + $"{nameof(UseLegacyRandom)}: {UseLegacyRandom}, "
            + $"{nameof(RandomSeed)}: {RandomSeed}, "
            + $"{nameof(PetBreed)}: {PetBreed}";
    }
}
