using Microsoft.Xna.Framework;
using StardewModdingAPI;
using System.Collections.Generic;

namespace JunimoServer.Services.CabinManager
{
    public class CabinManagerData
    {
        public Vector2? DefaultCabinLocation = null;

        public HashSet<long> AllPlayerIdsEverJoined = new HashSet<long>();

        private const string _storageDataKey = "JunimoHost.CabinManager.data";

        private IModHelper Helper;
        private IMonitor Monitor;

        public CabinManagerData(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            Monitor = monitor;
        }

        public void Read()
        {
            Monitor.Log($"Reading {_storageDataKey}", LogLevel.Debug);
            CabinManagerData Data = Helper.Data.ReadSaveData<CabinManagerData>(_storageDataKey) ?? new CabinManagerData(Helper, Monitor);
            DefaultCabinLocation = Data.DefaultCabinLocation;
            AllPlayerIdsEverJoined = Data.AllPlayerIdsEverJoined;
        }

        public void Write()
        {
            Monitor.Log($"Writing {_storageDataKey}", LogLevel.Debug);
            Helper.Data.WriteSaveData(_storageDataKey, this);
        }
    }
}
