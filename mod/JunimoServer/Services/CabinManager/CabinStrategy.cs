namespace JunimoServer.Services.CabinManager;

public enum CabinStrategy
{
    /// <summary>
    /// Use multiple instanced Cabins.
    ///
    /// Keeps a pool of invisible cabins at a hidden out-of-bounds location.
    /// Each player is dynamically assigned a cabin, which is relocated client-side
    /// so only the owner sees it at a visible farm position.
    /// Players can "move away" by relocating their cabin via Robin.
    /// </summary>
    CabinStack,

    /// <summary>
    /// Hidden cabins that exit at the main farmhouse's front door.
    ///
    /// All cabins remain hidden and each player keeps their own cabin interior. Cabin-exit
    /// warps are redirected to the main farmhouse's front-door tile on the Farm map, so every
    /// player steps out at the same spot. The main farmhouse interior stays reserved for the
    /// server host — players entering it are warped back to their own cabin.
    /// </summary>
    FarmhouseStack,

    /// <summary>
    /// Vanilla-like cabin behavior.
    ///
    /// Cabins are placed at real farm positions (map-designated spots) and behave
    /// like normal vanilla cabins with visible doors and standard warps.
    /// Auto-cabin creation still occurs so new players can always join,
    /// since there is no human host to visit Robin on a dedicated server.
    /// No message interception or farmhouse access restriction is applied.
    /// </summary>
    None,
}
