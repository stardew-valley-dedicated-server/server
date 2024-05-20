using System.Collections.Generic;
using System.Linq;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;

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

    public class RoleService
    {
        private RoleData _data = new RoleData();
        private const string RoleDataKey = "JunimoHost.Roles.data";


        private readonly IModHelper _helper;

        public RoleService(IModHelper helper)
        {
            _helper = helper;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            var saveData = _helper.Data.ReadSaveData<RoleData>(RoleDataKey);

            if (saveData != null)
            {
                _data = saveData;
                return;
            }

            _data.Roles[_helper.GetOwnerPlayerId()] = Role.Admin;
            SaveData();
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
            if (playerId == _helper.GetOwnerPlayerId()) return;

            _data.Roles[playerId] = Role.Unassigned;
            SaveData();
        }

        public bool IsPlayerAdmin(long playerId)
        {
            return _data.Roles.ContainsKey(playerId) && _data.Roles[playerId] == Role.Admin;
        }

        public bool IsPlayerOwner(long playerId)
        {
            return _helper.GetOwnerPlayerId() == playerId;
        }

        public long[] GetAdmins()
        {
            return _data.Roles.Keys.Where(IsPlayerAdmin).ToArray();
        }
    }
}