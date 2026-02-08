using System.Collections.Generic;

namespace JunimoServer.Services.Lobby
{
    /// <summary>
    /// Persistent data for lobby layouts, stored per save file.
    /// </summary>
    public class LobbyData
    {
        /// <summary>
        /// Dictionary of saved layouts by name.
        /// </summary>
        public Dictionary<string, LobbyLayout> Layouts { get; set; } = new();

        /// <summary>
        /// Name of the currently active layout for new players.
        /// </summary>
        public string ActiveLayoutName { get; set; } = "default";

        /// <summary>
        /// Persisted inventory backups for players in editing sessions.
        /// Key: player ID, Value: editing session backup data.
        /// Used for recovery if server crashes during layout editing.
        /// </summary>
        public Dictionary<long, EditingSessionBackup> EditingSessionBackups { get; set; } = new();
    }

    /// <summary>
    /// Persisted backup data for a player's editing session.
    /// Allows recovery of inventory and location if server crashes during editing.
    /// </summary>
    public class EditingSessionBackup
    {
        /// <summary>
        /// Player's unique multiplayer ID.
        /// </summary>
        public long PlayerId { get; set; }

        /// <summary>
        /// Name of the layout being edited.
        /// </summary>
        public string LayoutName { get; set; }

        /// <summary>
        /// Whether this is a new layout (vs editing existing).
        /// </summary>
        public bool IsNewLayout { get; set; }

        /// <summary>
        /// Player's inventory items before editing started.
        /// </summary>
        public List<SerializedItem> InventoryBackup { get; set; } = new();

        /// <summary>
        /// Player's location before editing started.
        /// </summary>
        public string PreviousLocation { get; set; }

        /// <summary>
        /// Player's X tile position before editing started.
        /// </summary>
        public int PreviousX { get; set; }

        /// <summary>
        /// Player's Y tile position before editing started.
        /// </summary>
        public int PreviousY { get; set; }
    }

    /// <summary>
    /// Serialized inventory item for persistence.
    /// </summary>
    public class SerializedItem
    {
        /// <summary>
        /// Qualified item ID, e.g., "(O)128" for Pufferfish.
        /// Null represents an empty inventory slot.
        /// </summary>
        public string ItemId { get; set; }

        /// <summary>
        /// Stack size.
        /// </summary>
        public int Stack { get; set; } = 1;

        /// <summary>
        /// Item quality (0=normal, 1=silver, 2=gold, 4=iridium).
        /// </summary>
        public int Quality { get; set; } = 0;
    }
}
