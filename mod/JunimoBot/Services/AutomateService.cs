using StardewValley;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace JunimoBot
{
    public class AutomateService
    {
        /// <summary>
        /// Whether the main player is currently being automated.
        /// </summary>
        public static bool IsAutomating
        {
            get
            {
                return _isAutomating;
            }
        }

        private static bool _isAutomating;

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly ModConfig _config;

        public AutomateService(IModHelper helper, IMonitor monitor, ModConfig config)
        {
            _helper = helper;
            _monitor = monitor;
            _config = config;

            helper.Events.Display.Rendered += OnRendered;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
        }

        private void OnRendered(object sender, RenderedEventArgs e)
        {
            if (!IsAutomating)
            {
                return;
            }

            // Draw status information in the top left corner
            // TODO: Create a panel for these
            Util.DrawTextBox(5, 100, Game1.dialogueFont, I18n.AutoModeLabelOn());
            Util.DrawTextBox(5, 180, Game1.dialogueFont, I18n.AutoModeLabelToggleHotkey(button: _config.AutomateToggleHotKey));
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (e.Button == _config.AutomateToggleHotKey)
            {
                ToggleAutoMode();
            }
        }

        private void ToggleAutoMode()
        {
            _isAutomating = !IsAutomating;
            Game1.displayHUD = IsAutomating;

            string toggleMessage = IsAutomating ? I18n.AutoModeLabelOn() : I18n.AutoModeLabelOff();
            Game1.addHUDMessage(new HUDMessage(toggleMessage));
            _monitor.Log(toggleMessage, LogLevel.Info);
        }
    }
}