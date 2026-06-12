using System.Xml.Serialization;
using JunimoServer.Services.CabinManager;
using JunimoServer.Services.Settings;
using StardewModdingAPI;

namespace JunimoServer.Services.PersistentOption
{
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
        /// CabinManagerService.DetectAndMigrateStrategyChange without a process restart.
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

        private void SyncFromSettings(ServerSettingsLoader settings)
        {
            Data.MaxPlayers = settings.MaxPlayers;
            Data.CabinStrategy = settings.CabinStrategy;
            Data.ExistingCabinBehavior = settings.ExistingCabinBehavior;
            Save();
        }
    }
}
