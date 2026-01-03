using System;
using System.Linq;
using HarmonyLib;
using Galaxy.Api;
using Steamworks;
using StardewModdingAPI;
using StardewValley;
using StardewValley.SDKs;
using StardewValley.SDKs.Steam;
using StardewValley.SDKs.GogGalaxy;
using StardewValley.SDKs.GogGalaxy.Internal;
using StardewValley.Menus;
using StardewValley.SDKs.GogGalaxy.Listeners;
using System.Threading.Tasks;
using JunimoServer.Util;

namespace JunimoServer.Services.Auth
{
    public class GalaxyAuthService : ModService
    {
        /// <summary>
        /// Static reference for Harmony patches
        /// </summary>
        private static GalaxyAuthService _instance;
        private static IMonitor _monitor;
        private static IModHelper _helper;

        /// <summary>
        /// TODO: Double-check where/how exactly this is used/displayed, but should be ok to fake this value.
        /// </summary>
        private const string ServerName = "GalaxyTest";

        /// <summary>
        /// `SteamHostId` is only written to but not read from, so we should be fine to fake this value.
        /// Still need to double-check that Galaxy itself doesn't need or want it for whatever reason.
        /// </summary>
        private const long ServerSteamId = 123456789;

        private static IOperationalStateChangeListener stateChangeListener;
        private static IAuthListener authListener;

        /// <summary>
        /// Backing field for memoization
        /// </summary>
        private static SDKHelper replacementSdk;

        /// <summary>
        /// Backing field for memoization
        /// </summary>
        private CSteamID ServerCSteamId;

        public GalaxyAuthService(IMonitor monitor, IModHelper helper, Harmony harmony)
        {
            // Set instance variables for use in static harmony patches
            _instance = this;
            _monitor = monitor;
            _helper = helper;

            var galaxyAuthServiceType = typeof(GalaxyAuthService);

            // Replace `SteamHelper` with `GalaxyHelper`,
            // bypassing most Steam Client calls
            harmony.Patch(
                original: AccessTools.PropertyGetter(typeof(Program), "sdk"),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(ProgramSdk_Prefix)));

            // Replace `encryptedAppTicketService` for `GalaxyInstance.User().SignInSteam` with custom implementation,
            // bypassing Steam Client SDK calls
            harmony.Patch(
                original: AccessTools.Method(typeof(GalaxyHelper), nameof(GalaxyHelper.Initialize)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(GalaxyHelperInitialize_Prefix)));

            // Replace SteamHostId for lobby data (clarify: so that client doesn't fail?)
            // bypassing Steam Client SDK calls
            harmony.Patch(
                original: AccessTools.Method(typeof(SteamUser), nameof(SteamUser.GetSteamID)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamUser_GetSteamID_Prefix)));

            // Replace StardewDisplayName/SteamHostId for lobby data,
            // bypassing Steam Client SDK calls
            harmony.Patch(
                original: AccessTools.Method(typeof(SteamFriends), nameof(SteamFriends.GetPersonaName)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(SteamFriends_GetPersonaName_Prefix)));

