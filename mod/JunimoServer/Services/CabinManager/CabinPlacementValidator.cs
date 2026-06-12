using System;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;

namespace JunimoServer.Services.CabinManager;

/// <summary>
/// Read-only mirror of vanilla <c>buildStructure</c>'s safety checks (no state mutation).
/// Source of truth: decompiled buildStructure:16637-16717, isBuildable:17096-17132.
///
/// Two deliberate divergences:
/// - Map-property reads go through <c>farm</c>, not <c>Game1.currentLocation</c> as
///   <c>isBuildable</c> does — the AlwaysOn host warps off-farm (e.g. FarmHouse), where
///   the vanilla path would read the wrong location's tiles and reject every valid slot.
/// - Farmer-collision uses absolute tile coords, not <c>buildStructure</c>'s loop-index
///   offsets (:16668) — a vanilla bug we don't reproduce — and skips the host farmer
///   (see the loop below).
/// </summary>
public static class CabinPlacementValidator
{
    public static bool TryValidate(
        Farm farm,
        Building cabin,
        Point topLeft,
        out string failureReason
    )
    {
        var buildableRect = farm.GetBuildableRectangle();

        for (int dy = 0; dy < cabin.tilesHigh.Value; dy++)
        {
            for (int dx = 0; dx < cabin.tilesWide.Value; dx++)
            {
                var tile = new Vector2(topLeft.X + dx, topLeft.Y + dy);

                // The cabin being moved may overlap its own current footprint.
                if (farm.buildings.Contains(cabin) && cabin.occupiesTile(tile))
                {
                    continue;
                }

                if (!IsTileBuildable(farm, cabin, tile, buildableRect, out failureReason))
                {
                    return false;
                }

                foreach (var farmer in farm.farmers)
                {
                    // Skip the AlwaysOn host: it stands parked on the Farm but is
                    // invisible and has ignoreCollisions set (AlwaysOn.cs), so a cabin
                    // placed over it never traps it — blocking on it would be a false
                    // positive players can't diagnose. Honor real (visible) farmhands.
                    if (farmer.IsMainPlayer)
                    {
                        continue;
                    }

                    if (
                        farmer
                            .GetBoundingBox()
                            .Intersects(new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64))
                    )
                    {
                        failureReason = "another player is standing there";
                        return false;
                    }
                }
            }
        }

        // Door-front tile (one south of the human door) must be buildable or a path.
        if (cabin.humanDoor.Value != new Point(-1, -1))
        {
            var doorFront = new Vector2(
                topLeft.X + cabin.humanDoor.X,
                topLeft.Y + cabin.humanDoor.Y + 1
            );
            bool selfOverlap = farm.buildings.Contains(cabin) && cabin.occupiesTile(doorFront);
            if (
                !selfOverlap
                && !farm.isPath(doorFront)
                && !IsTileBuildable(farm, cabin, doorFront, buildableRect, out failureReason)
            )
            {
                return false;
            }
        }

        var prevention = cabin.isThereAnythingtoPreventConstruction(farm, topLeft.ToVector2());
        if (prevention != null)
        {
            failureReason = prevention;
            return false;
        }

        failureReason = "";
        return true;
    }

    /// <summary>One tile of <c>isBuildable</c>'s non-passable branch (:17111-17131).</summary>
    private static bool IsTileBuildable(
        Farm farm,
        Building cabin,
        Vector2 tile,
        Rectangle buildableRect,
        out string failureReason
    )
    {
        if (buildableRect != Rectangle.Empty && !buildableRect.Contains((int)tile.X, (int)tile.Y))
        {
            failureReason = "out of bounds";
            return false;
        }

        var other = farm.getBuildingAt(tile);
        if (other != null && other != cabin && !other.isMoving)
        {
            failureReason = "another building is in the way";
            return false;
        }

        // (O)590 is the artifact spot, which buildStructure permits building over
        // (isBuildable:17116) — there is no isArtifactSpot helper.
        bool placeable =
            farm.CanItemBePlacedHere(
                tile,
                itemIsPassable: false,
                CollisionMask.All,
                ~CollisionMask.Objects,
                useFarmerTile: true
            )
            || farm.getObjectAtTile((int)tile.X, (int)tile.Y)?.QualifiedItemId == "(O)590";
        if (!placeable)
        {
            failureReason = "blocked by terrain or object";
            return false;
        }

        // Map "Buildable"/"Diggable" gate (isBuildable:17122-17129). The
        // LooserBuildRestrictions branch (:17118) is omitted: it's an opt-in map
        // property, and omitting it only makes us stricter (reject a buildable tile),
        // never laxer — a safe direction for a placement guard.
        var buildableProp = farm.doesTileHavePropertyNoNull(
            (int)tile.X,
            (int)tile.Y,
            "Buildable",
            "Back"
        );
        if (buildableProp.Equals("f", StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "blocked by terrain or object";
            return false;
        }
        bool buildableYes =
            buildableProp.Equals("t", StringComparison.OrdinalIgnoreCase)
            || buildableProp.Equals("true", StringComparison.OrdinalIgnoreCase);
        bool diggable =
            farm.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back") != null;
        if (!buildableYes && !diggable)
        {
            failureReason = "blocked by terrain or object";
            return false;
        }

        failureReason = "";
        return true;
    }
}
