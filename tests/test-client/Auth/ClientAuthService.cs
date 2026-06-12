using System.Diagnostics;
using Galaxy.Api;
using HarmonyLib;
using JunimoTestClient.Diagnostics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;
using StardewValley.SDKs;
using StardewValley.SDKs.GogGalaxy;
using StardewValley.SDKs.GogGalaxy.Listeners;
using StardewValley.SDKs.Steam;
using Steamworks;

namespace JunimoTestClient.Auth;

/// <summary>
/// Patches SteamHelper to use the steam-auth service for Galaxy authentication.
/// Mirrors the server mod's GalaxyAuthService.PerformDeferredInitialization pattern
/// but adapted for a client (no GameServer, no lobby creation).
/// </summary>
public class ClientAuthService
{
    // Galaxy SDK credentials (same as server, embedded in all games using Galaxy SDK)
    private const string GalaxyClientId = "48767653913349277";
    private const string GalaxyClientSecret =
        "58be5c2e55d7f535cf8c4b6bbc09d185de90b152c8c42703cc13502465f0d04a";

    private static IMonitor _monitor = null!;
    private static IModHelper _helper = null!;
    private static SteamAuthClient? _authClient;
    private static bool _galaxyInitComplete;
    private static string? _clientSteamId;

    // Galaxy listeners (must be kept alive; prevent GC)
    private static IAuthListener? _authListener;
    private static IOperationalStateChangeListener? _stateChangeListener;

    // Galaxy fitness state. Read by the broker (via /health → HealthWatchdog) so
    // Steam-required leases skip Galaxy-broken clients. Stored as int (-1=null,
    // 0=false, 1=true) because Nullable<bool> can't be `volatile`. _galaxyReady
    // is true for both genuine logged-on state and LAN-only mode (Steam-required
    // leases are separately filtered by Steam-account ownership). State is
    // diagnostic only.
    private const int GalaxyReadyNull = -1;
    private const int GalaxyReadyFalse = 0;
    private const int GalaxyReadyTrue = 1;
    private static int _galaxyReady = GalaxyReadyNull;
    private static volatile string _galaxyState = "uninitialized";

    public static bool? GalaxyReady
    {
        get
        {
            var v = Volatile.Read(ref _galaxyReady);
            return v == GalaxyReadyNull ? null : v == GalaxyReadyTrue;
        }
    }
    public static string GalaxyState => _galaxyState;

    private readonly Harmony _harmony;
    private readonly string? _steamAuthUrl;
    private static int _accountIndex;

    public ClientAuthService(IModHelper helper, IMonitor monitor, Harmony harmony)
    {
        _monitor = monitor;
        _helper = helper;
        _harmony = harmony;
        _steamAuthUrl = Environment.GetEnvironmentVariable("STEAM_AUTH_URL");

        var accountStr = Environment.GetEnvironmentVariable("SDVD_TEST_STEAM_ACCOUNT_INDEX");
        _accountIndex = int.TryParse(accountStr, out var idx) ? idx : 1;
    }

    public void Apply()
    {
        if (string.IsNullOrEmpty(_steamAuthUrl))
        {
            _monitor.Log(
                "STEAM_AUTH_URL not set, Steam/Galaxy features disabled, LAN-only mode",
                LogLevel.Info
            );
            ApplyDisabledPatches();
            return;
        }

        _monitor.Log(
            $"Steam auth service: {_steamAuthUrl} (account {_accountIndex})",
            LogLevel.Info
        );
        _authClient = new SteamAuthClient(_steamAuthUrl, _accountIndex, _monitor);
        ApplyAuthPatches();
    }

    // ========================================================================
    // SteamHelper reflection helpers (same pattern as server mod)
    // ========================================================================

    private static void IncrementConnectionProgress(SteamHelper instance) =>
        _helper
            .Reflection.GetProperty<int>(instance, "ConnectionProgress")
            .SetValue(instance.ConnectionProgress + 1);

    private static void SetActive(SteamHelper instance, bool value) =>
        _helper.Reflection.GetField<bool>(instance, "active").SetValue(value);

