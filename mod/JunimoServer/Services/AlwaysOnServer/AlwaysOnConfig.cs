using JunimoServer.Services.Settings;
using StardewModdingAPI;

namespace JunimoServer.Services.AlwaysOn;

public class AlwaysOnConfig
{
    public static AlwaysOnConfig FromSettings(ServerSettingsLoader settings)
    {
        return new AlwaysOnConfig()
        {
            PetName = settings.PetName,
            FarmCaveChoiceIsMushrooms = settings.MushroomCave,
            IsCommunityCenterRun = !settings.BuyJoja,
            ShouldCreatePet = settings.PetBreed is >= 0 and <= 9,
        };
    }

    public SButton HotKeyToggleAutoMode { get; set; } = SButton.F9;
    public SButton HotKeyToggleVisibility { get; set; } = SButton.F10;

    public string PetName { get; set; } = "Apples";
    public bool ShouldCreatePet { get; set; } = true;
    public bool FarmCaveChoiceIsMushrooms { get; set; } = true;
    public bool IsCommunityCenterRun { get; set; } = true;

    public bool LockPlayerChests { get; set; } = true;

    public int EggHuntCountdownSeconds { get; set; } = 300;
    public int FlowerDanceCountdownSeconds { get; set; } = 300;
    public int LuauSoupCountdownSeconds { get; set; } = 300;
    public int JellyDanceCountdownSeconds { get; set; } = 300;
    public int GrangeDisplayCountdownSeconds { get; set; } = 300;
    public int IceFishingCountdownSeconds { get; set; } = 300;

    public int FestivalExitWarningSeconds { get; set; } = 120;

    public int EndOfDayTimeOut { get; set; } = 120000;
    public int FairTimeOut { get; set; } = 120000;
    public int SpiritsEveTimeOut { get; set; } = 120000;
    public int WinterStarTimeOut { get; set; } = 120000;

    public int EggFestivalTimeOut { get; set; } = 120000;
    public int FlowerDanceTimeOut { get; set; } = 120000;
    public int LuauTimeOut { get; set; } = 120000;
    public int DanceOfJelliesTimeOut { get; set; } = 120000;
    public int FestivalOfIceTimeOut { get; set; } = 120000;
}
