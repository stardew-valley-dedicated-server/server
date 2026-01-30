namespace JunimoServer.Services.Settings
{
    public class ServerSettings
    {
        public GameSettings Game { get; set; } = new();
        public ServerRuntimeSettings Server { get; set; } = new();
    }

    public class GameSettings
    {
        public string FarmName { get; set; } = "Junimo";
        public int FarmType { get; set; } = 0;
        public float ProfitMargin { get; set; } = 1.0f;
        public int StartingCabins { get; set; } = 1;
        public string SpawnMonstersAtNight { get; set; } = "auto";
    }

    public class ServerRuntimeSettings
    {
        public int MaxPlayers { get; set; } = 10;
        public string CabinStrategy { get; set; } = "CabinStack";
        public bool SeparateWallets { get; set; } = false;
        public string ExistingCabinBehavior { get; set; } = "KeepExisting";
        public bool VerboseLogging { get; set; } = false;
        public bool AllowIpConnections { get; set; } = false;
    }
}
