using StardewModdingAPI;

namespace JunimoBot
{
    public sealed class ModConfig
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 24643;

        public SButton AutomateToggleHotKey { get; set; } = SButton.F9;
    }
}