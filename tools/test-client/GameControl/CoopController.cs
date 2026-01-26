using System.Reflection;
using JunimoTestClient.Util;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;

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

            _monitor.Log("Opened invite code input menu", LogLevel.Debug);

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

            _monitor.Log($"Submitting invite code: {inviteCode}", LogLevel.Debug);

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
    /// <remarks>
    /// Deprecated: Use OpenInviteCodeMenu → wait for TitleTextInputMenu → SubmitInviteCode instead.
    /// This single-frame approach is unreliable because the text input menu may not appear until the next frame.
    /// </remarks>
    [Obsolete("Use OpenInviteCodeMenu + SubmitInviteCode instead — this single-frame approach is unreliable.")]
    public JoinResult EnterInviteCode(string inviteCode)
    {
        // First open the invite code menu
        var openResult = OpenInviteCodeMenu();
        if (!openResult.Success)
        {
            return openResult;
        }

        // The menu should now be TitleTextInputMenu - submit the code
        // Note: In practice, there might be a frame delay. The test should use the two-step approach.
        var inputMenu = GetTextInputMenu();
        if (inputMenu == null)
        {
            // Menu hasn't appeared yet - this is expected in single-frame execution
            // Return success, caller should wait for menu and call SubmitInviteCode
            return new JoinResult
            {
                Success = true,
                Message = "Invite code menu opening, call SubmitInviteCode after menu appears"
            };
        }

        return SubmitInviteCode(inviteCode);
    }

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

            _monitor.Log($"Connecting to LAN address: {address}", LogLevel.Debug);

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
                            _monitor.Log($"Error reading farmer slot {i}: {ex.Message}", LogLevel.Debug);
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

            _monitor.Log($"Selected farmhand slot {slotIndex}", LogLevel.Debug);

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

#endregion
