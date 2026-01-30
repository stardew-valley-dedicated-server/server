using JunimoServer.Services.CabinManager;
using JunimoServer.Services.Settings;
using StardewModdingAPI;
using System.Xml.Serialization;

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
            Data = helper.Data.ReadGlobalData<PersistentOptionsSaveData>(SaveKey) ?? new PersistentOptionsSaveData();

            // Capture persisted strategy before overwriting with settings file values
            PreviousCabinStrategy = Data.CabinStrategy;

            // Sync runtime settings from settings file on construction so services
            // always see the current values
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
