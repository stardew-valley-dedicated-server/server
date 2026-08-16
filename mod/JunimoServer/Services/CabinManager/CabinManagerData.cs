using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;

namespace JunimoServer.Services.CabinManager;

/// <summary>
/// Persisted record of an in-progress staged strategy migration (cabins migrate). The old
/// strategy stays live for the whole staging window; this record is what survives restarts
/// so the admin can resume or abort. Save-scoped by design (rides CabinManagerData's
/// ReadSaveData), so /newgame naturally starts clean.
/// </summary>
public class CabinMigrationState
{
    public CabinStrategy FromStrategy { get; set; }

    public CabinStrategy ToStrategy { get; set; }

    /// <summary>
    /// Interior NameOrUniqueName of each cabin placed during staging (unique and save-stable;
    /// owner uid won't do — spare cabins are ownerless). The bulk movers exempt these so a
    /// staged cabin survives an interim reload; abort moves them back to the hidden stack.
    /// </summary>
    public List<string> PlacedCabinIndoorNames { get; set; } = new List<string>();

    /// <summary>
    /// FarmhouseStack → CabinStack only: the admin-chosen shared stack spot. Held in the
    /// record during staging and applied to <see cref="CabinManagerData.DefaultCabinLocation"/>
    /// only at commit, so abort needs no rollback.
    /// </summary>
    public Vector2? StackSpotOverride { get; set; }
}

public class CabinManagerData
{
    public Vector2? DefaultCabinLocation = null;

    public HashSet<long> AllPlayerIdsEverJoined = new HashSet<long>();

    /// <summary>Active staged strategy migration, or null. See <see cref="CabinMigrationState"/>.</summary>
    public CabinMigrationState ActiveMigration = null;

    /// <summary>
    /// Positions of cabins a player has explicitly moved via the /cabin command,
    /// keyed by the owner's UniqueMultiplayerID. This records that the placement
    /// was intentional so the MoveToStack / strategy-switch bulk movers don't
    /// sweep it back into the hidden stack on the next load. The position itself
    /// persists via the building's own tileX/tileY; this map only records intent.
    /// </summary>
    public ConcurrentDictionary<long, Vector2> PlayerCabinPositions =
        new ConcurrentDictionary<long, Vector2>();

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
        CabinManagerData Data =
            Helper.Data.ReadSaveData<CabinManagerData>(_storageDataKey)
            ?? new CabinManagerData(Helper, Monitor);
        DefaultCabinLocation = Data.DefaultCabinLocation;
        AllPlayerIdsEverJoined = Data.AllPlayerIdsEverJoined;
        ActiveMigration = Data.ActiveMigration;
        PlayerCabinPositions =
            Data.PlayerCabinPositions ?? new ConcurrentDictionary<long, Vector2>();
    }

    public void Write()
    {
        Monitor.Log($"Writing saved data '{_storageDataKey}'", LogLevel.Trace);
        Helper.Data.WriteSaveData(_storageDataKey, this);
    }
}
