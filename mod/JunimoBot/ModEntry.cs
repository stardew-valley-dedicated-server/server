using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using Microsoft.Xna.Framework;
using StardewValley.Pathfinding;
using System.Linq;
using StardewModdingAPI.Events;

namespace JunimoBot
{
    public class ModEntry : Mod
    {
        private bool _hasSentJoinedMessage = false;


        private Multiplayer _multiplayer;

        private AutomateService _automateService;
        private ConfigService _configService;
        private ConnectService _connectService;
        private PathfindService _pathfindService;

        /// <summary>
        /// The mod configuration from the player.
        /// </summary>
        private ModConfig _config;


        public override void Entry(IModHelper helper)
        {
            Monitor.Log("JunimoBot loaded", LogLevel.Info);

            // Enable strongly typed translations
            I18n.Init(helper.Translation);

            // Load config
            _config = helper.ReadConfig<ModConfig>();
            _multiplayer ??= helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();

            // Load services
            _configService = new ConfigService(helper, Monitor, _config, ModManifest);
            _automateService = new AutomateService(helper, Monitor, _config);
            _connectService = new ConnectService(helper, Monitor, _config, _multiplayer);
            _pathfindService = new PathfindService(helper, Monitor);
            
            helper.Events.GameLoop.UpdateTicked += OnOneSecondUpdateTicked;
        }


        private void OnOneSecondUpdateTicked(object sender, EventArgs e)
        {
            if (!AutomateService.IsAutomating)
            {
                return;
            }

            _connectService.TryConnect();

            if (!Context.IsWorldReady)
            {
                return;
            }

            HandleDebugLog();
            HandleJoinedMessage();
            HandleDialogueBox();
        }

        private void HandleJoinedMessage()
        {
            if (!_hasSentJoinedMessage)
            {
                _hasSentJoinedMessage = true;
                SendChatMessage("Hello frens!");
            }
        }

        private void HandleDialogueBox()
        {
            if (Game1.activeClickableMenu == null || Game1.activeClickableMenu is not DialogueBox)
            {
                return;
            }

            Monitor.Log("HandleDialogueBox", LogLevel.Info);
            Game1.currentLocation.answerDialogue((Game1.activeClickableMenu as DialogueBox).responses.First());
        }

        private void HandleDebugLog()
        {
            Monitor.Log(Util.ToJson(new Dictionary<string, dynamic>()
            {
                ["currentLocation"] = Game1.currentLocation?.Name ?? "unknown",
                ["currentTilePoint"] = Game1.player.TilePoint,
                ["GetBedSpot"] = Util.GetBedSpot(Game1.player),
            }), LogLevel.Info);
        }

        private void SendChatMessage(string message)
        {
            _multiplayer.sendChatMessage(LocalizedContentManager.CurrentLanguageCode, message, Multiplayer.AllPlayers);
        }
    }
}