using System.Reflection;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Detects and reports current menu state.
/// </summary>
public static class MenuDetector
{
    /// <summary>
    /// Get detailed information about the current menu state.
    /// </summary>
    public static MenuInfo GetCurrentMenu()
    {
        var activeMenu = Game1.activeClickableMenu;

        if (activeMenu == null)
        {
            return new MenuInfo
            {
                Type = Game1.gameMode switch
                {
                    Game1.playingGameMode => "InGame",
                    Game1.loadingMode => "Loading",
                    _ => "None"
                },
                IsInGame = Context.IsWorldReady,
                GameMode = Game1.gameMode
            };
        }

        var info = new MenuInfo
        {
            Type = GetMenuTypeName(activeMenu),
            FullType = activeMenu.GetType().FullName,
            IsInGame = Context.IsWorldReady,
            GameMode = Game1.gameMode
        };

        // Handle TitleMenu with submenus
        if (activeMenu is TitleMenu titleMenu)
        {
            info.SubMenu = GetSubMenuInfo(titleMenu);
            info.TitleMenuState = GetTitleMenuState(titleMenu);
        }

        // Handle CoopMenu specifically
        if (activeMenu is CoopMenu coopMenu)
        {
            info.CoopMenuInfo = GetCoopMenuInfo(coopMenu);
        }

        return info;
    }

    private static string GetMenuTypeName(IClickableMenu menu)
    {
        return menu.GetType().Name;
    }

    private static SubMenuInfo? GetSubMenuInfo(TitleMenu titleMenu)
    {
        var subMenu = TitleMenu.subMenu;
        if (subMenu == null)
            return null;

        var info = new SubMenuInfo
        {
            Type = GetMenuTypeName(subMenu),
            FullType = subMenu.GetType().FullName
        };

        // If submenu is CoopMenu, get its details
        if (subMenu is CoopMenu coopMenu)
        {
            info.CoopMenuInfo = GetCoopMenuInfo(coopMenu);
        }

        return info;
    }

    private static string GetTitleMenuState(TitleMenu titleMenu)
    {
        // TitleMenu has different visual states
        if (TitleMenu.subMenu != null)
            return "SubMenuOpen";

        return "MainButtons";
    }

    private static CoopMenuInfo GetCoopMenuInfo(CoopMenu coopMenu)
    {
        var info = new CoopMenuInfo();

        try
        {
            // Use reflection to get internal state
            var currentTabField = typeof(CoopMenu).GetField("currentTab",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (currentTabField != null)
            {
                info.CurrentTab = (int)(currentTabField.GetValue(coopMenu) ?? 0);
                info.TabName = info.CurrentTab switch
                {
                    0 => "HostTab",
                    1 => "JoinTab",
                    _ => "Unknown"
                };
            }
        }
        catch
        {
            info.TabName = "Unknown";
        }

        return info;
    }

    /// <summary>
    /// Get connection/multiplayer status.
    /// </summary>
    public static ConnectionStatus GetConnectionStatus()
    {
        return new ConnectionStatus
        {
            IsMultiplayer = Game1.IsMultiplayer,
            IsClient = Game1.IsClient,
            IsServer = Game1.IsServer,
            IsLocalMultiplayer = Context.IsSplitScreen,
            IsConnected = Game1.IsMultiplayer && Context.IsWorldReady,
            WorldReady = Context.IsWorldReady,
            HasLoadedGame = Context.IsWorldReady,
            NumberOfPlayers = Game1.numberOfPlayers()
        };
    }

    /// <summary>
    /// Get current farmer info (if in game).
    /// </summary>
    public static FarmerInfo? GetFarmerInfo()
    {
        if (!Context.IsWorldReady || Game1.player == null)
            return null;

        return new FarmerInfo
        {
            Name = Game1.player.Name,
            FarmName = Game1.player.farmName.Value,
            Money = Game1.player.Money,
            IsMainPlayer = Game1.player.IsMainPlayer,
            UniqueId = Game1.player.UniqueMultiplayerID.ToString(),
            CurrentLocation = Game1.player.currentLocation?.Name
        };
    }

    /// <summary>
    /// Get all clickable components/buttons in the current menu.
    /// </summary>
    public static MenuButtonsInfo GetMenuButtons()
    {
        var info = new MenuButtonsInfo();
        var menu = GetActiveMenu();

        if (menu == null)
        {
            info.Error = "No active menu";
            return info;
        }

        info.MenuType = menu.GetType().Name;

        // Get allClickableComponents if available
        try
        {
            var componentsField = typeof(IClickableMenu).GetField("allClickableComponents",
                BindingFlags.Public | BindingFlags.Instance);
            if (componentsField?.GetValue(menu) is List<ClickableComponent> components)
            {
                foreach (var comp in components)
                {
                    info.Buttons.Add(new ButtonInfo
                    {
                        Name = comp.name,
                        Label = comp.label,
                        Id = comp.myID,
                        X = comp.bounds.X,
                        Y = comp.bounds.Y,
                        Width = comp.bounds.Width,
                        Height = comp.bounds.Height,
                        Visible = comp.visible
                    });
                }
            }
        }
        catch { }

        // Also try to get specific button fields common in menus
        TryAddButton(info, menu, "okButton");
        TryAddButton(info, menu, "cancelButton");
        TryAddButton(info, menu, "backButton");
        TryAddButton(info, menu, "upperRightCloseButton");
        TryAddButton(info, menu, "refreshButton");
        TryAddButton(info, menu, "joinTab");
        TryAddButton(info, menu, "hostTab");

        return info;
    }

    private static void TryAddButton(MenuButtonsInfo info, IClickableMenu menu, string fieldName)
    {
        try
        {
            var field = menu.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(menu) is ClickableComponent comp && !info.Buttons.Any(b => b.Name == fieldName))
            {
                info.Buttons.Add(new ButtonInfo
                {
                    Name = fieldName,
                    Label = comp.label ?? fieldName,
                    Id = comp.myID,
                    X = comp.bounds.X,
                    Y = comp.bounds.Y,
                    Width = comp.bounds.Width,
                    Height = comp.bounds.Height,
                    Visible = comp.visible
                });
            }
        }
        catch { }
    }