    private static void SetConnectionFinished(SteamHelper instance, bool value) =>
        _helper.Reflection.GetProperty<bool>(instance, "ConnectionFinished").SetValue(value);

    private static void SetGalaxyConnected(SteamHelper instance, bool value) =>
        _helper.Reflection.GetProperty<bool>(instance, "GalaxyConnected").SetValue(value);

    private static void SetNetworking(SteamHelper instance, SDKNetHelper networking) =>
        _helper.Reflection.GetField<SDKNetHelper>(instance, "networking").SetValue(networking);

    private static SDKNetHelper CreateSteamNetHelper()
    {
        var type = AccessTools.TypeByName("StardewValley.SDKs.Steam.SteamNetHelper");
        return (SDKNetHelper)Activator.CreateInstance(type)!;
    }

    // ========================================================================
    // Disabled mode patches (no STEAM_AUTH_URL)
    // ========================================================================

    private void ApplyDisabledPatches()
    {
        // LAN-only mode: report ready=true so the broker's wait strategy doesn't
        // block. requireSteam leases will fail the Steam-account-ownership filter
        // separately.
        SetGalaxyReady(true, "disabled");

        try
        {
            _harmony.Patch(
                AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Initialize)),
                prefix: new HarmonyMethod(
                    typeof(ClientAuthService),
                    nameof(Initialize_Disabled_Prefix)
                )
            );

