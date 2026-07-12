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

    public int EndOfDayTimeOutSeconds { get; set; } = 2000;
    public int FairTimeOutSeconds { get; set; } = 2000;
    public int SpiritsEveTimeOutSeconds { get; set; } = 2000;
    public int WinterStarTimeOutSeconds { get; set; } = 2000;

    public int EggFestivalTimeOutSeconds { get; set; } = 2000;
    public int FlowerDanceTimeOutSeconds { get; set; } = 2000;
    public int LuauTimeOutSeconds { get; set; } = 2000;
    public int DanceOfJelliesTimeOutSeconds { get; set; } = 2000;
    public int FestivalOfIceTimeOutSeconds { get; set; } = 2000;
}
