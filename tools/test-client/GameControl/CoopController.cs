using System.Reflection;
using JunimoTestClient.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
using Steamworks;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Controls Co-op menu interactions for joining games.
/// </summary>
public class CoopController
{
    private readonly IMonitor _monitor;
    private readonly ReflectionHelper _reflection;

    public CoopController(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        _reflection = new ReflectionHelper(helper);
    }

    /// <summary>
    /// Get the current CoopMenu if active.
    /// </summary>
    private CoopMenu? GetCoopMenu()
    {
        // Check if it's a submenu of TitleMenu
        if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is CoopMenu coopSub)
            return coopSub;

        // Check if it's the active menu directly
        if (Game1.activeClickableMenu is CoopMenu coopDirect)
            return coopDirect;

        return null;
    }

    /// <summary>
    /// Get the current FarmhandMenu if active.
    /// </summary>
    private FarmhandMenu? GetFarmhandMenu()
    {
        if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is FarmhandMenu farmhandSub)
            return farmhandSub;

        if (Game1.activeClickableMenu is FarmhandMenu farmhandDirect)
            return farmhandDirect;

        return null;
    }

    /// <summary>
    /// Get the current TitleTextInputMenu if active (used for invite code / LAN input).
    /// </summary>
    private TitleTextInputMenu? GetTextInputMenu()
    {
        if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is TitleTextInputMenu inputSub)
            return inputSub;

        if (Game1.activeClickableMenu is TitleTextInputMenu inputDirect)
            return inputDirect;

        return null;
    }

    /// <summary>
    /// Open the invite code input dialog by clicking the "Enter Invite Code..." slot.
    /// After calling this, wait for TitleTextInputMenu to appear, then call SubmitInviteCode.
    /// </summary>
    public JoinResult OpenInviteCodeMenu()
    {
        try
        {
            var coopMenu = GetCoopMenu();
            if (coopMenu == null)
            {
                return new JoinResult { Success = false, Error = "Not in coop menu" };
            }

            // Check if networking supports invite codes
            if (!_reflection.SupportsInviteCodes())
            {
                return new JoinResult { Success = false, Error = "Invite codes not supported" };
            }

            // Find the InviteCodeSlot in the menu slots
            var menuSlotsField = typeof(LoadGameMenu).GetField("menuSlots",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (menuSlotsField?.GetValue(coopMenu) is not List<LoadGameMenu.MenuSlot> slots)
            {
                return new JoinResult { Success = false, Error = "Could not access menu slots" };
            }

            // Find the InviteCodeSlot (it's a nested class in CoopMenu)
            var inviteSlot = slots.FirstOrDefault(s => s.GetType().Name == "InviteCodeSlot");
            if (inviteSlot == null)
            {
                return new JoinResult { Success = false, Error = "Invite code slot not found in menu" };
            }

            // Activate it - this opens TitleTextInputMenu
            inviteSlot.Activate();

            _monitor.Log("Opened invite code input menu", LogLevel.Trace);

            return new JoinResult
            {
                Success = true,
                Message = "Invite code menu opened"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to open invite code menu: {ex.Message}", LogLevel.Error);
            return new JoinResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Submit an invite code in the TitleTextInputMenu.
    /// Call this after OpenInviteCodeMenu and waiting for the menu to appear.
    /// </summary>
    public JoinResult SubmitInviteCode(string inviteCode)
    {
        try
        {
            var inputMenu = GetTextInputMenu();
            if (inputMenu == null)
            {
                return new JoinResult { Success = false, Error = "Not in text input menu" };
            }

            // Set the invite code text
            inputMenu.textBox.Text = inviteCode;

            // Commented out for consistent logs, we
            // _monitor.Log($"Submitting invite code: {inviteCode}", LogLevel.Trace);

            // Submit by calling textBoxEnter (same as pressing Enter or clicking OK)
            inputMenu.textBoxEnter(inputMenu.textBox);

            return new JoinResult
            {
                Success = true,
                Message = "Connecting to lobby..."
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to submit invite code: {ex.Message}", LogLevel.Error);
            return new JoinResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Enter an invite code and attempt to join the game.
    /// </summary>

    /// <summary>
    /// Enter a LAN/IP address and attempt to join.
    /// </summary>
    public JoinResult EnterLanAddress(string address)
    {
        try
        {
            var coopMenu = GetCoopMenu();
            if (coopMenu == null)
            {
                return new JoinResult { Success = false, Error = "Not in coop menu" };
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                address = "localhost";
            }

            // Create LAN client and open FarmhandMenu
            var multiplayer = _reflection.GetMultiplayer();
            var client = multiplayer.InitClient(new LidgrenClient(address));
            SetMenu(new FarmhandMenu(client));

            _monitor.Log($"Connecting to LAN address: {address}", LogLevel.Trace);

            return new JoinResult
            {
                Success = true,
                Message = $"Connecting to {address}..."
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to connect to LAN: {ex.Message}", LogLevel.Error);
            return new JoinResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get information about available farmhand slots.
    /// </summary>
    public FarmhandSelectionInfo GetFarmhandSlots()
    {
        var farmhandMenu = GetFarmhandMenu();
        if (farmhandMenu == null)
        {
            return new FarmhandSelectionInfo
            {
                InFarmhandMenu = false,
                Error = "Not in farmhand selection menu"
            };
        }

        var info = new FarmhandSelectionInfo
        {
            InFarmhandMenu = true,
            IsConnecting = farmhandMenu.gettingFarmhands || farmhandMenu.approvingFarmhand,
            Slots = new List<FarmhandSlotInfo>()
        };

        // Get menu slots via reflection
        var menuSlotsField = typeof(LoadGameMenu).GetField("menuSlots",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (menuSlotsField?.GetValue(farmhandMenu) is List<LoadGameMenu.MenuSlot> slots)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var slotInfo = new FarmhandSlotInfo { Index = i };

                // SaveFileSlot and FarmhandSlot have a public Farmer field
                if (slot is LoadGameMenu.SaveFileSlot saveSlot)
                {
                    var farmer = saveSlot.Farmer;
                    if (farmer != null)
                    {
                        try
                        {
                            // Access only basic properties that don't trigger serialization
                            slotInfo.Name = farmer.Name ?? "";
                            slotInfo.IsCustomized = farmer.isCustomized.Value;
                        }
                        catch (Exception ex)
                        {
                            _monitor.Log($"Error reading farmer slot {i}: {ex.Message}", LogLevel.Trace);
                            // Slot exists but farmer data couldn't be read
                            slotInfo.Name = "(error reading)";
                        }
                    }
                    else
                    {
                        // Empty slot
                        slotInfo.IsEmpty = true;
                    }
                }

                info.Slots.Add(slotInfo);
            }
        }

        return info;
    }

    /// <summary>
    /// Select a farmhand slot by index.
    /// </summary>
    public JoinResult SelectFarmhand(int slotIndex)
    {
        try
        {
            var farmhandMenu = GetFarmhandMenu();
            if (farmhandMenu == null)
            {
                return new JoinResult { Success = false, Error = "Not in farmhand selection menu" };
            }

            // Get menu slots via reflection
            var menuSlotsField = typeof(LoadGameMenu).GetField("menuSlots",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (menuSlotsField?.GetValue(farmhandMenu) is not List<LoadGameMenu.MenuSlot> slots)
            {
                return new JoinResult { Success = false, Error = "Could not access menu slots" };
            }

            if (slotIndex < 0 || slotIndex >= slots.Count)
            {
                return new JoinResult { Success = false, Error = $"Invalid slot index: {slotIndex}. Available: 0-{slots.Count - 1}" };
            }

            // Activate the slot
            var slot = slots[slotIndex];
            slot.Activate();

            _monitor.Log($"Selected farmhand slot {slotIndex}", LogLevel.Trace);

            return new JoinResult
            {
                Success = true,
                Message = $"Selected farmhand slot {slotIndex}, joining game..."
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to select farmhand: {ex.Message}", LogLevel.Error);
            return new JoinResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Set a menu (handles TitleMenu submenu vs direct menu).
    /// </summary>
    private void SetMenu(IClickableMenu menu)
    {
        if (Game1.activeClickableMenu is TitleMenu)
        {
            TitleMenu.subMenu = menu;
        }
        else
        {
            Game1.activeClickableMenu = menu;
        }
    }

    /// <summary>
    /// Diagnose a Steam lobby by ID - query all available data.
    /// </summary>
    public SteamLobbyDiagnostics DiagnoseSteamLobby(ulong lobbyId)
    {
        var diag = new SteamLobbyDiagnostics { LobbyId = lobbyId };

        try
        {
            var steamLobby = new CSteamID(lobbyId);
            diag.IsValid = steamLobby.IsValid();
            diag.IsLobby = steamLobby.IsLobby();

            if (!diag.IsValid || !diag.IsLobby)
            {
                diag.Error = "Invalid lobby ID";
                return diag;
            }

            // Get lobby owner
            var owner = SteamMatchmaking.GetLobbyOwner(steamLobby);
            diag.LobbyOwner = owner.m_SteamID;
            diag.LobbyOwnerValid = owner.IsValid();

            // Get member count
            diag.MemberCount = SteamMatchmaking.GetNumLobbyMembers(steamLobby);

            // Get lobby data count and all keys
            int dataCount = SteamMatchmaking.GetLobbyDataCount(steamLobby);
            diag.DataCount = dataCount;
            diag.LobbyData = new Dictionary<string, string>();

            for (int i = 0; i < dataCount; i++)
            {
                if (SteamMatchmaking.GetLobbyDataByIndex(steamLobby, i, out string key, 256, out string value, 8192))
                {
                    diag.LobbyData[key] = value;
                }
            }

            // Try to get specific known keys
            diag.ProtocolVersion = SteamMatchmaking.GetLobbyData(steamLobby, "protocolVersion");

            // Try GetLobbyGameServer - this is the failing call
            bool hasGameServer = SteamMatchmaking.GetLobbyGameServer(steamLobby, out uint gameServerIP, out ushort gameServerPort, out CSteamID gameServerSteamID);
            diag.HasGameServer = hasGameServer;
            diag.GameServerIP = gameServerIP;
            diag.GameServerPort = gameServerPort;
            diag.GameServerSteamID = gameServerSteamID.m_SteamID;
            diag.GameServerSteamIDValid = gameServerSteamID.IsValid();

            _monitor.Log($"Steam Lobby Diagnostics for {lobbyId}:", LogLevel.Info);
            _monitor.Log($"  Valid: {diag.IsValid}, IsLobby: {diag.IsLobby}", LogLevel.Info);
            _monitor.Log($"  Owner: {diag.LobbyOwner} (valid: {diag.LobbyOwnerValid})", LogLevel.Info);
            _monitor.Log($"  Members: {diag.MemberCount}", LogLevel.Info);
            _monitor.Log($"  Data entries: {diag.DataCount}", LogLevel.Info);
            foreach (var kv in diag.LobbyData)
            {
                _monitor.Log($"    {kv.Key} = {kv.Value}", LogLevel.Info);
            }
            _monitor.Log($"  HasGameServer: {diag.HasGameServer}", LogLevel.Info);
            _monitor.Log($"  GameServer IP: {diag.GameServerIP}, Port: {diag.GameServerPort}", LogLevel.Info);
            _monitor.Log($"  GameServer SteamID: {diag.GameServerSteamID} (valid: {diag.GameServerSteamIDValid})", LogLevel.Info);
        }
        catch (Exception ex)
        {
            diag.Error = ex.Message;
            _monitor.Log($"Steam lobby diagnostics failed: {ex}", LogLevel.Error);
        }

        return diag;
    }

    /// <summary>
    /// Diagnose a Steam lobby by joining it first (required to access full data).
    /// </summary>
    public void DiagnoseSteamLobbyWithJoin(ulong lobbyId)
    {
        _monitor.Log($"Joining Steam lobby {lobbyId} for diagnostics...", LogLevel.Info);

        try
        {
            var steamLobby = new CSteamID(lobbyId);

            // Request lobby data first
            SteamMatchmaking.RequestLobbyData(steamLobby);

            // Join the lobby
            var joinCall = SteamMatchmaking.JoinLobby(steamLobby);

            // Set up callback
            var callResult = CallResult<LobbyEnter_t>.Create((result, failure) =>
            {
                if (failure)
                {
                    _monitor.Log("Failed to join lobby (IO failure)", LogLevel.Error);
                    return;
                }

                _monitor.Log($"Joined lobby, response: {result.m_EChatRoomEnterResponse}", LogLevel.Info);

                if (result.m_EChatRoomEnterResponse == 1) // Success
                {
                    // Now diagnose
                    DiagnoseSteamLobby(lobbyId);

                    // Leave the lobby
                    SteamMatchmaking.LeaveLobby(steamLobby);
                    _monitor.Log("Left lobby after diagnostics", LogLevel.Info);
                }
                else
                {
                    _monitor.Log($"Lobby join failed with response: {result.m_EChatRoomEnterResponse}", LogLevel.Error);
                }
            });

            callResult.Set(joinCall);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to join lobby for diagnostics: {ex}", LogLevel.Error);
        }
    }
}

#region DTOs

public class JoinResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public class FarmhandSelectionInfo
{
    public bool Success => InFarmhandMenu && string.IsNullOrEmpty(Error);
    public bool InFarmhandMenu { get; set; }
    public bool IsConnecting { get; set; }
    public string? Error { get; set; }
    public List<FarmhandSlotInfo> Slots { get; set; } = new();
}

public class FarmhandSlotInfo
{
    public int Index { get; set; }
    public bool IsCustomized { get; set; }
    public bool IsEmpty { get; set; }
    public string? Name { get; set; }
    public string? FarmName { get; set; }
}

public class SteamLobbyDiagnostics
{
    public ulong LobbyId { get; set; }
    public bool IsValid { get; set; }
    public bool IsLobby { get; set; }
    public ulong LobbyOwner { get; set; }
    public bool LobbyOwnerValid { get; set; }
    public int MemberCount { get; set; }
    public int DataCount { get; set; }
    public Dictionary<string, string> LobbyData { get; set; } = new();
    public string? ProtocolVersion { get; set; }
    public bool HasGameServer { get; set; }
    public uint GameServerIP { get; set; }
    public ushort GameServerPort { get; set; }
    public ulong GameServerSteamID { get; set; }
    public bool GameServerSteamIDValid { get; set; }
    public string? Error { get; set; }
}

#endregion
