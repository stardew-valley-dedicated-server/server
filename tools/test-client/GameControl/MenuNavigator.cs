using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Programmatically navigates between game menus.
/// </summary>
public class MenuNavigator
{
    private readonly IMonitor _monitor;

    public MenuNavigator(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Navigate to a specific menu by name.
    /// </summary>
    public NavigationResult NavigateTo(string target)
    {
        return target.ToLowerInvariant() switch
        {
            "title" or "titlemenu" => NavigateToTitle(),
            "coop" or "coopmenu" => NavigateToCoop(),
            "exit" => ExitToTitle(),
            _ => new NavigationResult { Success = false, Error = $"Unknown target: {target}" }
        };
    }

    /// <summary>
    /// Navigate to the title menu.
    /// </summary>
    public NavigationResult NavigateToTitle()
    {
        try
        {
            // If already at title menu with no submenu, we're done
            if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu == null)
            {
                return new NavigationResult
                {
                    Success = true,
                    Message = "Already at title menu"
                };
            }

            // If we're in a game, exit to title
            if (Context.IsWorldReady)
            {
                return ExitToTitle();
            }

            // If we're at title with a submenu, close it
            if (Game1.activeClickableMenu is TitleMenu)
            {
                TitleMenu.subMenu = null;
                Game1.changeMusicTrack("MainTheme");
                return new NavigationResult
                {
                    Success = true,
                    Message = "Closed submenu, now at title"
                };
            }

            // Otherwise, create a new title menu
            Game1.activeClickableMenu = new TitleMenu();
            return new NavigationResult
            {
                Success = true,
                Message = "Navigated to title menu"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to navigate to title: {ex.Message}", LogLevel.Error);
            return new NavigationResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Navigate to the Co-op menu.
    /// </summary>
    public NavigationResult NavigateToCoop()
    {
        try
        {
            // Make sure we're at the title menu first
            if (Game1.activeClickableMenu is not TitleMenu)
            {
                var titleResult = NavigateToTitle();
                if (!titleResult.Success)
                    return titleResult;

                if (Game1.activeClickableMenu is not TitleMenu)
                {
                    return new NavigationResult { Success = false, Error = "Failed to get title menu" };
                }
            }

            // Check if already at coop menu
            if (TitleMenu.subMenu is CoopMenu)
            {
                return new NavigationResult
                {
                    Success = true,
                    Message = "Already at coop menu"
                };
            }

            // Open the coop menu
            TitleMenu.subMenu = new CoopMenu(tooManyFarms: false);

            return new NavigationResult
            {
                Success = true,
                Message = "Navigated to coop menu"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to navigate to coop: {ex.Message}", LogLevel.Error);
            return new NavigationResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Exit current game to title screen.
    /// </summary>
    public NavigationResult ExitToTitle()
    {
        try
        {
            if (!Context.IsWorldReady && Game1.activeClickableMenu is TitleMenu)
            {
                return new NavigationResult
                {
                    Success = true,
                    Message = "Already at title menu"
                };
            }

            Game1.ExitToTitle();

            return new NavigationResult
            {
                Success = true,
                Message = "Exiting to title"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to exit to title: {ex.Message}", LogLevel.Error);
            return new NavigationResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Switch tab in CoopMenu (0 = Join, 1 = Host).
    /// Note: Tab enum is JOIN_TAB=0, HOST_TAB=1
    /// </summary>
    public NavigationResult SwitchCoopTab(int tabIndex)
    {
        try
        {
            var coopMenu = TitleMenu.subMenu as CoopMenu ?? Game1.activeClickableMenu as CoopMenu;

            if (coopMenu == null)
            {
                return new NavigationResult { Success = false, Error = "Not in coop menu" };
            }

            // Convert int to Tab enum (currentTab is public)
            var tab = (CoopMenu.Tab)tabIndex;
            coopMenu.currentTab = tab;

            _monitor.Log($"Switched to tab {tab}", LogLevel.Debug);

            return new NavigationResult
            {
                Success = true,
                Message = $"Switched to tab {tab}"
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to switch coop tab: {ex.Message}", LogLevel.Error);
            return new NavigationResult { Success = false, Error = ex.Message };
        }
    }
}

public class NavigationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}
