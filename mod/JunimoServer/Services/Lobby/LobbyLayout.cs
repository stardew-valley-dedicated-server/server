using System.Collections.Generic;

namespace JunimoServer.Services.Lobby
{
    /// <summary>
    /// Represents a saved lobby cabin layout with furniture and decorations.
    /// </summary>
    public class LobbyLayout
    {
        public string Name { get; set; }
        public string CabinSkin { get; set; } = "Log Cabin";
        public int UpgradeLevel { get; set; } = 0;

        // Full cabin state
        public List<SerializedFurniture> Furniture { get; set; } = new();
        public List<SerializedObject> Objects { get; set; } = new();

        /// <summary>
        /// Wallpaper per room/area. Key is room ID (e.g., "Main"), value is wallpaper ID.
        /// </summary>
        public Dictionary<string, string> Wallpapers { get; set; } = new();

        /// <summary>
        /// Flooring per room/area. Key is room ID (e.g., "Main"), value is floor ID.
        /// </summary>
        public Dictionary<string, string> Floors { get; set; } = new();

        /// <summary>
        /// Custom spawn point X coordinate (tile). If null, uses default entry point.
        /// </summary>
        public int? SpawnX { get; set; }

        /// <summary>
        /// Custom spawn point Y coordinate (tile). If null, uses default entry point.
        /// </summary>
        public int? SpawnY { get; set; }
    }

    /// <summary>
    /// Serialized furniture data for lobby layouts.
    /// </summary>
    public class SerializedFurniture
    {
        /// <summary>Item ID, e.g., "(F)1376"</summary>
        public string ItemId { get; set; }
        public int TileX { get; set; }
        public int TileY { get; set; }
        public int Rotation { get; set; }
        /// <summary>Item ID of held object (for tables holding items), or null.</summary>
        public string HeldObjectId { get; set; }
    }

    /// <summary>
    /// Serialized placeable object data for lobby layouts.
    /// </summary>
    public class SerializedObject
    {
        /// <summary>Item ID, e.g., "(BC)FishSmoker"</summary>
        public string ItemId { get; set; }
        public int TileX { get; set; }
        public int TileY { get; set; }
    }
}
