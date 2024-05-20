using System.Linq;
using StardewValley;
using StardewValley.Locations;

namespace JunimoServer.Services.HostAutomation.Activities
{
    public class MatchFarmhouseToOwnerCabinLevelActivity : Activity
    {
        public MatchFarmhouseToOwnerCabinLevelActivity() : base(60)
        {
        }

        protected override void OnTick(int tickNum)
        {
            var owner = ((Cabin)Game1.getFarm().buildings.First(building => building.isCabin).indoors.Value).owner;
            if (owner.HouseUpgradeLevel != Game1.player.HouseUpgradeLevel)
            {
                Game1.player.HouseUpgradeLevel = owner.HouseUpgradeLevel;
            }
        }
    }
}