namespace JunimoServer.Services.CabinManager
{
    // TODO: Before further attempting to unravel this, fully understand and document what each is supposed to, and what it actually does!
    // Then evaluate if both are enough, or if we need to adjust this. Then fix the code!
    // TODO: We also need a e.g. CabinStrategyChangeHandler (deconstruct and move relocated cabins back to stack etc)
    public enum CabinStrategy
    {
        /// <summary>
        /// Use multiple instanced Cabins.
        ///
        /// Starts with a single instanced cabin building, where each player can "move away" by relocating their cabin by visiting Robin as usual.
        /// Once relocated, a cabin can deconstructed again, which will make the player "move in" to the instanced cabin building again.
        /// 
        /// Under the hood, this works by keeping a set amount of invisible and unassigned cabins in a location out of bounds,
        /// which are dynamically assigned to new and existing players. We simply update teleport locations for entering and
        /// leaving cabins.
        ///
        /// Please note that you can not visit another players cabin at the moment, but this is planned for the future.
        /// </summary>
        CabinStack,

        /// <summary>
        /// Use one instanced Farmhouse.
        ///
        /// 
        /// Please note that you can not visit another players farmhouse at the moment, but this is planned for the future.
        /// </summary>
        FarmhouseStack
    }
}
