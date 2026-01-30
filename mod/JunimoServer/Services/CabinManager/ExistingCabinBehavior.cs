namespace JunimoServer.Services.CabinManager
{
    /// <summary>
    /// Controls what happens to visible cabins that already exist on the farm
    /// when the server starts with a stacked CabinStrategy. This applies to
    /// imported saves, map changes, game updates that add cabins, or any other
    /// scenario where cabins end up at real farm positions.
    /// </summary>
    public enum ExistingCabinBehavior
    {
        /// <summary>
        /// Leave existing cabins at their current farm positions.
        /// Only newly created cabins follow the active CabinStrategy.
        /// </summary>
        KeepExisting,

        /// <summary>
        /// Relocate all visible cabins to the hidden stack position on startup.
        /// Use this when switching to CabinStack/FarmhouseStack and you want
        /// all cabins managed uniformly.
        /// </summary>
        MoveToStack
    }
}
