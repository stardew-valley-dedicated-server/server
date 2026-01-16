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

        /// <summary>
        /// Fixed value which can be found in Stardew Valley GalaxyHelper.cs and SteamHelper.cs
        /// </summary>
        private const string GalaxyClientId = "48767653913349277";

        /// <summary>
        /// Fixed value which can be found in Stardew Valley GalaxyHelper.cs and SteamHelper.cs
        /// </summary>
        private const string GalaxyClientSecret = "58be5c2e55d7f535cf8c4b6bbc09d185de90b152c8c42703cc13502465f0d04a";

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

        /// <summary>
        /// Steam app ticket fetcher instance (reused after authentication)
        /// </summary>
        private static SteamAppTicketFetcherHttp _steamAppTicketFetcher;

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
                _monitor.Log("GalaxySDK 'SteamHelper' replaced with 'GalaxyHelper'", LogLevel.Trace);
                replacementSdk = new GalaxyHelper();
            }

            // Return cached result and skip original
            __result = replacementSdk;
            return false;
        }

        // `ConnectionProgress` caps at 4 when using Galaxy, and at 5 when using Steam (+1 for EncryptedAppTicket)
        private static void IncrementGalaxyConnectionProgress(GalaxyHelper __instance) => _helper.Reflection.GetProperty<int>(__instance, "ConnectionProgress").SetValue(__instance.ConnectionProgress + 1);
        private static void SetGalaxyActive(GalaxyHelper __instance) => _helper.Reflection.GetField<bool>(__instance, "active").SetValue(true);
        private static void SetGalaxyConnectionFinished(GalaxyHelper __instance) => _helper.Reflection.GetProperty<bool>(__instance, "ConnectionFinished").SetValue(true);

        private string _steamUser = "";
        private string _steamPass = "";

        private static string GetSteamUsername()
        {
            return _instance._steamUser;
        }

        private static string GetSteamPassword()
        {
            return _instance._steamPass;
        }

        /// <summary>
        /// Modifies the Galaxy SDK to use our custom Steam encryptedAppTicket implementation,
        /// bypassing Steam Client SDK calls.
        /// </summary>
        private static bool GalaxyHelperInitialize_Prefix(GalaxyHelper __instance)
        {
            _monitor.Log("Initializing authentication for networking and invite codes...", LogLevel.Info);
            _monitor.Log("GalaxySDK initializing...", LogLevel.Debug);

            // Check if using external steam-auth service (two-container setup)
            var steamAuthUrl = Environment.GetEnvironmentVariable("STEAM_AUTH_URL");
            var isAuthenticated = !string.IsNullOrEmpty(steamAuthUrl)
                ? _instance.UseExternalSteamAuth(steamAuthUrl)
                : _instance.ShowLoginPrompt();

            if (!isAuthenticated)
            {
                _monitor.Log("Authentication skipped, invite codes are unavailable.", LogLevel.Info);
                _monitor.Log("You may still join using IP:HOST, but it is strongly recommended to use Steam so that farmer ownership is assigned correctly.", LogLevel.Info);
                SetGalaxyConnectionFinished(__instance);

                // Always skip original method
                return false;
            }

            try
            {
                GalaxyInstance.Init(new InitParams(GalaxyClientId, GalaxyClientSecret, "."));
                authListener = _instance.CreateGalaxyAuthListener(__instance);
                stateChangeListener = _instance.CreateGalaxyStateChangeListener(__instance);
            }
            catch (Exception ex)
            {
                _monitor.Log("GalaxySDK initialization failed:", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Error);
            }

            try
            {
                // Reason: signing in
                IncrementGalaxyConnectionProgress(__instance);

                // Sign into galaxy to enable lobby matchmaking and networking
                _instance.SignIntoGalaxy();

                // Ensures that galaxy listener callbacks are processed
                SetGalaxyActive(__instance);

                // Reason: retrieving encrypted app ticket
                IncrementGalaxyConnectionProgress(__instance);
            }
            catch (Exception ex)
            {
                // Check if this is a user-facing error (no need for stack trace)
                var isUserFacingError = ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("Invalid Steam password", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("Incorrect Steam Guard", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("account is locked", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("session may have been replaced", StringComparison.OrdinalIgnoreCase);

                if (isUserFacingError)
                {
                    _monitor.Log($"GalaxySDK sign in failed: {ex.Message}", LogLevel.Error);
                }
                else
                {
                    // Technical error - show full details
                    _monitor.Log("GalaxySDK sign in failed:", LogLevel.Error);
                    _monitor.Log(ex.ToString(), LogLevel.Error);
                }

                SetGalaxyConnectionFinished(__instance);
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
                _instance.ServerCSteamId = new CSteamID(ServerSteamId);
            }

            __result = _instance.ServerCSteamId;
            _monitor.Log($"GalaxySDK using Steam ID: {ServerSteamId}", LogLevel.Trace);
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
            _monitor.Log($"GalaxySDK using name: {__result}", LogLevel.Trace);
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

                // Write invite code to file for CLI display
                InviteCodeFile.Write(__result);

                // Print startup banner with invite code
                ServerBanner.Print(_monitor, _helper);

                return false;
            }
            catch (Exception ex)
            {
                _monitor.Log($"GalaxySDK failed generating invite code:", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Error);
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
                _monitor.Log($"GalaxySDK auth success", LogLevel.Trace);
                onGalaxyAuthSuccessOriginal.Invoke();
            });

            var onGalaxyAuthFailure = new Action<IAuthListener.FailureReason>((reason) =>
            {
                _monitor.Log($"GalaxySDK auth failed: {reason}", LogLevel.Error);
                onGalaxyAuthFailureOriginal.Invoke(reason);
            });

            var onGalaxyAuthLost = new Action(() =>
            {
                _monitor.Log("GalaxySDK auth lost, signing in again...", LogLevel.Info);

                try
                {
                    _monitor.Log("GalaxySDK attempting sign out...", LogLevel.Info);
                    GalaxyInstance.User().SignOut();
                }
                catch (Exception e)
                {
                    _monitor.Log(e.ToString(), LogLevel.Error);
                }

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
                _monitor.Log($"GalaxySDK auth state changed to '{num}'", LogLevel.Info);
                onGalaxyStateChangeOriginal.Invoke(num);

                // TODO: Come back to this, should have low priority
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
            _monitor.Log("GalaxySDK retrieving steam app ticket...", LogLevel.Debug);

            // Use the already-authenticated fetcher (created in EnsureAuthenticatedNow or UseExternalSteamAuth)
            if (_steamAppTicketFetcher == null)
            {
                throw new InvalidOperationException("Steam authentication was not completed. Call EnsureAuthenticatedNow first.");
            }

            // Get the encrypted app ticket
            var steamAppTicketFetcherResult = _steamAppTicketFetcher.GetTicket();
            return Convert.FromBase64String(steamAppTicketFetcherResult.Ticket);
        }

        private bool SignIntoGalaxy()
        {
            _monitor.Log("GalaxySDK signing in...", LogLevel.Debug);

            var appTicket = GetEncryptedAppTicketSteam();
            var appTicketLength = Convert.ToUInt32(appTicket.Length);

            GalaxyInstance.User().SignInSteam(appTicket, appTicketLength, ServerName);

            return true;
        }

        /// <summary>
        /// Use external steam-auth service (two-container setup).
        /// Authentication is already done via 'docker compose run -it steam-auth setup'.
        /// </summary>
        private bool UseExternalSteamAuth(string steamAuthUrl)
        {
            // Create fetcher in "external" mode - no login flow, just fetch tickets
            _steamAppTicketFetcher = new SteamAppTicketFetcherHttp(
                monitor: _monitor,
                steamAuthUrl: steamAuthUrl,
                timeoutMs: 30000,
                externalMode: true  // Skip login flow, assume already authenticated
            );

            // Verify the service is healthy and logged in
            try
            {
                _steamAppTicketFetcher.VerifyServiceReady();
                _monitor.Log("Steam-auth service is ready ✓", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                _monitor.Log($"Steam-auth service not ready: {ex.Message}", LogLevel.Error);
                _monitor.Log("Make sure you ran: docker compose run -it steam-auth setup", LogLevel.Error);
                return false;
            }
        }

        private bool ShowLoginPrompt()
        {
            _monitor.Log("***********************************************************************", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("*    Choose Steam authentication method:                              *", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("*    [1] Use credentials from config (STEAM_USERNAME/STEAM_PASSWORD)  *", LogLevel.Info);
            _monitor.Log("*    [2] Enter credentials now (prompt)                               *", LogLevel.Info);
            _monitor.Log("*    [3] Use QR code (scan with Steam mobile app)                     *", LogLevel.Info);
            _monitor.Log("*    [n] Skip authentication (no invite codes)                        *", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("***********************************************************************", LogLevel.Info);

            // Wait for expected user input
            var currentValue = "";
            var attemptCount = 0;
            while(currentValue != "1" && currentValue != "2" && currentValue != "3" && currentValue != "n")
            {
                if (attemptCount > 0)
                {
                    _monitor.Log("Type '1', '2', '3', or 'n' to continue:", LogLevel.Info);
                }
                currentValue = Console.ReadLine();
                attemptCount++;
            }

            if (currentValue == "n")
            {
                // Skip authentication - return false to indicate skip
                return false;
            }

            if (currentValue == "1")
            {
                // Verify that env vars are actually set
                var envUser = Environment.GetEnvironmentVariable("STEAM_USERNAME");
                var envPass = Environment.GetEnvironmentVariable("STEAM_PASSWORD");

                if (string.IsNullOrEmpty(envUser) || string.IsNullOrEmpty(envPass))
                {
                    _monitor.Log("", LogLevel.Info);
                    _monitor.Log("ERROR: STEAM_USERNAME and STEAM_PASSWORD environment variables are not set!", LogLevel.Error);
                    _monitor.Log("Please set these variables in your .env file or choose a different option.", LogLevel.Info);
                    _monitor.Log("", LogLevel.Info);

                    // Retry - show prompt again
                    return ShowLoginPrompt();
                }

                // Set instance fields from env vars (allows token reuse)
                _instance._steamUser = envUser;
                _instance._steamPass = envPass;
                _monitor.Log("Using credentials from environment variables ✓", LogLevel.Info);

                // Authenticate immediately after setting credentials
                EnsureAuthenticatedNow();
                return true;
            }

            if (currentValue == "3")
            {
                // QR code login - force fresh login
                _instance._steamUser = "USE_QR_CODE";
                _instance._steamPass = "";

                // Authenticate immediately after setting credentials
                EnsureAuthenticatedNow();
                return true;
            }

            // Option 2: Prompt for credentials
            var shouldPromptForCredentials = currentValue == "2";
            if (shouldPromptForCredentials)
            {
                // Signal password mode to command-loop for the NEXT read after username
                var passwordModeFile = "/tmp/smapi-password-mode";
                try
                {
                    System.IO.File.WriteAllText(passwordModeFile, "1");
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Warning: Could not create password mode signal file: {ex.Message}", LogLevel.Debug);
                }

                _monitor.Log("Enter your Steam username:", LogLevel.Info);
                _instance._steamUser = Console.ReadLine();
                _monitor.Log("Steam username received ✓", LogLevel.Info);

                _monitor.Log("Enter your Steam password:", LogLevel.Info);
                _instance._steamPass = Console.ReadLine();
                _monitor.Log("Steam password received ✓", LogLevel.Info);

                // Clean up signal file after password is entered
                try
                {
                    if (System.IO.File.Exists(passwordModeFile))
                    {
                        System.IO.File.Delete(passwordModeFile);
                    }
                }
                catch (Exception ex)
                {
                    _monitor.Log($"Warning: Could not delete password mode signal file: {ex.Message}", LogLevel.Debug);
                }

                // Authenticate immediately after setting credentials
                EnsureAuthenticatedNow();
            }

            return true;
        }

        /// <summary>
        /// Initialize the Steam fetcher and authenticate immediately
        /// </summary>
        private static void EnsureAuthenticatedNow()
        {
            _steamAppTicketFetcher = new SteamAppTicketFetcherHttp(
                monitor: _monitor,
                steamAuthUrl: Environment.GetEnvironmentVariable("STEAM_AUTH_URL") ?? "http://localhost:3001",
                timeoutMs: 60000
            );

            var username = GetSteamUsername();
            var password = GetSteamPassword();
            _steamAppTicketFetcher.EnsureAuthenticated(username, password);
        }
    }
}
