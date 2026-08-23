using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;

namespace JunimoServer.Util;

public static class CabinExtensions
{
    public static bool IsOwnedBy(this Cabin cabin, long ownerId)
    {
        return cabin?.owner?.UniqueMultiplayerID == ownerId;
    }

    public static IEnumerable<Warp> GetWarpsToFarm(this Cabin cabin)
    {
        return cabin.warps.Where(warp => warp.TargetName == "Farm");
    }

    /// <summary>
    /// Points the cabin's exit warps at the given Farm tile, always emitting a replication
    /// delta — even when the values are unchanged. Vanilla clients re-derive interior exit
    /// warps locally from the building's position while deserializing a location
    /// introduction (GameLocation's buildings.OnValueAdded → Building.updateInteriorWarps),
    /// which clobbers whatever targets the introduction carried; only a post-introduction
    /// delta restores the server's targets. A NetInt equal-value write is a no-op (no
    /// delta), so a rejoin whose targets are already correct server-side would otherwise
    /// leave that client on its re-derived hidden-stack targets for the whole session.
    /// The bounce value never reaches the wire — a delta serializes the field's current
    /// value at broadcast time, after both writes have landed.
    /// </summary>
    public static void SetWarpsToFarm(this Cabin cabin, Point position)
    {
        foreach (var warp in cabin.GetWarpsToFarm())
        {
            if (warp.TargetX == position.X)
            {
                warp.TargetX = position.X + 1;
            }
            warp.TargetX = position.X;

            if (warp.TargetY == position.Y)
            {
                warp.TargetY = position.Y + 1;
            }
            warp.TargetY = position.Y;
        }
    }

    public static void SetWarpsToFarmCabinDoor(this Cabin cabin)
    {
        cabin.SetWarpsToFarm(cabin.ParentBuilding.getPointForHumanDoor());
    }

    public static void SetWarpsToFarmFarmhouseDoor(this Cabin cabin)
    {
        cabin.SetWarpsToFarm(Game1.getFarm().GetMainFarmHouseEntry());
    }
}
