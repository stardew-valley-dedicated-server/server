using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
using StardewModdingAPI;
using System;


namespace JunimoBot
{
    public class ConnectService
    {
        private bool _hasSelectedFarmhand = false;

        private FarmhandMenu _farmhandMenu;
        private Client _client;

        private bool IsFarmhandsLoaded
        {
            get
            {
                return _farmhandMenu.MenuSlots.Count != 0;
            }
        }

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly ModConfig _config;
        private readonly Multiplayer _multiplayer;

        public ConnectService(IModHelper helper, IMonitor monitor, ModConfig config, Multiplayer multiplayer)
        {
            _helper = helper;
            _monitor = monitor;
            _config = config;
            _multiplayer = multiplayer;

            _helper.Events.GameLoop.ReturnedToTitle += ReturnedToTitle;
        }

        private void ReturnedToTitle(object sender, EventArgs e)
        {
            Reset();
        }

        public void TryConnect()
        {
            bool ShouldConnect = _client == null || (_client != null && !_client.connectionStarted && !_client.readyToPlay);
            bool IsConnecting = _client != null && _client.connectionStarted && !_client.readyToPlay;

            if (ShouldConnect)
            {
                Connect(_config.Host, _config.Port);
            }
            else if (IsConnecting)
            {
                if (!IsFarmhandsLoaded)
                {
                    _monitor.Log("Waiting until farmhands are loaded...", LogLevel.Info);
                }
                else if (IsFarmhandsLoaded && !_hasSelectedFarmhand)
                {
                    _monitor.Log($"Selecting farmhand", LogLevel.Info);
                    SelectFarmHand();
                }
                else if (IsFarmhandsLoaded && _hasSelectedFarmhand && SaveGame.IsProcessing)
                {
                    _monitor.Log($"Loading game...", LogLevel.Info);
                }
                else
                {
                    _monitor.Log("Successfully connected", LogLevel.Info);
                }
            }
        }

        private void Reset()
        {
            _farmhandMenu = null;
            _client = null;
            _hasSelectedFarmhand = false;
        }

        private void Connect(string serverIP, int serverPort)
        {
            _monitor.Log($"Connecting to {serverIP}:{serverPort}", LogLevel.Info);

            // Client does the actual connection, farmhand menu for automating the selection
            _client = _multiplayer.InitClient(new LidgrenClient($"{serverIP}:{serverPort}"));
            _farmhandMenu = new FarmhandMenu(_client);

            if (Game1.activeClickableMenu is TitleMenu)
            {
                TitleMenu.subMenu = _farmhandMenu;
            }
            else
            {
                Game1.activeClickableMenu = _farmhandMenu;
            }
        }

        private void SelectFarmHand(int farmhandIndex = 0)
        {
            if (_farmhandMenu.MenuSlots.Count <= farmhandIndex)
            {
                _monitor.Log("Unable to select farmhand, not yet automated so please create one manually for now.", LogLevel.Info);
                return;
            }

            _farmhandMenu.MenuSlots[farmhandIndex].Activate();
            _hasSelectedFarmhand = true;
        }
    }
}