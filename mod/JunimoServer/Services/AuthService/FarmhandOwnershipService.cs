using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using JunimoServer.Services.NetworkTweaks;
using JunimoServer.Services.Settings;
using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Network;

namespace JunimoServer.Services.Auth;

/// <summary>A farmhand's platform owner: which transport family owns the slot, the platform's
/// numeric id (Steam64 or Galaxy uint64) as a decimal string, and where the record came from —
/// <see cref="FarmhandOwnershipService.OriginClaim"/> (written by the approve recorder) or
/// <see cref="FarmhandOwnershipService.OriginOperator"/> (rebind / save-import bind). The
/// abandoned-claim sweep clears only claim-origin records, so operator pre-assignments on
/// not-yet-customized slots survive restarts.</summary>
public class FarmhandOwnerRecord
{
    public string Platform { get; set; } = "";
    public string Id { get; set; } = "";
    public string Origin { get; set; } = FarmhandOwnershipService.OriginClaim;
}

/// <summary>On-disk schema of the per-save ownership store (farmhand UniqueMultiplayerID → owner).</summary>
public class FarmhandOwnershipFile
{
    public Dictionary<long, FarmhandOwnerRecord> Farmhands { get; set; } = new();
}

/// <summary>Verdict of the ownership matrix for one (farmhand, connection) pair.</summary>
public enum ClaimVerdict
{
    /// <summary>Admit the claim / show the slot.</summary>
    Allow,

    /// <summary>Fresh slot — vanilla owns the enableFarmhandCreation check at join time; the
    /// list-time filter applies it itself.</summary>
    AllowFresh,

    RejectOwnedByOther,
    RejectLegacyNoIdentity,
    RejectLegacyMismatch,
    RejectLanPoolOnly,
}

/// <summary>
/// Server-authoritative farmhand ownership: a persisted map keyed by transport-authenticated
/// identity (the SDR connection's Steam64, or the Galaxy connection's uint64), enforced at the
/// single claim choke point <c>GameServer.checkFarmhandRequest</c>.
///
/// Vanilla's ownership story is client-side graying plus a join-time <c>authCheck</c> that is
/// skipped whenever either side's userID is empty — which is always the case on our SDR
/// transport (the server passes <c>""</c> like vanilla <c>SteamNetServer</c>, because clients
/// stamp GOG Galaxy pseudo-ids that share no id space with the connection's Steam64). This
/// service closes that gap: ownership is born from the transport at the exact approve moment
/// (never from client-declared data), and the gate rejects claims whose transport identity
/// doesn't match the recorded owner.
///
/// The store is one JSON file inside the save folder, so binds travel with every Saves-folder
/// copy (backup, restore, migration to another server). Mutations write through immediately.
/// All store access is game-thread-only (matches the <c>_reservedFarmhands</c> invariant in
/// <see cref="FarmhandSenderService"/>).
/// </summary>
public class FarmhandOwnershipService : ModService
{
    private const string StoreFileName = "farmhand-ownership.json";

    public const string OriginClaim = "claim";
    public const string OriginOperator = "operator";

    /// <summary>Marker platform for an operator-released slot: claimable by any transport, first
    /// successful claim becomes the owner. Never exposed through <see cref="TryGetOwner"/>.</summary>
    private const string PlatformReleased = "released";

    /// <summary>Static reference for the Harmony prefix (which cannot be an instance method).</summary>
    private static FarmhandOwnershipService _instance;

    internal static FarmhandOwnershipService Instance => _instance;

    private readonly ServerSettingsLoader _settings;

    private Dictionary<long, FarmhandOwnerRecord> _records = new();
    private string _loadedSaveName;

