using System;
using System.IO;
using System.Xml;
using JunimoServer.Services.GameLoader;
using StardewModdingAPI;

namespace JunimoServer.Services.SaveImport;

/// <summary>
/// Owns Layer A of the <c>saves import</c> feature (the pre-load, game-free part) plus the
/// pending-finalize intent record. <see cref="ExecuteImport"/> imports a save in one shot:
///   - <b>as-is</b> (no userId): point the next boot at the save; no file changes.
///   - <b>swap+bind</b> (with a userId): transform the save in place to demote its original
///     <c>&lt;player&gt;</c> owner into a customized cabin farmhand bound to the userId, install a
///     fresh blank "Server" master, persist a finalize intent, and point the next boot at it.
///
/// All engine-touching finalizer work (cabin build, AssignFarmhand, contents/NPC move, userID
/// re-stamp) lives in <c>CabinManagerService</c> (Layer B), which reads/clears the intent via
/// <see cref="TryReadIntent"/>/<see cref="ClearIntent"/>. DI is one-way: <c>CabinManagerService</c>
/// injects this service, never the reverse — a mutual injection would be a constructor cycle that
/// the eager DI container fails the process on. Layer A is engine-free, so this service depends only
/// on <see cref="GameLoaderService"/> (for <c>SetSaveNameToLoad</c>).
/// </summary>
public class SaveImportService : ModService
{
    internal const string SaveKey = "JunimoHost.SaveImport";

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly GameLoaderService _gameLoader;

    public SaveImportService(IModHelper helper, IMonitor monitor, GameLoaderService gameLoader)
        : base(helper, monitor)
    {
        _helper = helper;
        _monitor = monitor;
        _gameLoader = gameLoader;
    }

    /// <summary>
    /// Outcome of an import attempt, returned so the console command and the test endpoint can
    /// report a consistent, structured result.
    /// </summary>
    public class ImportResult
    {
        public bool Success { get; set; }
        public bool Swapped { get; set; }
        public string? Error { get; set; }
        public string SaveName { get; set; } = "";

        /// <summary>The demoted owner's name (swap only), for the result line.</summary>
        public string? FormerOwnerName { get; set; }

        /// <summary>The demoted owner's UniqueMultiplayerID (swap only).</summary>
        public long FormerOwnerUid { get; set; }

        /// <summary>True when a swap re-run only re-pointed an existing pending bind (no re-transform).</summary>
        public bool RepointedBind { get; set; }
    }

    /// <summary>
    /// Imports <paramref name="saveName"/>. When <paramref name="userId"/> is null/empty the import
    /// is "as-is" (no file changes, just retarget boot); otherwise it is a swap+bind in place.
    /// Never throws — all failures are returned as <c>Success=false</c> with a Warn already logged.
    /// </summary>
    public ImportResult ExecuteImport(string saveName, string? userId)
    {
        var result = new ImportResult { SaveName = saveName };
        try
        {
            if (string.IsNullOrWhiteSpace(saveName))
            {
                return Fail(result, "No save name provided.");
            }

            var savesPath = Constants.SavesPath;
            var saveDir = Path.Combine(savesPath, saveName);
            var mainFile = Path.Combine(saveDir, saveName);

            // Validate before any work: folder + primary file present and parseable. "Folder exists"
            // is NOT sufficient — a mid-interrupted-overnight save may have only the engine's
            // backup files, which Layer A does not load; refuse so the operator restores first.
            if (!Directory.Exists(saveDir))
            {
                return Fail(result, $"Save '{saveName}' not found.");
            }
            if (!File.Exists(mainFile))
            {
                return Fail(
                    result,
                    $"Save '{saveName}' has no primary save file ('{saveName}/{saveName}'). "
                        + "If only a backup remains (interrupted overnight save), restore it first."
                );
            }

            // The save must not be the currently-active/loaded one — rewriting the folder the
            // running game is mid-save on would corrupt the live world.
            if (string.Equals(saveName, Constants.SaveFolderName, StringComparison.Ordinal))
            {
                return Fail(
                    result,
                    $"'{saveName}' is the currently-loaded save; cannot import the active save. "
                        + "Import a different save, then restart."
                );
            }

            var isSwap = !string.IsNullOrWhiteSpace(userId);

            // ── As-is mode: no file changes, just retarget boot (the former `select` behavior). ──
            if (!isSwap)
            {
                return ExecuteAsIs(result, saveName, mainFile);
            }

            return ExecuteSwap(result, saveName, saveDir, mainFile, userId!.Trim());
        }
        catch (Exception ex)
        {
            // Never Error (server-side test poison) — Warn + return failure, file untouched.
            return Fail(result, $"Unexpected error: {ex.Message}");
        }
    }

