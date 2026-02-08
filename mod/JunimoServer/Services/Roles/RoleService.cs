using JunimoServer.Services.Settings;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace JunimoServer.Services.Roles
{
    public enum Role
    {
        Admin,
        Unassigned,
    }

    public class RoleData
    {
        public Dictionary<long, Role> Roles = new Dictionary<long, Role>();
    }

    public class RoleService : ModService
    {
        private RoleData _data = new RoleData();
        private const string RoleDataKey = "JunimoHost.Roles.data";

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly ServerSettingsLoader _settings;

        public RoleService(IModHelper helper, IMonitor monitor, ServerSettingsLoader settings)
        {
            _helper = helper;
            _monitor = monitor;
            _settings = settings;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            var saveData = _helper.Data.ReadSaveData<RoleData>(RoleDataKey);

            if (saveData != null)
            {
                _data = saveData;
            }
            // No default admin assignment - admins are configured via ADMIN_STEAM_IDS
        }

        private void SaveData()
        {
            _helper.Data.WriteSaveData(RoleDataKey, _data);
        }

        public void AssignAdmin(long playerId)
        {
            _data.Roles[playerId] = Role.Admin;
            SaveData();
        }

        public void UnassignAdmin(long playerId)
        {
            // Can't unassign the server host (though this should never happen in practice)
            if (playerId == _helper.GetServerHostId())
            {
                return;
            }

            _data.Roles[playerId] = Role.Unassigned;
            SaveData();
        }

        public bool IsPlayerAdmin(long playerId)
        {
            return _data.Roles.ContainsKey(playerId) && _data.Roles[playerId] == Role.Admin;
        }

        /// <summary>
        /// Checks if the given player ID is the server host (the dedicated server itself).
        /// In a dedicated server, no human player should ever be the server host.
        /// </summary>
        public bool IsServerHost(long playerId)
        {
            return _helper.GetServerHostId() == playerId;
        }

        /// <summary>
        /// Checks if the given farmer is the server host (the dedicated server itself).
        /// In a dedicated server, no human player should ever be the server host.
        /// </summary>
        public bool IsServerHost(Farmer farmer)
        {
            return IsServerHost(farmer.UniqueMultiplayerID);
        }

        public long[] GetAdmins()
        {
            return _data.Roles.Keys.Where(IsPlayerAdmin).ToArray();
        }

        /// <summary>
        /// Called when a player connects. Checks if their Steam ID is in the admin list.
        /// </summary>
        private void OnPeerConnected(object sender, PeerConnectedEventArgs e)
        {
            TryAutoPromoteAdmin(e.Peer.PlayerID);
        }

        /// <summary>
        /// Checks if a player's Steam ID is in the configured admin list and promotes them if so.
        /// </summary>
        public void TryAutoPromoteAdmin(long playerId)
        {
            var adminSteamIds = _settings.AdminSteamIds;
            if (adminSteamIds == null || adminSteamIds.Length == 0)
                return;

            // Already an admin, skip
            if (IsPlayerAdmin(playerId))
                return;

            // Get the player's Steam ID via the game server
            var steamId = GetPlayerSteamId(playerId);
            if (string.IsNullOrEmpty(steamId))
            {
                _monitor.Log($"[Roles] Cannot get Steam ID for player {playerId}", LogLevel.Trace);
                return;
            }

            // Check if this Steam ID is in the admin list
            if (Array.Exists(adminSteamIds, id => id == steamId))
            {
                AssignAdmin(playerId);
                _monitor.Log($"[Roles] Auto-promoted player {playerId} (Steam ID: {steamId}) to admin", LogLevel.Info);
            }
        }

        /// <summary>
        /// Gets the Steam/platform ID for a connected player.
        /// </summary>
        private static string GetPlayerSteamId(long playerId)
        {
            if (Game1.server is not GameServer gameServer)
                return null;

            // Access internal 'servers' field via reflection
            var serversField = typeof(GameServer).GetField("servers", BindingFlags.NonPublic | BindingFlags.Instance);
            if (serversField?.GetValue(gameServer) is not List<Server> servers)
                return null;

            foreach (var server in servers)
            {
                var userId = server.getUserId(playerId);
                if (!string.IsNullOrEmpty(userId))
                    return userId;
            }

            return null;
        }
    }
}