    public FarmhandOwnershipService(
        IModHelper helper,
        IMonitor monitor,
        Harmony harmony,
        ServerSettingsLoader settings
    )
        : base(helper, monitor)
    {
        if (_instance != null)
        {
            throw new InvalidOperationException(
                "FarmhandOwnershipService already initialized - only one instance allowed"
            );
        }

        _instance = this;
        _settings = settings;

        // Enforcement gate + ownership recorder on the single claim choke point all three
        // transports route through (including crafted message-2 packets). Default (Normal)
        // priority keeps this BELOW NetworkTweaker's Priority.High SafeLookup prefix, whose
        // early-returns own the farmer-null / game-unavailable / uid-missing states.
        harmony.Patch(
            original: AccessTools.Method(
                typeof(GameServer),
                nameof(GameServer.checkFarmhandRequest)
            ),
            prefix: new HarmonyMethod(
                typeof(FarmhandOwnershipService),
                nameof(CheckFarmhandRequest_OwnershipGate_Prefix)
            )
        );

        // Protects ownership-bound slots from the game's save-load re-homing. When a save
        // loads, ResetFarmhandState re-checks every farmhand's home; one whose cabin doesn't
        // resolve is assigned another cabin, and Cabin.CanAssignTo allows taking a cabin
        // whose farmhand is not yet customized (isUnclaimedFarmhand sees neither stamps nor
        // the ownership map) — Cabin.AssignFarmhand then permanently DELETES that displaced
        // farmhand (Cabin.cs:100 → FarmerTeam.DeleteFarmhand → farmhandData.Remove). This
        // happens routinely at load, so without the guard an operator pre-assignment (a
        // rebind on a not-yet-customized slot) can be destroyed by the next reload.
        harmony.Patch(
            original: AccessTools.Method(
                typeof(StardewValley.Locations.Cabin),
                nameof(StardewValley.Locations.Cabin.CanAssignTo)
            ),
            postfix: new HarmonyMethod(
                typeof(FarmhandOwnershipService),
                nameof(CanAssignTo_ProtectOwnedSlot_Postfix)
            )
        );

        Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
    }

    /// <summary>Whether the gate rejects and the visibility filter narrows. When false both
    /// revert to permissive behavior, but the recorder keeps recording (claims are legal in
    /// that mode, so the map stays truthful for a later re-enable).</summary>
    public bool EnforcementEnabled => _settings.EnforceFarmhandOwnership;

    // ── Store access (game-thread only) ─────────────────────────────────────────────

    private static string StorePath =>
        string.IsNullOrEmpty(Constants.SaveFolderName)
            ? null
            : Path.Combine(Constants.SavesPath, Constants.SaveFolderName, StoreFileName);

    /// <summary>Binds the in-memory store to the current save, (re)loading the file when the
    /// save changed. Lazy so callers need no ordering guarantee against SaveLoaded handlers
    /// (the save-import finalizer records a bind from its own SaveLoaded handler).</summary>
    private bool EnsureLoaded()
    {
        var saveName = Constants.SaveFolderName;
        if (string.IsNullOrEmpty(saveName))
        {
            return false;
        }

        if (_loadedSaveName == saveName)
        {
            return true;
        }

        _records = ReadStore();
        _loadedSaveName = saveName;
        return true;
    }

    private Dictionary<long, FarmhandOwnerRecord> ReadStore()
    {
        var path = StorePath;
        try
        {
            return ReadStoreFile(path);
        }
        catch (Exception ex)
        {
            // Unreadable store degrades to legacy-stamp behavior instead of blocking the load.
            Monitor.Log(
                $"[Ownership] Could not read {path}: {ex.Message}. Starting with an empty map; "
                    + "farmhands fall back to their legacy stamps.",
                LogLevel.Warn
            );
            return new Dictionary<long, FarmhandOwnerRecord>();
        }
    }

    private static Dictionary<long, FarmhandOwnerRecord> ReadStoreFile(string path)
    {
        if (path == null || !File.Exists(path))
        {
            return new Dictionary<long, FarmhandOwnerRecord>();
        }

        var file = JsonSerializer.Deserialize<FarmhandOwnershipFile>(File.ReadAllText(path));
        return file?.Farmhands ?? new Dictionary<long, FarmhandOwnerRecord>();
    }

    /// <summary>Reads an arbitrary save folder's ownership store without touching the live
    /// in-memory map (save-import tooling inspects the imported save's records). Empty on
    /// absence or corruption.</summary>
    public static Dictionary<long, FarmhandOwnerRecord> PeekStore(string saveFolderPath)
    {
        try
        {
            return ReadStoreFile(Path.Combine(saveFolderPath, StoreFileName));
        }
        catch
        {
            return new Dictionary<long, FarmhandOwnerRecord>();
        }
    }