    private ImportResult ExecuteAsIs(ImportResult result, string saveName, string mainFile)
    {
        // Refuse an as-is import of a save with a pending (un-finalized) swap. A swap rewrote this
        // save's <player> into a blank Server master and reparented the owner into <farmhands>,
        // recording a finalize intent that Layer B consumes on the next boot to home+bind the owner.
        // Running as-is now would clear that intent (below) and boot the swapped save with no
        // finalizer — the cabin-less owner's userID gets cleared mid-load and their farmhouse
        // contents/NPCs never move (a half-applied swap with no in-product recovery). Direct the
        // operator to reboot to finalize, or restore a backup to truly revert to as-is.
        var pending = TryReadIntent();
        if (pending != null && string.Equals(pending.SaveName, saveName, StringComparison.Ordinal))
        {
            return Fail(
                result,
                $"'{saveName}' has a pending host-swap awaiting finalize; an as-is import would "
                    + "strand it. Restart the server to finalize the swap, or restore a backup to "
                    + "revert to a plain as-is import."
            );
        }

        // Parse before retargeting boot — otherwise a malformed save selects cleanly here and only
        // fails on the next restart (the swap path gets this for free from its own doc.Load).
        try
        {
            new XmlDocument().Load(mainFile);
        }
        catch (Exception ex)
        {
            return Fail(
                result,
                $"Save '{saveName}' could not be parsed: {ex.Message}. Restore a backup, then import."
            );
        }

        // As-is makes the save's <player> the automated headless host. Correct for a single-player
        // import; for a co-op save whose owner is a real player it is the inversion the feature
        // exists to prevent. The XML can't reliably distinguish the two, so always warn loudly,
        // naming the owner, and proceed (as-is makes no file changes — the operator can re-run swap).
        string ownerName = TryReadPlayerName(mainFile) ?? "(unknown)";
        _monitor.Log(
            $"Importing '{saveName}' as-is: its owner '{ownerName}' becomes the automated headless "
                + "host and can no longer play as that farmer. Re-run with --swap-host-to <id> if you "
                + "meant to keep them as a player.",
            LogLevel.Warn
        );

        _gameLoader.SetSaveNameToLoad(saveName);
        // As-is never writes a finalize intent. Any surviving intent here is for a DIFFERENT save (a
        // pending intent for THIS save was refused above); clear it so a stale other-save swap intent
        // doesn't leak onto this plain as-is boot (Layer B's wrong-save guard would also catch it,
        // but clearing here keeps the boot target and intent consistent).
        ClearIntent();

        Diagnostics.ModEventLog.Emit("save_import_executed", new { saveName, swapped = false });

        result.Success = true;
        result.Swapped = false;
        _monitor.Log($"Imported '{saveName}' as-is. Restart the server to load it.", LogLevel.Info);
        return result;
    }

