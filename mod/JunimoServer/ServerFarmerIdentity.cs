namespace JunimoServer;

/// <summary>
/// Identity literals for the headless "Server" host farmer that are genuinely constant
/// and shared between the two host-creation paths: <c>GameCreatorService.CreateNewGame</c>
/// (a brand-new game's host) and <c>SaveImportXmlTransform</c> (the clone-blank host installed
/// when importing a save with <c>--swap-host-to</c>). Keeping the literals in one place means
/// the imported host and the new-game host are the same farmer by construction and cannot drift.
///
/// Deliberately excluded:
/// - <c>DisplayName</c>: there is no serialized display-name field — <c>Character.displayName</c>
///   and its backing field are both <c>[XmlIgnore]</c> and recomputed from <c>name</c> at runtime.
///   The new-game path's <c>displayName = Name</c> is a runtime assignment, not an identity field.
/// - <c>WhichPetType</c>: not a constant — the new-game path picks Cat/Dog from <c>config.PetBreed</c>.
///   The import clone-blank host never plays, so it hardcodes its own cosmetic pet default locally.
/// </summary>
internal static class ServerFarmerIdentity
{
    /// <summary>The host farmer's name. Also the runtime display name (derived from name).</summary>
    public const string Name = "Server";

    /// <summary>The host farmer's favorite thing.</summary>
    public const string FavoriteThing = "Junimos";

    /// <summary>The host farmer is always a "customized" farmer (master, never an empty slot).</summary>
    public const bool IsCustomized = true;
}