            // Patch GalaxySocket.GetInviteCode to use "G" prefix for Galaxy lobbies, to make clients connect properly
            harmony.Patch(
                original: AccessTools.Method(typeof(GalaxySocket), nameof(GalaxySocket.GetInviteCode)),
                prefix: new HarmonyMethod(galaxyAuthServiceType, nameof(GalaxySocket_GetInviteCode_Prefix)));
        }

        private static bool ProgramSdk_Prefix(ref SDKHelper __result)
        {
            if (replacementSdk == null)
            {
                _monitor.Log("Replacing 'SteamHelper' with 'GalaxyHelper'", LogLevel.Info);
                replacementSdk = new GalaxyHelper();
            }

            // Return cached result and skip original
            __result = replacementSdk;
            return false;
        }

        /// <summary>
        /// Modifies the Galaxy SDK to use our custom Steam encryptedAppTicket implementation,
        /// bypassing Steam Client SDK calls.
        /// </summary>
        private static bool GalaxyHelperInitialize_Prefix(GalaxyHelper __instance)
        {
            _monitor.Log("Initializing GalaxySDK", LogLevel.Info);

            var SetGalaxyActive = () => _helper.Reflection.GetField<bool>(__instance, "active").SetValue(true);
            var SetGalaxyConnectionFinished = () => _helper.Reflection.GetProperty<bool>(__instance, "ConnectionFinished").SetValue(true);

            // Note that `ConnectionProgress` increments up to 4 when using Galaxy, and up to 5 (+1 for EncryptedAppTicket) when using Steam
            var IncrementGalaxyConnectionProgress = () => _helper.Reflection.GetProperty<int>(__instance, "ConnectionProgress").SetValue(__instance.ConnectionProgress + 1);

            try
            {
                GalaxyInstance.Init(new InitParams("48767653913349277", "58be5c2e55d7f535cf8c4b6bbc09d185de90b152c8c42703cc13502465f0d04a", "."));
                authListener = _instance.CreateGalaxyAuthListener(__instance);
                stateChangeListener = _instance.CreateGalaxyStateChangeListener(__instance);
            }
            catch (Exception ex)
            {
                _monitor.Log("Error initializing the Galaxy API.", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Error);
            }

            try
            {
                // Reason: signing in
                IncrementGalaxyConnectionProgress();

                // Sign into galaxy to enable lobby matchmaking and networking
                _instance.SignIntoGalaxy();

                // Ensures that galaxy listener callbacks are processed
                SetGalaxyActive();

                // Reason: retrieving encrypted app ticket
                IncrementGalaxyConnectionProgress();
            }
            catch (Exception exception)
            {
                _monitor.Log("Signing into GalaxySDK failed:", LogLevel.Error);
                _monitor.Log(exception.ToString(), LogLevel.Error);

                //
                SetGalaxyConnectionFinished();
            }

            // Always skip original method
            return false;
        }

        /// <summary>
        /// Provide fake Steam ID for `HostSteamId`, bypassing Steam Client SDK calls.
        /// </summary>
        private static bool SteamUser_GetSteamID_Prefix(ref CSteamID __result)
        {
            if (_instance.ServerCSteamId != default)
            {
                _monitor.Log("Replacing 'SteamHelper' with 'GalaxyHelper'", LogLevel.Info);
                _instance.ServerCSteamId = new CSteamID(ServerSteamId);
            }

            __result = _instance.ServerCSteamId;
            _monitor.Log($"Using Steam ID: {ServerSteamId}", LogLevel.Debug);
            return false;
        }

        /// <summary>
        /// Provide the server display name for `HostDisplayName/StardewDisplayName`, bypassing Steam Client SDK calls.<br/><br/>
        ///  - `StardewDisplayName` is only set in SteamHelper, but we replaced it with GalaxyHelper<br/>
        ///  - `HostDisplayName` is the fallback, but calls `SteamFriends.GetPersonaName()` which is not available without Steam<br/>
        /// </summary>
        private static bool SteamFriends_GetPersonaName_Prefix(ref string __result)
        {
            __result = ServerName;
            _monitor.Log($"Using persona name: {__result}", LogLevel.Debug);
            return false;
        }

        /// <summary>
        /// Prefix invite codes with "G" for Galaxy lobbies, rather than "S" for Steam.
        /// </summary>
        private static bool GalaxySocket_GetInviteCode_Prefix(object __instance, ref string __result)
        {
            try
            {
                var lobby = _helper.Reflection.GetField<GalaxyID>(__instance, "lobby").GetValue();

                if (lobby == null)
                {
                    return false;
                }

                // Use "G" prefix for Galaxy lobbies instead of "S" for Steam
                __result = GalaxyNetHelper.GalaxyInvitePrefix + Base36.Encode(lobby.GetRealID());
                _monitor.Log($"Generated Galaxy invite code: {__result}", LogLevel.Info);

                return false;
            }
            catch (Exception ex)
            {
                _monitor.Log($"Error generating invite code: {ex}", LogLevel.Error);
                return true;
            }
        }

        /// <summary>
        /// Replicate original code for `GalaxyHelper.Initialize`.
        /// </summary>
        private IAuthListener CreateGalaxyAuthListener(GalaxyHelper galaxyHelper)
        {
            var listenerType = AccessTools.TypeByName("StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener");
            var onGalaxyAuthSuccessOriginal = _helper.Reflection.GetMethod(galaxyHelper, "onGalaxyAuthSuccess");
            var onGalaxyAuthFailureOriginal = _helper.Reflection.GetMethod(galaxyHelper, "onGalaxyAuthFailure");

            var onGalaxyAuthSuccess = new Action(() =>
            {
                _monitor.Log($"Galaxy auth success", LogLevel.Info);
                onGalaxyAuthSuccessOriginal.Invoke();
            });
            var onGalaxyAuthFailure = new Action<IAuthListener.FailureReason>((reason) =>
            {
                _monitor.Log($"Galaxy auth failed: {reason}", LogLevel.Error);
                onGalaxyAuthFailureOriginal.Invoke(reason);
            });
            var onGalaxyAuthLost = new Action(() =>
            {
                _monitor.Log("Galaxy auth lost, signing in again...", LogLevel.Info);

                try
                {
                    _monitor.Log("Attempting sign out", LogLevel.Info);
                    GalaxyInstance.User().SignOut();
                }
                catch (Exception e)
                {
                    _monitor.Log(e.ToString(), LogLevel.Error);
                }

                _monitor.Log("Signing In", LogLevel.Info);
                _instance?.SignIntoGalaxy();
            });

            return (IAuthListener)Activator.CreateInstance(listenerType, onGalaxyAuthSuccess, onGalaxyAuthFailure, onGalaxyAuthLost);
        }

        /// <summary>
        /// Replicate original code for `GalaxyHelper.Initialize`, with modification for `StardewDisplayName`.
        /// </summary>
        private IOperationalStateChangeListener CreateGalaxyStateChangeListener(GalaxyHelper galaxyHelper)
        {
            var listenerType = AccessTools.TypeByName("StardewValley.SDKs.GogGalaxy.Listeners.GalaxyOperationalStateChangeListener");
            var onGalaxyStateChangeOriginal = _helper.Reflection.GetMethod(galaxyHelper, "onGalaxyStateChange");

            var onGalaxyStateChange = new Action<uint>((num) =>
            {
                _monitor.Log($"Galaxy auth state changed to '{num}'", LogLevel.Info);
                onGalaxyStateChangeOriginal.Invoke(num);

                // By default, `StardewDisplayName` is set in `SteamHelper` but not in `GalaxyHelper`.
                // We do so here to ensure that clients skip `GalaxyInstance.Friends().GetFriendPersonaName`,
                // which would fail because the server is usually not a friend of the client.
                // try
                // {
                //     var key = "StardewDisplayName";
                //     var value = ServerName;
                //     _monitor.Log("Setting user data: 'StardewDisplayName'.", LogLevel.Error);
                //     _monitor.Log(_monitor.Dump(new { key, value }), LogLevel.Error);
                //     GalaxyInstance.User().SetUserData(key, value);
                // }
                // catch (Exception exception)
                // {
                //     _monitor.Log("Failed to set 'StardewDisplayName'.", LogLevel.Error);
                //     _monitor.Log(exception.ToString(), LogLevel.Error);
                // }
            });

            return (IOperationalStateChangeListener)Activator.CreateInstance(listenerType, onGalaxyStateChange);
        }

        /// <summary>
        /// Get the encrypted app ticket from Steam without using a real Steam client.
        /// </summary>
        private byte[] GetEncryptedAppTicketSteam()
        {
            var steamAppTicketFetcher = new SteamAppTicketFetcher(
                username: Environment.GetEnvironmentVariable("STEAM_USER"),
                password: Environment.GetEnvironmentVariable("STEAM_PASS"),
                timeoutMs: 60000
            );
            var steamAppTicketFetcherResult = steamAppTicketFetcher.GetTicket();
            return Convert.FromBase64String(steamAppTicketFetcherResult.Ticket);
        }

        private bool SignIntoGalaxy()
        {
            _monitor.Log("Signing into GalaxySDK with Steam ticket", LogLevel.Info);
            var appTicket = GetEncryptedAppTicketSteam();
            var appTicketLength = Convert.ToUInt32(appTicket.Length);
            GalaxyInstance.User().SignInSteam(appTicket, appTicketLength, ServerName);
            return true;
        }
    }
}
