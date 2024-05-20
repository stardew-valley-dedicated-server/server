using JunimoServer.Services.CabinManager;

namespace JunimoServer.Services.GameCreator
{
    public class NewGameConfig
    {
        public int WhichFarm { get; set; } = 0;
        public bool UseSeparateWallets { get; set; } = true;
        public int StartingCabins { get; set; } = 1;
        public bool CatPerson { get; set; } = false;
        public string FarmName { get; set; } = "Junimo";

        public int MaxPlayers { get; set; } = 4;

        public int CabinStrategy { get; set; } = (int)JunimoServer.Services.CabinManager.CabinStrategy.CabinStack;

        public override string ToString()
        {
            return
                $"{nameof(WhichFarm)}: {WhichFarm}, {nameof(UseSeparateWallets)}: {UseSeparateWallets}, {nameof(StartingCabins)}: {StartingCabins}, {nameof(CatPerson)}: {CatPerson}, {nameof(FarmName)}: {FarmName}, {nameof(MaxPlayers)}: {MaxPlayers}";
        }
    }
}