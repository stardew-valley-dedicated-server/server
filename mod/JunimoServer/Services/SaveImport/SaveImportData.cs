namespace JunimoServer.Services.SaveImport;

/// <summary>
/// SMAPI global-data record for the pending save-import finalize intent. Mirrors the shape of
/// <c>GameLoaderSaveData</c>; persisted under the key <see cref="SaveImportService.SaveKey"/>.
/// Written by the command at swap-import execute-time (alongside <c>SetSaveNameToLoad</c>);
/// read and cleared by the Layer B finalizer in <c>CabinManagerService.OnSaveLoaded</c>.
/// As-is imports never write <see cref="Pending"/>.
/// </summary>
internal class SaveImportData
{
    /// <summary>Null when nothing is pending. A single record (not a queue) — last write wins.</summary>
    public PendingFinalize Pending { get; set; } = null;
}

/// <summary>
/// The one-shot finalize intent. No homeLocation/cabin name is stored — Layer B derives those
/// live from the loaded world. Public because <see cref="SaveImportService.TryReadIntent"/>
/// (a public method on the public service) returns it.
/// </summary>
public class PendingFinalize
{
    /// <summary>The save folder name this finalize targets (the in-place-transformed save).</summary>
    public string SaveName { get; set; } = "";

    /// <summary>The demoted owner's UniqueMultiplayerID (unchanged across the transform).</summary>
    public long OwnerUid { get; set; }

    /// <summary>The platform userID to bind the demoted owner to on load.</summary>
    public string UserId { get; set; } = "";
}
