using Microsoft.Xna.Framework;
using StardewValley.Buildings;

namespace JunimoServer.Util
{
    public enum CabinRole
    {
        Player,
        SharedLobby,
        IndividualLobby,
        Editing,
    }

    /// <summary>
    /// Single source of truth for cabin position constants and classification.
    /// Cabins are categorized by their tile position on the farm map:
    ///   Player stack:       (-20, -20)
    ///   Shared lobby:       (-21, -21)
    ///   Individual lobbies: (-22..-120, -21)
    ///   Editing cabin:      (<= -121, -21)
    ///   Visible cabins:     real farm coordinates (>= 0)
    /// </summary>
    public static class CabinPositions
    {
        public static readonly Point PlayerStack = new Point(-20, -20);
        public static readonly Point SharedLobby = new Point(-21, -21);
        public const int LobbyRowY = -21;
        public const int EditingThresholdX = -121;

        public static CabinRole Classify(Building building)
        {
            if (building == null || !building.isCabin)
            {
                return CabinRole.Player;
            }

            int x = building.tileX.Value;
            int y = building.tileY.Value;

            if (y == LobbyRowY)
            {
                if (x == SharedLobby.X)
                {
                    return CabinRole.SharedLobby;
                }

                if (x <= EditingThresholdX)
                {
                    return CabinRole.Editing;
                }

                if (x < SharedLobby.X)
                {
                    return CabinRole.IndividualLobby;
                }
            }

            return CabinRole.Player;
        }

        public static bool IsLobbyOrEditing(Building building)
        {
            var role = Classify(building);
            return role == CabinRole.SharedLobby
                || role == CabinRole.IndividualLobby
                || role == CabinRole.Editing;
        }

        public static bool IsInPlayerStack(Building building)
        {
            return building.tileX.Value == PlayerStack.X && building.tileY.Value == PlayerStack.Y;
        }
    }
}