    private ImportResult ExecuteSwap(
        ImportResult result,
        string saveName,
        string saveDir,
        string mainFile,
        string userId
    )
    {
        // Up-front operator warning (destructive-in-place). No confirm gate — the safety net is the
        // fault-tolerant temp-then-rename transform + the finalizer self-heal.
        _monitor.Log(
            $"`saves import` rewrites the save in place — back up '{saveName}' first if you haven't.",
            LogLevel.Warn
        );

        // ID validation: accept any non-empty all-digit ulong (Steam64 OR Galaxy-uint64 — no
        // 7656119… prefix check, which would reject every legitimate GOG bind).
        if (!IsValidPlatformId(userId))
        {
            return Fail(
                result,
                $"--swap-host-to value '{userId}' is not a valid platform id "
                    + "(expected a non-empty decimal Steam64 or GOG Galaxy id)."
            );
        }

        // Swap on a LAN-configured server: the bind is recorded but authCheck can't enforce it on LAN
        // (every client's getUserID() is "", so the userID match never runs). The bind's job is to
        // scope the demoted owner's farmhand to that platform account so only they can select it; on
        // LAN it's inert — the slot isn't ownership-locked and any LAN player could select it. Warn,
        // proceed (the demotion into a cabin is still useful).
        if (IsLanServer())
        {
            _monitor.Log(
                "This server is LAN — --swap-host-to records the bind but LAN can't enforce it, so the "
                    + "demoted owner's farmhand won't be scoped to the intended account (any LAN player "
                    + "could select it); use a Steam/GOG server for the bind to take effect.",
                LogLevel.Warn
            );
        }

        // Load the main save XML into memory.
        XmlDocument doc;
        try
        {
            doc = new XmlDocument();
            doc.Load(mainFile);
        }
        catch (Exception ex)
        {
            // Malformed input → Warn (never Error), abort before any .tmp write; save untouched.
            return Fail(result, $"Save '{saveName}' could not be parsed: {ex.Message}");
        }

        long newMasterUid;
        long ownerUid;
        try
        {
            SaveImportXmlTransform.ApplySwap(doc, userId, out newMasterUid, out ownerUid);
        }
        catch (SaveImportAlreadySwappedException)
        {
            // Re-import guard: the save's <player> is already a host-swapped Server master. The
            // command can still re-point a pending bind in place (correcting a mistyped id) — that
            // logic lives here, against the persisted intent, not in the XML transform.
            return HandleAlreadySwappedReRun(result, saveName, userId);
        }
        catch (SaveImportException ex)
        {
            return Fail(result, ex.Message);
        }

        // Capture the demoted owner's name from the transformed doc for the result line.
        result.FormerOwnerName = TryReadOwnerNameFromDoc(doc, ownerUid);
        result.FormerOwnerUid = ownerUid;

        // ── Fault-tolerant write: .tmp → validate → atomic rename. ──
        var tmpFile = mainFile + ".tmp";
        try
        {
            // Write the transformed doc to .tmp.
            doc.Save(tmpFile);

            // Validate the .tmp by re-parsing it from disk and re-running the post-transform guards
            // — proves the bytes on disk are loadable and structurally correct before we replace the
            // real file.
            var reparsed = new XmlDocument();
            reparsed.Load(tmpFile);
            SaveImportXmlTransform.ValidatePostTransform(
                reparsed.DocumentElement!,
                newMasterUid,
                ownerUid,
                userId
            );

            // Atomic rename of the main file. File.Replace requires dest to exist (it does here);
            // it is atomic on the shared saves volume.
            File.Replace(tmpFile, mainFile, destinationBackupFileName: null);
        }
        catch (Exception ex)
        {
            // A crash/validation-failure before/at the rename leaves the real file byte-intact and
            // the boot target untouched. Discard the .tmp.
            TryDelete(tmpFile);
            return Fail(
                result,
                $"Transform validation/write failed; save left unchanged: {ex.Message}"
            );
        }

        // Regenerate SaveGameInfo from the post-transform <player> (swap only) so list/info views and
        // external tooling don't show the old human host. SaveGameInfo is NOT read on this server's
        // direct-load boot path, so a regen failure is non-fatal: the main save is already transformed
        // and the bind must still be recorded + boot retargeted, or the swap is left half-applied with
        // no in-product recovery (a re-run would hit the already-swapped guard with no pending intent).
        // So Warn and continue — do NOT abort.
        if (!TryRegenerateSaveGameInfo(doc, saveDir, out var sgiError))
        {
            _monitor.Log(
                $"Main save transformed but SaveGameInfo regen failed: {sgiError}. Boot and the bind "
                    + "are unaffected (SaveGameInfo isn't read on load); the list/info view may show a "
                    + "stale host name until the next save.",
                LogLevel.Warn
            );
        }

        // Past the on-disk commit, a failure must not return overall failure: the intent governs
        // recovery. If the intent can't be persisted, leave the boot target alone — a swapped save
        // with no intent boots into the stranded half-apply (cabin-less owner's userID cleared
        // mid-load), whereas an un-booted swapped save is still recoverable (restore a backup, or
        // re-run for the already-swapped re-point path).
        try
        {
            WriteIntent(
                new PendingFinalize
                {
                    SaveName = saveName,
                    OwnerUid = ownerUid,
                    UserId = userId,
                }
            );
        }
        catch (Exception ex)
        {
            _monitor.Log(
                $"'{saveName}' was transformed but the finalize intent could not be persisted "
                    + $"({ex.Message}); boot target left unchanged to avoid a stranded swap. Re-run "
                    + "`saves import` to retry the bind, or restore a backup to revert.",
                LogLevel.Warn
            );
            return Fail(
                result,
                $"Save transformed but finalize intent could not be persisted: {ex.Message}. "
                    + "Boot target unchanged; re-run to retry."
            );
        }

        // Intent is durable past this point, so a boot-retarget failure is recoverable — Warn, don't fail.
        try
        {
            _gameLoader.SetSaveNameToLoad(saveName);
        }
        catch (Exception ex)
        {
            _monitor.Log(
                $"'{saveName}' transformed and finalize intent persisted, but the boot target could "
                    + $"not be set ({ex.Message}). Set it manually or re-run `saves import` before "
                    + "restarting to finalize.",
                LogLevel.Warn
            );
        }

        Diagnostics.ModEventLog.Emit(
            "save_import_executed",
            new
            {
                saveName,
                swapped = true,
                ownerUid,
                // Never the raw user id.
                hasUserId = true,
            }
        );

        result.Success = true;
        result.Swapped = true;
        _monitor.Log(
            $"Imported '{saveName}' with host swap: former owner "
                + $"'{result.FormerOwnerName ?? ownerUid.ToString()}' demoted to a cabin farmhand bound "
                + "to the provided id. Restart the server to finalize.",
            LogLevel.Info
        );
        return result;
    }