    private void Write()
    {
        var path = StorePath;
        if (path == null)
        {
            return;
        }

        try
        {
            // Temp + move so a crash mid-write can't corrupt the store. The save folder may not
            // exist yet on a fresh world (created at the first day-save) — create it; the game
            // tolerates pre-existing folders.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(new FarmhandOwnershipFile { Farmhands = _records })
            );
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Monitor.Log(
                $"[Ownership] Could not write {path}: {ex.Message}. The change is live in memory "
                    + "but not persisted.",
                LogLevel.Warn
            );
        }
    }

    /// <summary>Looks up the current save's owner record for a farmhand. False for released
    /// slots (a released marker is not an owner).</summary>
    public bool TryGetOwner(long farmhandUid, out FarmhandOwnerRecord owner)
    {
        owner = null;
        if (!EnsureLoaded() || !_records.TryGetValue(farmhandUid, out owner) || owner == null)
        {
            owner = null;
            return false;
        }

        if (owner.Platform == PlatformReleased)
        {
            owner = null;
            return false;
        }

        return true;
    }

    /// <summary>Whether the slot is operator-released: claimable by any transport, first
    /// successful claim becomes the owner (the recorder overwrites the marker; an identity-less
    /// LAN claim clears it, returning the slot to the LAN pool).</summary>
    public bool IsReleased(long farmhandUid)
    {
        return EnsureLoaded()
            && _records.TryGetValue(farmhandUid, out var record)
            && record?.Platform == PlatformReleased;
    }

    /// <summary>Marks a farmhand released and persists the store.</summary>
    public void MarkReleased(long farmhandUid)
    {
        if (!EnsureLoaded())
        {
            return;
        }

        _records[farmhandUid] = new FarmhandOwnerRecord
        {
            Platform = PlatformReleased,
            Id = "",
            Origin = OriginOperator,
        };
        Write();
        Monitor.Log($"[Ownership] Marked farmhand {farmhandUid} released", LogLevel.Debug);
    }

    /// <summary>Records (or overwrites) a farmhand's owner and persists the store. A record
    /// matching the existing platform+id is skipped — which both saves the disk write on the
    /// common owner-rejoin case and preserves an operator-origin record against downgrade to
    /// claim origin when the pre-assigned owner first connects.</summary>
    public void RecordOwner(
        long farmhandUid,
        string platform,
        string id,
        string origin = OriginClaim
    )
    {
        if (!EnsureLoaded())
        {
            return;
        }

        if (
            _records.TryGetValue(farmhandUid, out var existing)
            && existing != null
            && existing.Platform == platform
            && existing.Id == id
        )
        {
            // Same identity: no rebind needed, but an operator origin must still win over a
            // claim origin (it exempts the record from the abandoned-claim sweep — the
            // durability a same-identity rebind is asking for). The reverse downgrade is
            // never applied: an owner rejoin must not weaken a pre-assignment.
            if (origin == OriginOperator && existing.Origin != OriginOperator)
            {
                existing.Origin = OriginOperator;
                Write();
                Monitor.Log(
                    $"[Ownership] Upgraded farmhand {farmhandUid} record to operator origin",
                    LogLevel.Debug
                );
            }

            return;
        }

        _records[farmhandUid] = new FarmhandOwnerRecord
        {
            Platform = platform,
            Id = id,
            Origin = origin,
        };
        Write();
        Monitor.Log(
            $"[Ownership] Recorded farmhand {farmhandUid} owner: platform={platform} origin={origin}",
            LogLevel.Debug
        );
    }

    /// <summary>Removes a farmhand's record — owner or released marker — and persists.
    /// Returns true if removed.</summary>
    public bool RemoveOwner(long farmhandUid)
    {
        if (!EnsureLoaded() || !_records.Remove(farmhandUid))
        {
            return false;
        }

        Write();
        Monitor.Log($"[Ownership] Removed record for farmhand {farmhandUid}", LogLevel.Debug);
        return true;
    }

    /// <summary>
    /// Load-time refresh + self-heal: re-read the store from disk (the folder may have been
    /// replaced by an import between sessions), then drop records whose farmhand no longer
    /// exists in <c>farmhandData</c> (deleted between sessions, or removed by a sweep this
    /// process didn't observe).
    /// </summary>
    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        _loadedSaveName = null;
        var farmhandData = Game1.netWorldState?.Value?.farmhandData;
        if (!EnsureLoaded() || farmhandData == null)
        {
            return;
        }

        var orphans = _records.Keys.Where(uid => !farmhandData.ContainsKey(uid)).ToList();
        if (orphans.Count == 0)
        {
            return;
        }

        foreach (var uid in orphans)
        {
            _records.Remove(uid);
        }

        Write();
        Monitor.Log(
            $"[Ownership] Dropped {orphans.Count} record(s) for farmhands no longer in the save: "
                + string.Join(", ", orphans),
            LogLevel.Debug
        );
    }

    // ── Platform-id helpers (single home for bind-id validation/classification) ─────

    // Steam64 individual-account range: start + full 32-bit account-id space.
    private const ulong Steam64Min = 76561197960265728UL;
    private const ulong Steam64Max = 76561202255233023UL;

    /// <summary>A platform id (Steam64 or GOG Galaxy uint64) is a non-empty decimal that fits
    /// in ulong. Invariant, no sign/whitespace — NOT <c>All(char.IsDigit)</c>, which accepts
    /// ulong-overflowing strings and non-ASCII Unicode digits.</summary>
    public static bool IsValidPlatformId(string id) =>
        ulong.TryParse(
            id,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out _
        );

    /// <summary>Classifies an operator-supplied bind id into a platform tag: ids in the
    /// Steam64 individual-account range are Steam, everything else is Galaxy. Same strict
    /// parse as <see cref="IsValidPlatformId"/>.</summary>
    public static string ClassifyPlatformId(string id)
    {
        return
            ulong.TryParse(
                id,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value
            ) && value is >= Steam64Min and <= Steam64Max
            ? ConnectionTransport.PlatformSteam
            : ConnectionTransport.PlatformGalaxy;
    }

    // ── Ownership matrix ────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ownership matrix — single home for both the join gate and the list-time visibility
    /// filter (<see cref="FarmhandSenderService"/>). Rows, in order:
    /// map-owned → allow iff the transport identity matches the record (LAN never matches);
    /// released → allow all transports (first claim wins; the recorder re-owns the slot);
    /// unmapped with stamp S≠"" → LAN reject; Steam: at join allow iff claimed C == S
    ///   (legacy-continuity bootstrap — a courtesy fence), at list time show (stamps are
    ///   Galaxy-space, SDR can't narrow; the vanilla client grays); Galaxy: at join pass
    ///   through (vanilla authCheck compares identity vs stamp, same id space), at list time
    ///   show only on exact match;
    /// unmapped, unstamped, customized (IP-created farmhand) → LAN only (protects identity-less
    ///   LAN players from platform takeover) until Tier 3;
    /// fresh → <see cref="ClaimVerdict.AllowFresh"/> (vanilla owns enableFarmhandCreation at
    ///   join; the filter applies it at list time).
    /// </summary>
    /// <param name="atJoin">True at the claim gate (a claimed userID is available and vanilla
    /// runs downstream); false at list time.</param>
    /// <param name="claimedUserId">The incoming root's userID at join time; null at list time.</param>
    public ClaimVerdict EvaluateClaim(
        Farmer stored,
        bool hasIdentity,
        TransportIdentity identity,
        bool atJoin,
        string claimedUserId
    )
    {
        var uid = stored.UniqueMultiplayerID;
        if (TryGetOwner(uid, out var owner))
        {
            return hasIdentity && owner.Platform == identity.Platform && owner.Id == identity.Id
                ? ClaimVerdict.Allow
                : ClaimVerdict.RejectOwnedByOther;
        }

        if (IsReleased(uid))
        {
            return ClaimVerdict.Allow;
        }

        var stamp = stored.userID.Value;
        if (!string.IsNullOrEmpty(stamp))
        {
            if (!hasIdentity)
            {
                return ClaimVerdict.RejectLegacyNoIdentity;
            }

            if (identity.Platform == ConnectionTransport.PlatformSteam)
            {
                return atJoin && (claimedUserId ?? "") != stamp
                    ? ClaimVerdict.RejectLegacyMismatch
                    : ClaimVerdict.Allow;
            }

            return !atJoin && stamp != identity.Id
                ? ClaimVerdict.RejectLegacyMismatch
                : ClaimVerdict.Allow;
        }

        if (stored.isCustomized.Value)
        {
            return hasIdentity ? ClaimVerdict.RejectLanPoolOnly : ClaimVerdict.Allow;
        }

        return ClaimVerdict.AllowFresh;
    }

    // ── Enforcement gate + recorder ─────────────────────────────────────────────────

    /// <summary>
    /// PREFIX on <c>GameServer.checkFarmhandRequest</c> (below the SafeLookup prefix).
    /// Reject-or-pass-through only — it never approves, so every vanilla guard still runs.
    ///
    /// Recorder: when the connection carries a transport identity, <c>approve</c> is wrapped so
    /// the ownership record is written at the exact approve moment; an identity-less (LAN)
    /// approve on a released slot clears the marker instead, returning the slot to the LAN
    /// pool. Detecting approval any later (e.g. postfix + <c>otherFarmers.ContainsKey</c>) is
    /// wrong: <c>otherFarmers</c> also contains the uid when vanilla rejects an "already in
    /// use" request, and postfixes run even after a prefix cancels — a rejected request would
    /// then overwrite the online farmhand's owner record with the requester's identity.
    ///
    /// The decision itself is <see cref="EvaluateClaim"/> — the one matrix shared with the
    /// visibility filter.
    /// </summary>
    public static bool CheckFarmhandRequest_OwnershipGate_Prefix(
        GameServer __instance,
        string userId,
        string connectionId,
        NetFarmerRoot farmer,
        Action<OutgoingMessage> sendMessage,
        ref Action approve,
        bool __runOriginal
    )
    {
        // Harmony runs ALL prefixes even when a higher-priority one already cancelled the
        // original — no-op when the SafeLookup prefix rejected.
        if (!__runOriginal)
        {
            return false;
        }

        if (_instance == null)
        {
            return true;
        }

        // Mirror SafeLookup's early-returns: vanilla/SafeLookup own these states.
        if (farmer?.Value == null || !__instance.isGameAvailable())
        {
            return true;
        }

        var uid = farmer.Value.UniqueMultiplayerID;
        if (
            !Game1.netWorldState.Value.farmhandData.TryGetValue(uid, out var stored)
            || stored == null
        )
        {
            return true;
        }

        var hasIdentity = ConnectionTransport.TryResolveIdentity(connectionId, out var identity);

        // Recorder — active even with enforcement off, so the map stays truthful.
        if (hasIdentity || _instance.IsReleased(uid))
        {
            var originalApprove = approve;
            var capturedIdentity = identity;
            approve = () =>
            {
                if (hasIdentity)
                {
                    _instance.RecordOwner(uid, capturedIdentity.Platform, capturedIdentity.Id);
                }
                else
                {
                    _instance.RemoveOwner(uid);
                }

                originalApprove();
            };
        }

        if (!_instance.EnforcementEnabled)
        {
            return true;
        }

        var verdict = _instance.EvaluateClaim(
            stored,
            hasIdentity,
            identity,
            atJoin: true,
            claimedUserId: farmer.Value.userID.Value
        );
        if (verdict is ClaimVerdict.Allow or ClaimVerdict.AllowFresh)
        {
            return true;
        }

        // Info, not Debug: a rejection is the operator's only clue when a player reports a
        // missing/locked farmhand (e.g. the same Steam account presenting a different identity
        // after switching doors — friends list / S-code use the Steam connection's Steam64,
        // the G-code uses the Galaxy id; not correlatable server-side, see steam-auth.md).
        var reason = verdict switch
        {
            ClaimVerdict.RejectOwnedByOther =>
                "owned by another platform identity — if this is the same player, run "
                    + $"'farmhand rebind {uid} <their platform id from the connect log>'",
            ClaimVerdict.RejectLegacyNoIdentity =>
                "legacy platform stamp present but connection carries no identity",
            ClaimVerdict.RejectLegacyMismatch => "legacy stamp continuity mismatch",
            _ => "IP-created farmhand (LAN pool only) — 'farmhand release "
                + $"{uid}' makes it claimable by any player",
        };
        _instance.Monitor.Log(
            $"[Ownership] Rejected farmhand {uid} for {ConnectionTransport.GetTransportName(connectionId)}: {reason}",
            LogLevel.Info
        );
        NetworkTweaker.RejectFarmhandRequestMethod.Invoke(
            __instance,
            new object[] { userId, connectionId, farmer, sendMessage }
        );
        return false;
    }

    /// <summary>
    /// POSTFIX on <c>Cabin.CanAssignTo</c>. Vanilla answers true whenever the cabin's
    /// current farmhand is merely uncustomized (<c>isUnclaimedFarmhand</c> sees neither
    /// stamps nor the ownership map), and every caller that gets a true proceeds to
    /// <c>Cabin.AssignFarmhand</c>, which permanently DELETES the displaced farmhand. That
    /// mostly fires during save load, when the game re-homes any farmhand whose cabin
    /// doesn't resolve. Narrow the answer: a cabin whose farmhand has an ownership record
    /// may only be assigned to that same farmhand, so operator pre-assignments and mid-join
    /// claims survive a reload. Released markers and swept claim records leave no record
    /// behind, so freed slots stay reusable.
    /// </summary>
    public static void CanAssignTo_ProtectOwnedSlot_Postfix(
        StardewValley.Locations.Cabin __instance,
        Farmer farmhand,
        ref bool __result
    )
    {
        if (!__result || _instance == null || farmhand == null)
        {
            return;
        }

        if (
            __instance.HasOwner
            && __instance.OwnerId != farmhand.UniqueMultiplayerID
            && _instance.TryGetOwner(__instance.OwnerId, out _)
        )
        {
            __result = false;
        }
    }
}
