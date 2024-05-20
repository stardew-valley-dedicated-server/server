using StardewModdingAPI;

namespace JunimoServer.Services.AlwaysOnServer
{
    public class AlwaysOnConfig
    {
        public SButton ServerHotKey { get; set; } = SButton.F9;

        public string PetName { get; set; } = "Apples";
        public bool FarmCaveChoiceIsMushrooms { get; set; } = true;
        public bool IsCommunityCenterRun { get; set; } = true;

        public bool LockPlayerChests { get; set; } = true;

        public int EggHuntCountDownConfig { get; set; } = 300;
        public int FlowerDanceCountDownConfig { get; set; } = 300;
        public int LuauSoupCountDownConfig { get; set; } = 300;
        public int JellyDanceCountDownConfig { get; set; } = 300;
        public int GrangeDisplayCountDownConfig { get; set; } = 300;
        public int IceFishingCountDownConfig { get; set; } = 300;

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
}
