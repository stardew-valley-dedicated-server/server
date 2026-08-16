using JunimoServer.Services.CabinManager;

namespace JunimoServer.Services.PersistentOption;

public class PersistentOptionsSaveData
{
    public int MaxPlayers { get; set; } = 6;

    public CabinStrategy CabinStrategy { get; set; } = CabinStrategy.CabinStack;

    public ExistingCabinBehavior ExistingCabinBehavior { get; set; } =
        ExistingCabinBehavior.KeepExisting;

    public bool AllowCabinRelocation { get; set; } = true;

    /// <summary>
    /// Frozen cabin count for the None strategy: min(designated map positions, MaxPlayers)
    /// at game creation, or the final placed count when a staged migration commits to None.
    /// Later MaxPlayers changes do NOT grow it — on-demand placement onto a developed farm
    /// would bulldoze player content. 0 means the save predates this field; consumers fall
    /// back to computing the cap live.
    /// </summary>
    public int NoneCabinCount { get; set; } = 0;

    /// <summary>
    /// Save name whose FIRST load must reset <see cref="NoneCabinCount"/> to 0, set when a
    /// save import stages that save as the next boot target. The clear is deferred to the
    /// load (not done at import staging) so the still-running previous world keeps its own
    /// frozen cap until the restart — and keeps it for good if the import never boots
    /// (canceled/retargeted): a load of any OTHER save just drops the stale marker.
    /// </summary>
    public string PendingNoneCapClearSaveName { get; set; } = null;
}
