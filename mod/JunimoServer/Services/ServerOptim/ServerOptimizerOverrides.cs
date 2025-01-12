using StardewModdingAPI;
using StardewValley;
using xTile.Display;

namespace JunimoServer.Services.ServerOptim
{
    public class ServerOptimizerOverrides
    {
        private static IMonitor _monitor;
        private static bool _shouldDrawFrame = true;

        public static void Initialize(IMonitor monitor)
        {
            _monitor = monitor;
        }


        public static bool AssignNullDisplay_Prefix()
        {
            Game1.mapDisplayDevice = new NullDisplayDevice();
            return false;
        }

        public static bool ReturnNullDisplay_Prefix(IDisplayDevice __result)
        {
            __result = new NullDisplayDevice();
            return false;
        }

        public static void CreateLobby_Prefix(ref ServerPrivacy privacy, ref uint memberLimit)
        {
            // Used by GoG
            privacy = ServerPrivacy.Public;
            memberLimit = 150;
        }

        public static bool Disable_Prefix()
        {
            return false;
        }

        // ReSharper disable once InconsistentNaming
        public static bool Draw_Prefix(GameRunner __instance)

        {
            if (!_shouldDrawFrame)
            {
                __instance.SuppressDraw();
            }

            return _shouldDrawFrame;
        }

        public static void DisableDrawing()
        {
            _shouldDrawFrame = false;
        }

        public static void EnableDrawing()
        {
            _shouldDrawFrame = true;
        }
    }
}