    /// <summary>
    /// Get menu slots (for LoadGameMenu, CoopMenu, FarmhandMenu).
    /// </summary>
    public static MenuSlotsInfo GetMenuSlots()
    {
        var info = new MenuSlotsInfo();
        var menu = GetActiveMenu();

        if (menu == null)
        {
            info.Error = "No active menu";
            return info;
        }

        info.MenuType = menu.GetType().Name;

        // Try to get menuSlots from LoadGameMenu-derived menus
        if (menu is LoadGameMenu loadMenu)
        {
            try
            {
                var slotsField = typeof(LoadGameMenu).GetField("menuSlots",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (slotsField?.GetValue(loadMenu) is List<LoadGameMenu.MenuSlot> slots)
                {
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var slot = slots[i];
                        var slotInfo = new SlotInfo
                        {
                            Index = i,
                            Type = slot.GetType().Name
                        };

                        // Try to get Farmer from slot
                        var farmerField = slot.GetType().GetField("Farmer",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (farmerField?.GetValue(slot) is Farmer farmer)
                        {
                            slotInfo.FarmerName = farmer.Name;
                            slotInfo.FarmName = farmer.farmName.Value;
                            slotInfo.IsCustomized = farmer.isCustomized.Value;
                        }

                        info.Slots.Add(slotInfo);
                    }
                }
            }
            catch { }
        }

        return info;
    }

    /// <summary>
    /// Get the currently active menu (handles TitleMenu submenus).
    /// </summary>
    private static IClickableMenu? GetActiveMenu()
    {
        var menu = Game1.activeClickableMenu;

        // If it's a TitleMenu with a submenu, return the submenu
        if (menu is TitleMenu && TitleMenu.subMenu != null)
        {
            return TitleMenu.subMenu;
        }

        return menu;
    }
}

#region DTOs

public class MenuInfo
{
    public string Type { get; set; } = "";
    public string? FullType { get; set; }
    public SubMenuInfo? SubMenu { get; set; }
    public string? TitleMenuState { get; set; }
    public CoopMenuInfo? CoopMenuInfo { get; set; }
    public bool IsInGame { get; set; }
    public byte GameMode { get; set; }
}

public class SubMenuInfo
{
    public string Type { get; set; } = "";
    public string? FullType { get; set; }
    public CoopMenuInfo? CoopMenuInfo { get; set; }
}

public class CoopMenuInfo
{
    public int CurrentTab { get; set; }
    public string TabName { get; set; } = "";
}

public class ConnectionStatus
{
    public bool IsMultiplayer { get; set; }
    public bool IsClient { get; set; }
    public bool IsServer { get; set; }
    public bool IsLocalMultiplayer { get; set; }
    public bool IsConnected { get; set; }
    public bool WorldReady { get; set; }
    public bool HasLoadedGame { get; set; }
    public int NumberOfPlayers { get; set; }
}

public class FarmerInfo
{
    public string Name { get; set; } = "";
    public string FarmName { get; set; } = "";
    public int Money { get; set; }
    public bool IsMainPlayer { get; set; }
    public string UniqueId { get; set; } = "";
    public string? CurrentLocation { get; set; }
}

public class MenuButtonsInfo
{
    public string? MenuType { get; set; }
    public string? Error { get; set; }
    public List<ButtonInfo> Buttons { get; set; } = new();
}

public class ButtonInfo
{
    public string? Name { get; set; }
    public string? Label { get; set; }
    public int Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Visible { get; set; }
}

public class MenuSlotsInfo
{
    public string? MenuType { get; set; }
    public string? Error { get; set; }
    public List<SlotInfo> Slots { get; set; } = new();
}

public class SlotInfo
{
    public int Index { get; set; }
    public string? Type { get; set; }
    public string? FarmerName { get; set; }
    public string? FarmName { get; set; }
    public bool IsCustomized { get; set; }
}

#endregion
