using HarmonyLib;
using Junimohost.Stardewsteamauth.V1;
using StardewModdingAPI;
using StardewValley.SDKs.Steam;

namespace JunimoServer.Services.GalaxyAuth
{
    public class GalaxyAuthService
    {
        public GalaxyAuthService(IMonitor monitor, IModHelper helper, Harmony harmony, StardewSteamAuthService.StardewSteamAuthServiceClient steamAuthClient)
        {
            GalaxyAuthOverrides.Initialize(monitor, helper, steamAuthClient);
            harmony.Patch(
                original: AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Initialize)),
                prefix: new HarmonyMethod(typeof(GalaxyAuthOverrides), nameof(GalaxyAuthOverrides.SteamHelperInitialize_Prefix))
            );
        }
    }
}