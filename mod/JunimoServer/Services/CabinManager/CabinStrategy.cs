namespace JunimoServer.Services.CabinManager
{
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
        /// Use one instanced Farmhouse.
        ///
        /// All cabins remain hidden. Cabin door warps are redirected to the main farmhouse,
        /// so every player shares the farmhouse interior.
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
        None
    }
}