    /// <summary>
    /// Handles a swap re-run against a save whose <c>&lt;player&gt;</c> is already a Server master.
    /// Same id → true no-op. Different id while the intent is still pending (pre-reboot) → re-point
    /// <see cref="PendingFinalize.UserId"/> in place (the owner is already correctly reparented).
    /// Different id after the finalizer already consumed the intent → can't re-bind via intent;
    /// direct the operator to /unlink or restore-and-reimport.
    /// </summary>
    private ImportResult HandleAlreadySwappedReRun(
        ImportResult result,
        string saveName,
        string userId
    )
    {
        var data = ReadData();
        var pending = data.Pending;

        if (pending != null && string.Equals(pending.SaveName, saveName, StringComparison.Ordinal))
        {
            if (string.Equals(pending.UserId, userId, StringComparison.Ordinal))
            {
                _monitor.Log(
                    $"'{saveName}' is already host-swapped with this id — nothing to do.",
                    LogLevel.Warn
                );
                result.Success = true;
                result.Swapped = true;
                return result;
            }

            // Different id, pending not yet consumed: re-point in place. Re-run the userID-collision
            // guard against the new id first (the demoted owner is already reparented; only the bind
            // changes). The owner already carries the new structure, so check the transformed file's
            // farmhands for a conflict with the new id.
            var conflict = FindUserIdConflictInSave(saveName, userId, pending.OwnerUid);
            if (conflict != null)
            {
                return Fail(
                    result,
                    $"The new id collides with existing farmhand '{conflict}'; pending bind unchanged."
                );
            }

            pending.UserId = userId;
            WriteIntent(pending);
            // Also rewrite the on-disk <userID> so the file and intent agree — otherwise a finalizer
            // abort before its live re-stamp would leave the owner bound to the OLD id (defeating the
            // whole point of re-pointing). Best-effort: the live re-stamp from the intent is primary.
            TryRewriteOwnerUserIdInSave(saveName, pending.OwnerUid, userId);
            _gameLoader.SetSaveNameToLoad(saveName);
            _monitor.Log($"Updated pending bind for '{saveName}' to the new id.", LogLevel.Warn);
            result.Success = true;
            result.Swapped = true;
            result.RepointedBind = true;
            result.FormerOwnerUid = pending.OwnerUid;
            return result;
        }

        // Already swapped AND no matching pending intent (finalizer already ran, or a different
        // save's intent is pending): can't re-bind through the import path.
        return Fail(
            result,
            $"'{saveName}' is already host-swapped and the bind is already applied. To change it, "
                + "use /unlink + reconnect, or restore a backup and re-import."
        );
    }

