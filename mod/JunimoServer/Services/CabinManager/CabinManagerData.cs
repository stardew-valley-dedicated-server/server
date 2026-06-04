using Microsoft.Xna.Framework;
using StardewModdingAPI;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace JunimoServer.Services.CabinManager
{
    public class CabinManagerData
    {
        public Vector2? DefaultCabinLocation = null;

        public HashSet<long> AllPlayerIdsEverJoined = new HashSet<long>();

        /// <summary>
        /// Positions of cabins a player has explicitly moved via the /cabin command,
        /// keyed by the owner's UniqueMultiplayerID. This records that the placement
        /// was intentional so the MoveToStack / strategy-switch bulk movers don't
        /// sweep it back into the hidden stack on the next load. The position itself
        /// persists via the building's own tileX/tileY; this map only records intent.
        /// </summary>
        public ConcurrentDictionary<long, Vector2> PlayerCabinPositions = new ConcurrentDictionary<long, Vector2>();

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
            Monitor.Log($"Reading saved data '{_storageDataKey}'", LogLevel.Trace);
            CabinManagerData Data = Helper.Data.ReadSaveData<CabinManagerData>(_storageDataKey) ?? new CabinManagerData(Helper, Monitor);
            DefaultCabinLocation = Data.DefaultCabinLocation;
            AllPlayerIdsEverJoined = Data.AllPlayerIdsEverJoined;
            PlayerCabinPositions = Data.PlayerCabinPositions ?? new ConcurrentDictionary<long, Vector2>();
        }

        public void Write()
        {
            Monitor.Log($"Writing saved data '{_storageDataKey}'", LogLevel.Trace);
            Helper.Data.WriteSaveData(_storageDataKey, this);
        }
    }
}
