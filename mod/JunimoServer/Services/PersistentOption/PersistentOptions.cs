using System.Xml.Serialization;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.Settings;
using StardewModdingAPI;

namespace JunimoServer.Services.PersistentOption;

public class PersistentOptions
{
    private const string SaveKey = "JunimoHost.PersistentOptions";

    private readonly IModHelper _helper;
    public PersistentOptionsSaveData Data { get; private set; }

    /// <summary>
    /// The CabinStrategy from the previous run (before settings sync).
    /// Used by CabinManagerService to detect strategy changes and migrate cabins.
    /// </summary>
    [XmlIgnore]
    public CabinStrategy PreviousCabinStrategy { get; private set; }

    public PersistentOptions(IModHelper helper, ServerSettingsLoader settings)
    {
        _helper = helper;
        Data =
            helper.Data.ReadGlobalData<PersistentOptionsSaveData>(SaveKey)
            ?? new PersistentOptionsSaveData();
        RecaptureAndSync(settings);
    }

    /// <summary>
    /// Captures the currently-persisted strategy as PreviousCabinStrategy, then
    /// overwrites Data from the current settings file. Called on construction and
    /// again on a runtime /reload so a CabinStrategy change is detected by
    /// CabinManagerService.DetectAndApplyStrategySwitch without a process restart.
    /// </summary>
    public void RecaptureAndSync(ServerSettingsLoader settings)
    {
        // Persisted value = the strategy the cabins are currently arranged for.
        PreviousCabinStrategy = Data.CabinStrategy;

        // Sync runtime settings from the settings file so services see current values.
        SyncFromSettings(settings);
    }

    public void SetPersistentOptions(PersistentOptionsSaveData optionsSaveData)
    {
        _helper.Data.WriteGlobalData(SaveKey, optionsSaveData);
        Data = optionsSaveData;

        // Game creation (the only caller) writes the options a world is being created FOR,
        // so there is nothing to migrate at its first SaveLoaded — align the strategy-change
        // detector. Without this, a stale PreviousCabinStrategy from the prior save trips
        // DetectAndApplyStrategySwitch on the fresh game: SaveLoaded fires only after
        // CreateNewGame finished (EnsureAtLeastXCabins included), so a fresh stacked game
        // already parks a hidden cabin, which reads as a materializing switch — e.g.
        // FarmhouseStack → CabinStack gets rejected and the fresh game's strategy silently
        // reverts.
        PreviousCabinStrategy = optionsSaveData.CabinStrategy;
    }

    public void Save()
    {
        _helper.Data.WriteGlobalData(SaveKey, Data);
    }

    [XmlIgnore]
    public bool IsFarmHouseStack => Data.CabinStrategy == CabinStrategy.FarmhouseStack;

    [XmlIgnore]
    public bool IsCabinStack => Data.CabinStrategy == CabinStrategy.CabinStack;

    [XmlIgnore]
    public bool IsNone => Data.CabinStrategy == CabinStrategy.None;

    [XmlIgnore]
    public bool UsesHiddenCabins => IsCabinStack || IsFarmHouseStack;

    [XmlIgnore]
    public bool AllowCabinRelocation => Data.AllowCabinRelocation;

    private void SyncFromSettings(ServerSettingsLoader settings)
    {
        Data.MaxPlayers = settings.MaxPlayers;
        Data.CabinStrategy = settings.CabinStrategy;
        Data.ExistingCabinBehavior = settings.ExistingCabinBehavior;
        Data.AllowCabinRelocation = settings.AllowCabinRelocation;
        Save();
    }
}
