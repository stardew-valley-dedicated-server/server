using StardewValley;
using StardewValley.Locations;

namespace JunimoServer.Services.AlwaysOn
{
    public static class CabinOverrides
    {
        private static AlwaysOnConfig _config;

        public static void Initialize(AlwaysOnConfig config) => _config = config;

        // Postfix on Cabin.updateEvenIfFarmerIsntHere. Vanilla calls
        // inventoryMutex.Update(...) then releases the lock every frame the host
        // holds it (decompiled Cabin.cs:128-132). We re-acquire inside the same
        // call frame so peers never observe an unlocked window: NetMutex.lockRequest
        // dispatches synchronously via Fire->Poll, so owner.Value flips back to the
        // host before any peer's lockRequest can land on the next tick.
        public static void UpdateEvenIfFarmerIsntHere_Postfix(Cabin __instance)
        {
            if (_config == null || !_config.LockPlayerChests)
            {
                return;
            }

            if (!Game1.IsMasterGame)
            {
                return;
            }

            var owner = __instance.owner;
            var ownerOffline =
                owner == null
                || owner.isUnclaimedFarmhand
                || !Game1.getOnlineFarmers().Contains(owner);
            if (!ownerOffline)
            {
                return;
            }

            // Defensive: vanilla just released, so IsLocked() is normally false.
            // Skip re-acquire if some other code path holds it.
            if (__instance.inventoryMutex.IsLocked())
            {
                return;
            }

            __instance.inventoryMutex.RequestLock();
        }
    }
}
