using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Network;

#nullable enable
namespace JunimoServer.Util;

public static class FarmerExtensions
{
    // Character.currentLocationRef is protected; the public currentLocation getter only
    // exposes the interpolated value.
    private static readonly AccessTools.FieldRef<Character, NetLocationRef> _currentLocationRef =
        AccessTools.FieldRefAccess<Character, NetLocationRef>("currentLocationRef");

    /// <summary>
    /// The location a remote farmer is in or about to be in. A farmhand's replicated
    /// <c>currentLocationRef</c> fields are interpolation-wait netfields, so <c>currentLocation</c>
    /// keeps answering the OLD location for <c>InterpolationTicks</c> (15 server ticks) after
    /// the client's warp delta arrived; the target value is the destination the delta carried.
    /// Null when the name does not resolve on the server.
    /// </summary>
    public static GameLocation? GetTargetLocation(this Farmer farmer)
    {
        var target = _currentLocationRef(farmer);
        var name = target.locationName.TargetValue ?? target.locationName.Value;
        return string.IsNullOrEmpty(name)
            ? null
            : Game1.getLocationFromName(name, target.isStructure.TargetValue);
    }

    public static void WarpHome(this Farmer farmer)
    {
        var cabin = Game1.getFarm().GetCabin(farmer.UniqueMultiplayerID);
        if (cabin == null)
        {
            return;
        }

        var indoors = cabin.GetIndoors<Cabin>();
        if (indoors != null)
        {
            var indoorsName = indoors.NameOrUniqueName;
            var indoorsEntryWarpTarget = indoors.getEntryLocation();

            // Passout does a screen fade and then warps the player
            Game1.server.sendMessage(
                farmer.UniqueMultiplayerID,
                Multiplayer.passout,
                Game1.player,
                new object[]
                {
                    indoorsName,
                    indoorsEntryWarpTarget.X,
                    indoorsEntryWarpTarget.Y,
                    true,
                }
            );

            farmer.currentLocation = indoors;
            farmer.Position = new Vector2(
                indoorsEntryWarpTarget.X * 64f,
                indoorsEntryWarpTarget.Y * 64f
            );
        }
    }
}