    // ── Intent record access (read/cleared by Layer B) ──────────────────────────────

    /// <summary>Reads the pending finalize intent, or null when nothing is pending.</summary>
    public PendingFinalize TryReadIntent() => ReadData().Pending;

    /// <summary>Clears the pending finalize intent (single-shot — cleared on every finalize exit).</summary>
    public void ClearIntent()
    {
        _helper.Data.WriteGlobalData(SaveKey, new SaveImportData { Pending = null });
    }

    private SaveImportData ReadData() =>
        _helper.Data.ReadGlobalData<SaveImportData>(SaveKey) ?? new SaveImportData();

    private void WriteIntent(PendingFinalize pending)
    {
        _helper.Data.WriteGlobalData(SaveKey, new SaveImportData { Pending = pending });
    }

    // ── SaveGameInfo regen ──────────────────────────────────────────────────────────

    private bool TryRegenerateSaveGameInfo(
        XmlDocument transformedSave,
        string saveDir,
        out string error
    )
    {
        error = "";
        try
        {
            var info = SaveImportXmlTransform.BuildSaveGameInfo(transformedSave);
            if (info == null)
            {
                error = "could not build <Farmer> from transformed <player>";
                return false;
            }
            var sgiPath = Path.Combine(saveDir, "SaveGameInfo");
            var sgiTmp = sgiPath + ".tmp";
            try
            {
                info.Save(sgiTmp);
                // SaveGameInfo always pre-exists on a real save folder (next to the main file);
                // File.Move covers the (defensive) absent case.
                if (File.Exists(sgiPath))
                {
                    File.Replace(sgiTmp, sgiPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(sgiTmp, sgiPath);
                }
            }
            catch
            {
                TryDelete(sgiTmp); // never leave a junk .tmp on the volume
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── Small read-only helpers ──────────────────────────────────────────────────────

    // A platform userID (Steam64 or GOG Galaxy id) is a non-empty decimal that fits in ulong. Use
    // ulong.TryParse (invariant, no sign/whitespace) — NOT `All(char.IsDigit)`, which accepts both
    // ulong-overflowing strings and non-ASCII Unicode decimal digits, either of which stamps a bind
    // no real client's getUserID() can ever match (a permanently unauthenticatable cabin).
    private static bool IsValidPlatformId(string id) =>
        ulong.TryParse(
            id,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out _
        );

    private static bool IsLanServer() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_AUTH_URL"));

    private static string? TryReadPlayerName(string mainFile)
    {
        try
        {
            var doc = new XmlDocument();
            doc.Load(mainFile);
            return doc.SelectSingleNode("//SaveGame/player/name")?.InnerText;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadOwnerNameFromDoc(XmlDocument transformedDoc, long ownerUid)
    {
        try
        {
            var farmhands = transformedDoc.SelectNodes("//SaveGame/farmhands/Farmer");
            if (farmhands == null)
            {
                return null;
            }
            foreach (XmlElement f in farmhands)
            {
                var uidText = f.SelectSingleNode("UniqueMultiplayerID")?.InnerText;
                if (long.TryParse(uidText, out var uid) && uid == ownerUid)
                {
                    return f.SelectSingleNode("name")?.InnerText;
                }
            }
        }
        catch
        {
            // best-effort label only
        }
        return null;
    }

    /// <summary>
    /// Scans an on-disk save's farmhands (and the owner) for a <c>&lt;userID&gt;</c> matching
    /// <paramref name="userId"/>, EXCLUDING the demoted owner identified by
    /// <paramref name="excludeOwnerUid"/> (whose bind is the one being changed). Returns the
    /// conflicting farmer's name, or null.
    /// </summary>
    private static string? FindUserIdConflictInSave(
        string saveName,
        string userId,
        long excludeOwnerUid
    )
    {
        try
        {
            var mainFile = Path.Combine(Constants.SavesPath, saveName, saveName);
            var doc = new XmlDocument();
            doc.Load(mainFile);
            var farmhands = doc.SelectNodes("//SaveGame/farmhands/Farmer");
            if (farmhands == null)
            {
                return null;
            }
            foreach (XmlElement f in farmhands)
            {
                var uidText = f.SelectSingleNode("UniqueMultiplayerID")?.InnerText;
                if (long.TryParse(uidText, out var uid) && uid == excludeOwnerUid)
                {
                    continue;
                }
                if ((f.SelectSingleNode("userID")?.InnerText ?? "") == userId)
                {
                    var name = f.SelectSingleNode("name")?.InnerText;
                    return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
                }
            }
        }
        catch
        {
            // If we can't read it, don't block the re-point; the finalizer's own guards still apply.
        }
        return null;
    }

    /// <summary>
    /// Rewrites the demoted owner's <c>&lt;userID&gt;</c> in the save file to <paramref name="userId"/>
    /// (temp-then-atomic-rename), so a re-pointed bind is correct on disk too — not just in the intent.
    /// Without this, a finalizer abort before the live userID re-stamp would leave the owner bound to
    /// the OLD id (the value vanilla's ResetFarmhandState read from the file at load). Returns false if
    /// the owner or file can't be resolved (caller treats the file write as best-effort; the live
    /// re-stamp from the intent is still the primary path).
    /// </summary>
    private bool TryRewriteOwnerUserIdInSave(string saveName, long ownerUid, string userId)
    {
        try
        {
            var mainFile = Path.Combine(Constants.SavesPath, saveName, saveName);
            var doc = new XmlDocument();
            doc.Load(mainFile);
            var farmhands = doc.SelectNodes("//SaveGame/farmhands/Farmer");
            if (farmhands == null)
            {
                return false;
            }
            foreach (XmlElement f in farmhands)
            {
                var uidText = f.SelectSingleNode("UniqueMultiplayerID")?.InnerText;
                if (!long.TryParse(uidText, out var uid) || uid != ownerUid)
                {
                    continue;
                }
                var userIdNode = f.SelectSingleNode("userID");
                if (userIdNode == null)
                {
                    userIdNode = doc.CreateElement("userID");
                    f.PrependChild(userIdNode);
                }
                userIdNode.InnerText = userId;

                var tmp = mainFile + ".tmp";
                try
                {
                    doc.Save(tmp);
                    File.Replace(tmp, mainFile, destinationBackupFileName: null);
                }
                catch
                {
                    TryDelete(tmp); // never leave a junk .tmp on the volume
                    throw;
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _monitor.Log(
                $"Re-point: could not rewrite the on-disk bind for '{saveName}' ({ex.Message}); the "
                    + "live finalizer re-stamp from the updated intent still applies on a clean boot.",
                LogLevel.Warn
            );
            return false;
        }
    }

    private ImportResult Fail(ImportResult result, string message)
    {
        _monitor.Log($"saves import: {message}", LogLevel.Warn);
        result.Success = false;
        result.Error = message;
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup; a stray .tmp is harmless and re-runnable
        }
    }
}
