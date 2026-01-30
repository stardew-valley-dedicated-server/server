using System.Reflection;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Controls character customization menu interactions.
/// Uses reflection to access private fields in CharacterCustomization.
/// </summary>
public class CharacterController
{
    private readonly IMonitor _monitor;

    // Cached reflection info for private fields
    private static readonly FieldInfo? NameBoxField = typeof(CharacterCustomization)
        .GetField("nameBox", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? FavThingBoxField = typeof(CharacterCustomization)
        .GetField("favThingBox", BindingFlags.NonPublic | BindingFlags.Instance);

    public CharacterController(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Get the current CharacterCustomization menu if active.
    /// </summary>
    private CharacterCustomization? GetCharacterMenu()
    {
        // Check if it's a submenu of TitleMenu
        if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is CharacterCustomization charSub)
            return charSub;

        // Check if it's the active menu directly
        if (Game1.activeClickableMenu is CharacterCustomization charDirect)
            return charDirect;

        return null;
    }

    /// <summary>
    /// Get the nameBox TextBox from the menu using reflection.
    /// </summary>
    private TextBox? GetNameBox(CharacterCustomization menu)
    {
        return NameBoxField?.GetValue(menu) as TextBox;
    }

    /// <summary>
    /// Get the favThingBox TextBox from the menu using reflection.
    /// </summary>
    private TextBox? GetFavThingBox(CharacterCustomization menu)
    {
        return FavThingBoxField?.GetValue(menu) as TextBox;
    }

    /// <summary>
    /// Get information about the current character customization state.
    /// </summary>
    public CharacterInfo GetCharacterInfo()
    {
        var menu = GetCharacterMenu();
        if (menu == null)
        {
            return new CharacterInfo
            {
                InCharacterMenu = false,
                Error = "Not in character customization menu"
            };
        }

        var nameBox = GetNameBox(menu);
        var favThingBox = GetFavThingBox(menu);

        return new CharacterInfo
        {
            InCharacterMenu = true,
            Name = nameBox?.Text ?? "",
            FavoriteThing = favThingBox?.Text ?? "",
            CanConfirm = menu.canLeaveMenu()
        };
    }

    /// <summary>
    /// Set the character's name and favorite thing.
    /// </summary>
    public CustomizeResult SetCharacterData(string name, string favoriteThing)
    {
        try
        {
            var menu = GetCharacterMenu();
            if (menu == null)
            {
                return new CustomizeResult { Success = false, Error = "Not in character customization menu" };
            }

            var nameBox = GetNameBox(menu);
            var favThingBox = GetFavThingBox(menu);

            if (nameBox == null || favThingBox == null)
            {
                return new CustomizeResult { Success = false, Error = "Text boxes not available (reflection failed)" };
            }

            // Bypass the TextBox pixel-width truncation — it's a client-side UI constraint only.
            // The server stores names as NetString with no length cap.
            // TODO: enforce a max name length server-side to prevent abuse
            nameBox.limitWidth = false;
            nameBox.Text = name;
            nameBox.limitWidth = true;

            favThingBox.limitWidth = false;
            favThingBox.Text = favoriteThing;
            favThingBox.limitWidth = true;

            _monitor.Log($"Set character data - Name: {name}, FavoriteThing: {favoriteThing}", LogLevel.Trace);

            return new CustomizeResult { Success = true };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to set character data: {ex.Message}", LogLevel.Error);
            return new CustomizeResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Confirm the character creation by clicking the OK button.
    /// </summary>
    public CustomizeResult ConfirmCharacter()
    {
        try
        {
            var menu = GetCharacterMenu();
            if (menu == null)
            {
                return new CustomizeResult { Success = false, Error = "Not in character customization menu" };
            }

            if (!menu.canLeaveMenu())
            {
                return new CustomizeResult
                {
                    Success = false,
                    Error = "Cannot confirm - name or favorite thing may be empty"
                };
            }

            if (menu.okButton == null)
            {
                return new CustomizeResult { Success = false, Error = "OK button not available" };
            }

            // Simulate clicking the OK button
            menu.receiveLeftClick(menu.okButton.bounds.X + 1, menu.okButton.bounds.Y + 1);

            _monitor.Log("Confirmed character creation", LogLevel.Trace);

            return new CustomizeResult { Success = true, Message = "Character confirmed" };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to confirm character: {ex.Message}", LogLevel.Error);
            return new CustomizeResult { Success = false, Error = ex.Message };
        }
    }
}

#region DTOs

public class CharacterInfo
{
    public bool InCharacterMenu { get; set; }
    public string? Name { get; set; }
    public string? FavoriteThing { get; set; }
    public bool CanConfirm { get; set; }
    public string? Error { get; set; }
}

public class CustomizeResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public class CustomizeCharacterRequest
{
    public string Name { get; set; } = "";
    public string FavoriteThing { get; set; } = "";
}

#endregion
