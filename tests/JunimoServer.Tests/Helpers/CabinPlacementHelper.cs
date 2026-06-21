using JunimoServer.Tests.Clients;
using Xunit;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Shared setup for !cabin placement tests: positions the farmer at a known-clear
/// standard-farm tile and clears the prospective cabin footprint, so
/// CabinPlacementValidator isn't tripped by the farmhouse or spawn-time debris.
/// </summary>
public static class CabinPlacementHelper
{
    // Open western field on the standard farm, clear of the farmhouse (door at 64,15),
    // greenhouse (25,10), and shipping bin. !cabin places the cabin at farmer.Tile + (1,0).
    public const int FarmerTileX = 40;
    public const int FarmerTileY = 18;

    // A second known-clear western-field tile for a concurrent farmer, far enough below the
    // primary tile that the two 5x3 footprints (rows FarmerTileY and SecondFarmerTileY) can't
    // overlap. (41..45, 18) vs (41..45, 30) — 12 rows apart.
    public const int SecondFarmerTileX = 40;
    public const int SecondFarmerTileY = 30;

    /// <summary>Where !cabin places the cabin's top-left when the farmer stands at the tile above.</summary>
    public static readonly (int X, int Y) ExpectedCabinTile = (FarmerTileX + 1, FarmerTileY);

    /// <summary>Expected cabin top-left for a farmer warped to (<paramref name="tileX"/>,<paramref name="tileY"/>).</summary>
    public static (int X, int Y) ExpectedCabinTileFor(int tileX, int tileY) => (tileX + 1, tileY);

    /// <summary>
    /// Warps the farmer to <see cref="FarmerTileX"/>,<see cref="FarmerTileY"/> and clears the
    /// 5x3 cabin footprint plus the door-front row at farmer.Tile + (1,0). Debris spawn on a
    /// new farm is random per seed, so the clear is load-bearing for a deterministic placement.
    /// </summary>
    public static Task WarpAndClearFootprintAsync(GameTestClient client, CancellationToken ct) =>
        WarpAndClearFootprintAsync(client, FarmerTileX, FarmerTileY, ct);

    /// <summary>
    /// Warps the farmer to (<paramref name="tileX"/>,<paramref name="tileY"/>) and clears the
    /// 5x3 cabin footprint plus the door-front row at farmer.Tile + (1,0). Parametrized so two
    /// concurrent farmers can place to non-overlapping tiles.
    /// </summary>
    public static async Task WarpAndClearFootprintAsync(
        GameTestClient client,
        int tileX,
        int tileY,
        CancellationToken ct
    )
    {
        var warp = await client.Actions.Warp("Farm", tileX, tileY);
        Assert.True(warp?.Success == true, $"Warp to Farm failed: {warp?.Error}");
        var arrived = await client.WaitForLocationAsync("^Farm$", ct: ct);
        Assert.True(arrived is not null, "Warp to Farm did not complete before clearing footprint");

        var cleared = await client.Actions.ClearArea("Farm", tileX + 1, tileY, width: 5, height: 4);
        Assert.True(cleared?.Success == true, $"ClearArea failed: {cleared?.Error}");
    }
}
