using JunimoServer.Services.CabinManager;

namespace JunimoServer.Services.PersistentOption
{
    public class PersistentOptionsSaveData
    {
        public int MaxPlayers { get; set; } = 6;

        public CabinStrategy CabinStrategy { get; set; } = CabinStrategy.CabinStack;

        public ExistingCabinBehavior ExistingCabinBehavior { get; set; } = ExistingCabinBehavior.KeepExisting;
    }
}
