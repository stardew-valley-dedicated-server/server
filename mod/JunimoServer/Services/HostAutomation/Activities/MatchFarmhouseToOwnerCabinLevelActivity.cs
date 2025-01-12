using StardewValley;
using StardewValley.Locations;
using System.Linq;

namespace JunimoServer.Services.HostAutomation.Activities
{
    public class MatchFarmhouseToOwnerCabinLevelActivity : Activity
    {
        public MatchFarmhouseToOwnerCabinLevelActivity() : base(60)
        {
        }

        protected override void OnTick()
        {
            SyncFarmhouseLevel();
        }

        // Sets host farmer HouseUpgradeLevel to the first player/cabin owner, which it is currently always assumed to be the owner
        private void SyncFarmhouseLevel()
        {
            var cabin = Game1.getFarm().buildings.FirstOrDefault(building => building.isCabin);
            if (cabin == null)
            {
                return;
            }

            var owner = ((Cabin)cabin.GetIndoors()).owner;

            if (owner.HouseUpgradeLevel != Game1.player.HouseUpgradeLevel)
            {
                Game1.player.HouseUpgradeLevel = owner.HouseUpgradeLevel;
            }
        }
    }
}