            _harmony.Patch(
                AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Update)),
                prefix: new HarmonyMethod(typeof(ClientAuthService), nameof(Update_Disabled_Prefix))
            );

            _harmony.Patch(
                AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Shutdown)),
                prefix: new HarmonyMethod(typeof(ClientAuthService), nameof(Shutdown_Prefix))
            );

            PatchRequestFriendLobbyData();

            _monitor.Log("Steam patches applied (disabled mode)", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to apply disabled Steam patches: {ex.Message}", LogLevel.Error);
        }
    }

    private static bool Initialize_Disabled_Prefix(SteamHelper __instance)
    {
        _monitor.Log(
            "SteamHelper.Initialize skipped (no STEAM_AUTH_URL, LAN-only mode)",
            LogLevel.Info
        );

        // Mark connection as finished immediately so the CoopMenu doesn't get stuck
        // on "Connecting to online services...". LAN connections don't need Galaxy/Steam.
        SetActive(__instance, true);
        SetConnectionFinished(__instance, true);

        Game1.game1.IsMouseVisible = true;
        return false;
    }

    private static bool Update_Disabled_Prefix()
    {
        Game1.game1.IsMouseVisible = Game1.paused || Game1.options.hardwareCursor;
        return false;
    }

    private static bool Shutdown_Prefix()
    {
        _monitor.Log("SteamHelper.Shutdown skipped", LogLevel.Trace);
        _galaxyInitComplete = false;
        return false;
    }

    // ========================================================================
    // Auth mode patches (with STEAM_AUTH_URL)
    // ========================================================================

    private void ApplyAuthPatches()
    {
        try
        {
            _harmony.Patch(
                AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Initialize)),
                prefix: new HarmonyMethod(typeof(ClientAuthService), nameof(Initialize_Auth_Prefix))
            );

            _harmony.Patch(
                AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Update)),
                prefix: new HarmonyMethod(typeof(ClientAuthService), nameof(Update_Auth_Prefix))
            );

            _harmony.Patch(
                AccessTools.Method(typeof(SteamHelper), nameof(SteamHelper.Shutdown)),
                prefix: new HarmonyMethod(typeof(ClientAuthService), nameof(Shutdown_Prefix))
            );

            // Patch SteamUser.GetSteamID to return the client's Steam ID
            _harmony.Patch(
                AccessTools.Method(
                    typeof(Steamworks.SteamUser),
                    nameof(Steamworks.SteamUser.GetSteamID)
                ),
                prefix: new HarmonyMethod(
                    typeof(ClientAuthService),
                    nameof(SteamUser_GetSteamID_Prefix)
                )
            );

            // Patch SteamFriends.GetPersonaName to return "TestClient"
            _harmony.Patch(
                AccessTools.Method(
                    typeof(Steamworks.SteamFriends),
                    nameof(Steamworks.SteamFriends.GetPersonaName)
                ),
                prefix: new HarmonyMethod(
                    typeof(ClientAuthService),
                    nameof(SteamFriends_GetPersonaName_Prefix)
                )
            );

            // Patch SteamNetHelper.CreateClient to route Hybrid lobbies through GalaxyNetClient.
            // Hybrid = Steam invite code (S-prefix) resolved via Galaxy lobby. The game normally
            // creates SteamNetClient for these, which calls SteamMatchmaking.JoinLobby and
            // SteamNetworkingSockets.ConnectP2P (both require SteamAPI.Init() which is
            // unavailable in Docker (no Steam client daemon). GalaxyNetClient connects via
            // Galaxy P2P instead. The server accepts both (runs GalaxyNetServer alongside
            // SteamGameServerNetServer). Previously this worked by accident: the test ran
            // before the server's Steam lobby was ready, so InviteCode fell back to the
            // GOG code (G-prefix) which creates GalaxyNetClient directly. This patch makes
            // the Galaxy P2P path explicit and reliable.
            var steamNetHelperType = AccessTools.TypeByName(
                "StardewValley.SDKs.Steam.SteamNetHelper"
            );
            if (steamNetHelperType != null)
            {
                _harmony.Patch(
                    AccessTools.Method(steamNetHelperType, "CreateClient"),
                    prefix: new HarmonyMethod(
                        typeof(ClientAuthService),
                        nameof(CreateClient_Prefix)
                    )
                );
            }

            PatchRequestFriendLobbyData();

            _monitor.Log("Steam patches applied (auth mode)", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to apply auth Steam patches: {ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Replaces SteamHelper.Initialize with full Galaxy SDK initialization.
    /// Mirrors server's PerformDeferredInitialization but for client mode.
    /// </summary>
    private static bool Initialize_Auth_Prefix(SteamHelper __instance)
    {
        _monitor.Log("Initializing SteamHelper via steam-auth service...", LogLevel.Info);

        SetGalaxyReady(null, "pending");

        SetActive(__instance, true);

        // Run Galaxy init asynchronously to avoid blocking the game startup
        Task.Run(async () =>
        {
            try
            {
                await PerformGalaxyInitAsync(__instance);
            }
            catch (Exception ex)
            {
                _monitor.Log($"Galaxy initialization failed: {ex.Message}", LogLevel.Error);
                _monitor.Log(ex.ToString(), LogLevel.Debug);
                SetGalaxyReady(false, "failed");
                SetConnectionFinished(__instance, true);
            }
        });

        Game1.game1.IsMouseVisible = true;
        return false;
    }

    /// <summary>
    /// Full Galaxy SDK initialization mirroring the server mod's PerformDeferredInitialization.
    /// Steps:
    /// 1. Init Galaxy SDK
    /// 2. Wait for steam-auth service readiness
    /// 3. Get encrypted app ticket (carries ticketSha8 for cross-log correlation)
    /// 4. Create auth + state change listeners (closures capture ticketSha8 + signinStopwatch)
    /// 5. SignInSteam with the ticket
    /// 6. Pump Galaxy callbacks until logged on
    /// 7. Create SteamNetHelper and set ConnectionFinished
    /// </summary>
    private static async Task PerformGalaxyInitAsync(SteamHelper steamHelper)
    {
        if (_authClient == null)
        {
            _monitor.Log("Steam auth client not available", LogLevel.Error);
            SetGalaxyReady(false, "failed");
            SetConnectionFinished(steamHelper, true);
            return;
        }

        var accountIndex = _accountIndex;

        // Step 1: Initialize Galaxy SDK
        _monitor.Log("Initializing Galaxy SDK...", LogLevel.Debug);
        GalaxyInstance.Init(new InitParams(GalaxyClientId, GalaxyClientSecret, "."));
        _monitor.Log("Galaxy SDK initialized", LogLevel.Debug);

        // Step 3: Wait for steam-auth service to be ready.
        // Defense-in-depth: retry once before falling back to non-Galaxy mode. The
        // server-side login mutex should keep latency bounded, but this cheap retry
        // protects against transient network blips making a client permanently
        // Galaxy-incapable for the rest of the container's life.
        _monitor.Log("Waiting for steam-auth service readiness...", LogLevel.Info);
        var readyResponse = await _authClient.WaitForReadyAsync(timeoutSeconds: 30);
        if (readyResponse == null)
        {
            _monitor.Log(
                "Steam auth service not ready after 30s, retrying once before falling back...",
                LogLevel.Warn
            );
            readyResponse = await _authClient.WaitForReadyAsync(timeoutSeconds: 30);
        }
        if (readyResponse == null)
        {
            _monitor.Log(
                "Steam auth service not ready after retry, Galaxy features unavailable",
                LogLevel.Error
            );
            SetGalaxyReady(false, "failed");
            SetNetworking(steamHelper, CreateSteamNetHelper());
            SetConnectionFinished(steamHelper, true);
            return;
        }

        _clientSteamId = readyResponse.steam_id;
        _monitor.Log(
            $"Steam auth service ready (account {readyResponse.account}, SteamID: {_clientSteamId})",
            LogLevel.Info
        );

        // Step 4: Get encrypted app ticket
        var ticketResponse = await _authClient.GetAppTicketAsync();
        if (ticketResponse?.app_ticket == null)
        {
            _monitor.Log("Failed to get app ticket, Galaxy features unavailable", LogLevel.Error);
            SetGalaxyReady(false, "failed");
            SetNetworking(steamHelper, CreateSteamNetHelper());
            SetConnectionFinished(steamHelper, true);
            return;
        }

        var ticketSha8 = ticketResponse.sha8 ?? "";
        var ticketSource = ticketResponse.source ?? "unknown";

        _monitor.Log("App ticket received, signing into Galaxy...", LogLevel.Info);

        // Stopwatch is started just before SignInSteam so latencyMsFromSignin
        // measures the round-trip Galaxy needs to deliver the auth-listener callback.
        var signinStopwatch = new Stopwatch();

        // Step 2 (now after ticket is in hand): create Galaxy listeners with the ticket
        // sha8 + stopwatch closures. The closures carry per-pass identity through to
        // the Galaxy callback thread without needing AsyncLocal flow across that
        // boundary (see .claude/rules/asynclocal-pitfalls.md).
        _authListener = CreateGalaxyAuthListener(
            steamHelper,
            accountIndex,
            ticketSha8,
            signinStopwatch
        );
        _stateChangeListener = CreateGalaxyStateChangeListener(steamHelper);
        IncrementConnectionProgress(steamHelper); // listeners ready
        IncrementConnectionProgress(steamHelper); // ticket received

        // Step 5: Sign into Galaxy with the Steam app ticket
        var ticketBytes = Convert.FromBase64String(ticketResponse.app_ticket);
        var ticketLength = Convert.ToUInt32(ticketBytes.Length);

        ClientEventLog.Emit(
            "auth_galaxy_signin_requested",
            new
            {
                accountIndex,
                ticketSha8,
                ticketSource,
            }
        );
        signinStopwatch.Start();
        GalaxyInstance.User().SignInSteam(ticketBytes, ticketLength, "TestClient");

        IncrementConnectionProgress(steamHelper);

        // Step 6: Pump Galaxy callbacks until logged on (or 30s timeout)
        _monitor.Log("Pumping Galaxy callbacks, waiting for login...", LogLevel.Debug);
        var galaxyDeadline = DateTime.UtcNow.AddSeconds(30);
        while (!_galaxyInitComplete && DateTime.UtcNow < galaxyDeadline)
        {
            try
            {
                GalaxyInstance.ProcessData();
            }
            catch (Exception ex)
            {
                _monitor.Log($"GalaxyInstance.ProcessData() error: {ex.Message}", LogLevel.Trace);
            }
            await Task.Delay(50);
        }

        if (!_galaxyInitComplete)
        {
            _monitor.Log("Galaxy login timed out after 30s", LogLevel.Warn);
            ClientEventLog.Emit(
                "auth_galaxy_auth_failed",
                new
                {
                    accountIndex,
                    ticketSha8,
                    reason = "PUMP_TIMEOUT",
                    latencyMsFromSignin = signinStopwatch.ElapsedMilliseconds,
                }
            );
            SetGalaxyReady(false, "failed");
            // Still create networking so game doesn't crash
            SetNetworking(steamHelper, CreateSteamNetHelper());
            SetConnectionFinished(steamHelper, true);
        }
    }

    private static void SetGalaxyReady(bool? value, string state)
    {
        var encoded = value switch
        {
            null => GalaxyReadyNull,
            true => GalaxyReadyTrue,
            false => GalaxyReadyFalse,
        };
        Volatile.Write(ref _galaxyReady, encoded);
        _galaxyState = state;
    }

    /// <summary>
    /// Update patch: pump Galaxy callbacks (NOT GameServer.RunCallbacks; client doesn't run a game server).
    /// </summary>
    private static bool Update_Auth_Prefix()
    {
        if (_galaxyInitComplete)
        {
            try
            {
                GalaxyInstance.ProcessData();
            }
            catch (Exception ex)
            {
                _monitor.Log($"GalaxyInstance.ProcessData() error: {ex.Message}", LogLevel.Trace);
            }
        }

        Game1.game1.IsMouseVisible = Game1.paused || Game1.options.hardwareCursor;
        return false;
    }

    // ========================================================================
    // Friend lobby suppression
    // ========================================================================

    private void PatchRequestFriendLobbyData()
    {
        var steamNetHelperType = AccessTools.TypeByName("StardewValley.SDKs.Steam.SteamNetHelper");
        if (steamNetHelperType != null)
        {
            _harmony.Patch(
                AccessTools.Method(steamNetHelperType, "RequestFriendLobbyData"),
                prefix: new HarmonyMethod(
                    typeof(ClientAuthService),
                    nameof(RequestFriendLobbyData_Prefix)
                )
            );
        }
    }

    private static bool RequestFriendLobbyData_Prefix() => false;

    // ========================================================================
    // Steam ID patches
    // ========================================================================

    private static bool SteamUser_GetSteamID_Prefix(ref CSteamID __result)
    {
        if (!string.IsNullOrEmpty(_clientSteamId) && ulong.TryParse(_clientSteamId, out var id))
        {
            __result = new CSteamID(id);
        }
        else
        {
            // Fallback: obviously fake ID
            __result = new CSteamID(123456789);
        }
        return false;
    }

    private static bool SteamFriends_GetPersonaName_Prefix(ref string __result)
    {
        __result = "TestClient";
        return false;
    }

    // ========================================================================
    // SteamNetHelper client creation patch
    // ========================================================================

    /// <summary>
    /// Redirects Hybrid lobby connections to use GalaxyNetClient (Galaxy P2P)
    /// instead of SteamNetClient (Steam SDR) which requires SteamAPI.Init().
    /// </summary>
    private static bool CreateClient_Prefix(object lobby, ref Client __result)
    {
        if (lobby == null)
        {
            return true;
        }

        var lobbyType = lobby.GetType();
        var galaxyIdProp = lobbyType.GetProperty("GalaxyId");
        var lobbyTypeProp = lobbyType.GetProperty("LobbyType");
        if (galaxyIdProp == null || lobbyTypeProp == null)
        {
            return true;
        }

        // LobbyConnectionType: Steam=0, Galaxy=1, Hybrid=2, Invalid=3
        var connectionType = (int)lobbyTypeProp.GetValue(lobby)!;
        if (connectionType == 2) // Hybrid
        {
            var galaxyId = (ulong)galaxyIdProp.GetValue(lobby)!;
            _monitor.Log(
                $"Redirecting Hybrid lobby to GalaxyNetClient (Galaxy P2P, lobbyId={galaxyId})",
                LogLevel.Info
            );
            var multiplayer = _helper
                .Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer")
                .GetValue();
            __result = multiplayer.InitClient(new GalaxyNetClient(new GalaxyID(galaxyId)));
            return false;
        }

        return true;
    }

    // ========================================================================
    // Galaxy listener factories (mirror server's patterns)
    // ========================================================================

    /// <summary>
    /// Creates a Galaxy auth listener. Mirrors server's CreateSteamHelperGalaxyAuthListener.
    /// <para>
    /// <paramref name="ticketSha8"/> and <paramref name="signinStopwatch"/> are captured
    /// per-pass via closure rather than read from a static / AsyncLocal. The Galaxy SDK
    /// invokes these on its own thread; ambient context does not flow there
    /// (see <c>.claude/rules/asynclocal-pitfalls.md</c>).
    /// </para>
    /// </summary>
    private static IAuthListener CreateGalaxyAuthListener(
        SteamHelper steamHelper,
        int accountIndex,
        string ticketSha8,
        Stopwatch signinStopwatch
    )
    {
        var listenerType = AccessTools.TypeByName(
            "StardewValley.SDKs.GogGalaxy.Listeners.GalaxyAuthListener"
        );

        Action onSuccess = () =>
        {
            _monitor.Log("Galaxy auth success (client mode)", LogLevel.Info);
            ClientEventLog.Emit(
                "auth_galaxy_auth_success",
                new
                {
                    accountIndex,
                    ticketSha8,
                    latencyMsFromSignin = signinStopwatch.ElapsedMilliseconds,
                }
            );
            IncrementConnectionProgress(steamHelper);
        };

        Action<IAuthListener.FailureReason> onFailure = (reason) =>
        {
            _monitor.Log($"Galaxy auth failure: {reason}", LogLevel.Error);
            ClientEventLog.Emit(
                "auth_galaxy_auth_failed",
                new
                {
                    accountIndex,
                    ticketSha8,
                    reason = reason.ToString(),
                    latencyMsFromSignin = signinStopwatch.ElapsedMilliseconds,
                }
            );
            SetGalaxyReady(false, "failed");
            if (steamHelper.Networking == null)
            {
                SetNetworking(steamHelper, CreateSteamNetHelper());
            }

            SetConnectionFinished(steamHelper, true);
            SetGalaxyConnected(steamHelper, false);
        };

        Action onLost = () =>
        {
            _monitor.Log("Galaxy auth lost", LogLevel.Error);
            ClientEventLog.Emit("auth_galaxy_auth_lost", new { accountIndex, ticketSha8 });
            SetGalaxyReady(false, "lost");
            if (steamHelper.Networking == null)
            {
                SetNetworking(steamHelper, CreateSteamNetHelper());
            }

            SetConnectionFinished(steamHelper, true);
            SetGalaxyConnected(steamHelper, false);
        };

        return (IAuthListener)Activator.CreateInstance(listenerType, onSuccess, onFailure, onLost)!;
    }

    /// <summary>
    /// Creates a Galaxy state change listener. Mirrors server's CreateSteamHelperGalaxyStateChangeListener.
    /// When Galaxy reports "logged on" (bit 2), creates networking and marks connection finished.
    /// </summary>
    private static IOperationalStateChangeListener CreateGalaxyStateChangeListener(
        SteamHelper steamHelper
    )
    {
        var listenerType = AccessTools.TypeByName(
            "StardewValley.SDKs.GogGalaxy.Listeners.GalaxyOperationalStateChangeListener"
        );

        var onStateChange = new Action<uint>(
            (operationalState) =>
            {
                if (steamHelper.Networking != null)
                {
                    return;
                }

                if ((operationalState & 1) != 0)
                {
                    _monitor.Log("Galaxy signed in (client mode)", LogLevel.Debug);
                    IncrementConnectionProgress(steamHelper);
                }

                if ((operationalState & 2) != 0)
                {
                    _monitor.Log("Galaxy logged on (client mode)", LogLevel.Info);
                    SetNetworking(steamHelper, CreateSteamNetHelper());
                    IncrementConnectionProgress(steamHelper);
                    SetConnectionFinished(steamHelper, true);
                    SetGalaxyConnected(steamHelper, true);
                    _galaxyInitComplete = true;
                    SetGalaxyReady(true, "signed_in");
                }
            }
        );

        return (IOperationalStateChangeListener)
            Activator.CreateInstance(listenerType, onStateChange)!;
    }
}
