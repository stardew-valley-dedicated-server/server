using JunimoServer.Services.CabinManager;
using StardewModdingAPI;
using System.Xml.Serialization;

namespace JunimoServer.Services.PersistentOption
{
    public class PersistentOptions
    {
        private const string SaveKey = "JunimoHost.PersistentOptions";

        private readonly IModHelper _helper;
        public PersistentOptionsSaveData Data { get; private set; }

        public PersistentOptions(IModHelper helper)
        {
            _helper = helper;
            Data = helper.Data.ReadGlobalData<PersistentOptionsSaveData>(SaveKey) ?? new PersistentOptionsSaveData();
        }

        public void SetPersistentOptions(PersistentOptionsSaveData optionsSaveData)
        {
            _helper.Data.WriteGlobalData(SaveKey, optionsSaveData);
            Data = optionsSaveData;
        }

        [XmlIgnore]
        public bool IsFarmHouseStack
        {
            get => Data.CabinStrategy == CabinStrategy.FarmhouseStack;
        }

        [XmlIgnore]
        public bool IsCabinStack
        {
            get => !IsFarmHouseStack;
        }
    }
}
