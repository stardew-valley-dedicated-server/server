using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace JunimoServer.Services.SaveImport;

/// <summary>
/// Pure, game-free <see cref="XmlDocument"/> logic for the save-import "swap host" transform
/// (Layer A). Isolates the entire brittle-XML surface in one file. Runs pre-load at the title
/// screen, so it must NOT touch <c>Game1</c> or any live engine state — it operates only on the
/// save's main XML document.
///
/// What it does, on a save's <c>&lt;SaveGame&gt;</c> root:
/// 1. Installs a fresh blank "Server" master by deep-cloning the imported save's own
///    <c>&lt;player&gt;</c> node (a known-good Farmer serialization for THIS exact game version —
///    no embedded template to drift) and blanking it per the three-bucket policy below.
/// 2. Reparents the original <c>&lt;player&gt;</c> into <c>&lt;farmhands&gt;</c> (a node move —
///    <c>&lt;player&gt;</c> and each <c>&lt;farmhands&gt;/&lt;Farmer&gt;</c> share the identical
///    <c>Farmer</c> schema), stamping it customized + the supplied platform userID.
///
/// The clone-blank policy (clear-personal-progress-only, keep-every-world/relationship-relevant
/// field) is derived directly from the serialized members of
/// <c>decompiled/.../StardewValley/Farmer.cs</c> (and inherited <c>Character.cs</c>). Re-derive on
/// any SDV bump. Three buckets:
///   (A) CLEAR  — genuinely personal progress that must not leak onto the host.
///   (B) KEEP   — fields the master gates world geometry on; zeroing them reverts the imported
///                world. Because the master IS a clone of the original player, these are already
///                present and identical — "copy" = "do not clear".
///   (C) KEEP   — fields inert on a never-playing headless bot; harmless to carry.
/// An unknown field defaults to bucket C (kept), not A (cleared): an over-clear reverts world
/// state or breaks a master-gated system, whereas an over-carry only adds inert data to an
/// invisible bot. The ORIGINAL player is preserved verbatim in <c>&lt;farmhands&gt;</c>, so any
/// clear here only ever affects the (intentionally blank) host, never the player's data.
/// </summary>
internal static class SaveImportXmlTransform
{
    // Local cosmetic pet default for the clone-blank host. NOT shared with ServerFarmerIdentity:
    // the new-game path's whichPetType is config-conditional (Cat/Dog from PetBreed), so it can't
    // be a shared constant. The host never plays, so any value is harmless.
    private const string ServerPetType = "Cat";

    // ── Bucket A: simple scalar NetInt/NetBool/NetString fields zeroed to a fixed value. ──
    // These serialize as plain element text (<farmingLevel>5</farmingLevel>), so setting the text
    // is safe. houseUpgradeLevel is cleared on the BLANK MASTER only (it drives the cabin's
    // upgradeLevel via owner.HouseUpgradeLevel; the moved owner keeps its real level).
    private static readonly (string Element, string Value)[] ScalarClears =
    {
        ("farmingLevel", "0"),
        ("miningLevel", "0"),
        ("combatLevel", "0"),
        ("foragingLevel", "0"),
        ("fishingLevel", "0"),
        ("luckLevel", "0"),
        ("maxStamina", "270"), // NetInt default (Farmer.cs:599)
        ("maxItems", "12"), // NetInt default (Farmer.cs:602)
        ("houseUpgradeLevel", "0"),
        ("clubCoins", "0"),
    };

    // ── Bucket A: relationship / marriage state cleared on the BLANK MASTER only. ──
    // The clone copies <spouse> by default, and getSpouse() resolves by NAME with no per-farmer
    // ownership check (Farmer.cs:4779) — a non-cleared <spouse> leaves both the blank master and
    // the demoted owner married to the same NPC (a duplicate marriage). The moved owner keeps its
    // own <spouse>. These are simple NetString/NetBool, so removing the element is the clean clear.
    private static readonly string[] RelationshipClears =
    {
        "spouse",
        "divorceTonight",
        "changeWalletTypeTonight",
    };

    // ── Bucket A: collection / accumulator / delta progress, removed wholesale. ──
    // Removing the element makes it deserialize to the field's empty/default initializer. (Simple
    // scalars are handled by ScalarClears; these are collections, dictionaries, NetArrays, or
    // NetIntDelta whose serialized shape isn't a plain text value, so "remove" is the safe clear.)
    private static readonly string[] CollectionClears =
    {
        "items", // inventory (NetArray<Item>)
        "experiencePoints", // NetArray<int>
        "newLevels", // NetList<Point> — pending level-up popups; clear with the zeroed skill levels
        "questLog",
        "professions",
        "dialogueQuestionsAnswered",
        "triggerActionsRun",
        "secretNotesSeen",
        "songsHeard",
        "achievements",
        "locationsVisited",
        "cookingRecipes",
        "craftingRecipes",
        "recipesCooked",
        "basicShipped",
        "mineralsFound",
        "fishCaught",
        "archaeologyFound",
        "callsReceived",
        "tailoredItems",
        "trinketItem", // NetRef<Trinket> (XmlElement "trinketItem" → trinketItems)
        "chestConsumedLevels",
        "activeDialogueEvents",
        "previousActiveDialogueEvents",
        "qiGems", // NetIntDelta (element "qiGems" → netQiGems); remove → default { Minimum=0 } = 0
        "netDeepestMineLevel", // private NetInt; normally absent, removed defensively
        "netTimesReachedMineBottom",
    };

    // ── Bucket A: stale per-owner LOCATION fields reset on the blank master. ──
    // The host is reset to the FarmHouse (homeLocation, above). lastSleepLocation/lastSleepPoint are
    // read at load to place the master on wake (SaveGame.cs:1119-1129, where MasterPlayer IS
    // Game1.player here) — if left as the original owner's, the headless host wakes wherever the
    // owner last slept (a cabin, the island), not the FarmHouse. Remove both (lastSleepLocation
    // → null NetString, so SaveGame's `!= null` guard skips it → default FarmHouse placement;
    // lastSleepPoint is a NetPoint <X>/<Y> structure that must be removed, not text-set).
    private static readonly string[] LocationElementClears =
    {
        "lastSleepLocation",
        "lastSleepPoint",
    };

    // (C) LEAVE-AS-CLONED — inert on a never-playing bot, documented so their omission from the
    // clear-lists is intentional and the schema enumeration stays honest:
    //   JOTPKProgress, toolBeingUpgraded/daysLeftForToolUpgrade, daysUntilHouseUpgrade,
    //   acceptedDailyQuest, lastSeenMovieWeek, horseName, emoteFavorites/performedEmotes,
    //   difficultyModifier.
    // (B) KEEP (master-gated world-state, already an identical copy on the clone — do NOT clear):
    //   mailReceived, eventsSeen, mailForTomorrow, mailbox, caveChoice, friendshipData, <stats>.
    //   money/totalMoneyEarned are serialized but left untouched: under the default SHARED wallet
    //   their accessors proxy to the team, so they're farm-state the player resumes on. (Edge: with
    //   useSeparateWallets the blank bot seeds a phantom personal wallet from the owner's balance —
    //   harmless, the bot never spends; noted so no one asserts the master's money is zero.)

    /// <summary>
    /// Runs the full swap transform on a loaded save document, in place. The document must be the
    /// save's main XML (a <c>&lt;SaveGame&gt;</c> root with a <c>&lt;player&gt;</c> child). Throws
    /// <see cref="SaveImportException"/> on any structural problem or guard violation; the caller
    /// must discard the document (write nothing) on throw.
    /// </summary>
    /// <param name="doc">The parsed save document (mutated in place).</param>
    /// <param name="userId">The platform userID to bind the demoted owner to (validated non-empty).</param>
    /// <param name="newMasterUid">Out: the fresh UniqueMultiplayerID generated for the blank master.</param>
    /// <param name="ownerUid">Out: the demoted owner's UniqueMultiplayerID (carried unchanged).</param>
    public static void ApplySwap(
        XmlDocument doc,
        string userId,
        out long newMasterUid,
        out long ownerUid
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new SaveImportException("Internal: ApplySwap called with an empty userId.");
        }

        var root = doc.DocumentElement;
        if (root == null || root.Name != "SaveGame")
        {
            throw new SaveImportException(
                "Save XML root is not <SaveGame>; the file is not a recognizable Stardew save."
            );
        }

        var player = SelectSingle(root, "player");
        if (player == null)
        {
            throw new SaveImportException("Save XML has no <player> element.");
        }

        // Re-import guard: refuse to re-transform a save whose <player> is already a Server master
        // produced by a prior import (name=Server + customized + clone-blank fingerprint). Detect by
        // the conjunction the import itself produces and a vanilla/played player cannot — NOT by
        // name+customized alone (both are user-controllable).
        if (IsAlreadyServerMaster(player))
        {
            throw new SaveImportAlreadySwappedException(
                "This save's <player> is already a host-swapped 'Server' master."
            );
        }

        var farmhandsContainer = GetOrCreateFarmhandsContainer(root, player);

        // userID-collision guard: the supplied bind id must not already match any existing
        // farmhand's <userID> (or the owner's own pre-existing one) — authCheck is raw string
        // equality with no uniqueness guarantee, so a collision would let the bound player land on
        // the wrong cabin and lock out the legitimate owner. Walk every farmhand userID first.
        var conflict = FindUserIdConflict(farmhandsContainer, player, userId);
        if (conflict != null)
        {
            throw new SaveImportException(
                $"The supplied user id collides with existing farmhand '{conflict}' in this save. "
                    + "Pick the correct id, or restore a backup if you re-imported a save the player is already in."
            );
        }

        ownerUid = ReadUid(player);

        // 1. Build the blank Server master by cloning the original <player> and blanking it.
        var blankMaster = (XmlElement)player.CloneNode(deep: true);
        newMasterUid = BlankInto(blankMaster, farmhandsContainer, player);

        // 2. Reparent the original <player> into <farmhands>, stamped customized + userID.
        SetScalarElement(player, "isCustomized", "true");
        SetScalarElement(player, "userID", userId);
        // Leave the owner's homeLocation as-is (FarmHouse for a former master). Layer B re-homes it;
        // do NOT create a cabin / touch farmhandReference here (brittle engine-generated names).

        root.RemoveChild(player);
        // The original <player> is an element named "player"; rename to "Farmer" so it is a valid
        // <farmhands> entry (each entry is a <Farmer>). XmlDocument can't rename in place, so
        // re-create the element as <Farmer> and move the children over.
        var farmhandEntry = RenameElement(doc, player, "Farmer");
        farmhandsContainer.AppendChild(farmhandEntry);

        // 3. Install the blank master as the new <player> (first child position is irrelevant to
        //    the deserializer, which selects by element name).
        root.PrependChild(blankMaster);

        ValidatePostTransform(root, newMasterUid, ownerUid, userId);
    }

    /// <summary>
    /// Blanks <paramref name="blankMaster"/> in place into the headless "Server" host. Returns the
    /// fresh collision-checked UniqueMultiplayerID written onto it.
    /// </summary>
    private static long BlankInto(
        XmlElement blankMaster,
        XmlElement farmhandsContainer,
        XmlElement originalPlayer
    )
    {
        // Identity (shared literals from ServerFarmerIdentity so import host == new-game host).
        SetScalarElement(blankMaster, "name", ServerFarmerIdentity.Name);
        SetScalarElement(blankMaster, "favoriteThing", ServerFarmerIdentity.FavoriteThing);
        SetScalarElement(
            blankMaster,
            "isCustomized",
            ServerFarmerIdentity.IsCustomized ? "true" : "false"
        );
        SetScalarElement(blankMaster, "homeLocation", "FarmHouse");
        SetScalarElement(blankMaster, "whichPetType", ServerPetType);
        // A master never carries a farmhand platform bind; the clone copies the original's userID
        // (normally empty), so clear it defensively.
        SetScalarElement(blankMaster, "userID", "");

        // There is no <displayName> element to set — displayName is [XmlIgnore]/runtime-derived
        // from <name> (Character.cs:290/143), recomputed as displayName = name at load.

        // Fresh, collision-checked UniqueMultiplayerID, absent from every other farmer in the save.
        var existingUids = CollectAllFarmerUids(farmhandsContainer, originalPlayer);
        var newUid = GenerateUniqueId(existingUids);
        SetScalarElement(blankMaster, "UniqueMultiplayerID", newUid.ToString());

        // (A) Clear personal progress + relationship + stale-location state. Bucket B/C ride along.
        foreach (var (element, value) in ScalarClears)
        {
            SetScalarElement(blankMaster, element, value);
        }
        foreach (var element in RelationshipClears)
        {
            RemoveChildElements(blankMaster, element);
        }
        foreach (var element in CollectionClears)
        {
            RemoveChildElements(blankMaster, element);
        }
        foreach (var element in LocationElementClears)
        {
            RemoveChildElements(blankMaster, element);
        }

        return newUid;
    }

    /// <summary>
    /// Validates the post-transform document on the <c>.tmp</c> before the atomic rename. Asserts
    /// only the bucket-A personal-progress containers empty + relationship state cleared on the
    /// blank master, plus structural integrity. Does NOT assert bucket-B (mail/events/caveChoice/
    /// friendshipData/stats) or team-proxied (money) empty — those are deliberately kept and would
    /// fail on every real save.
    /// </summary>
    public static void ValidatePostTransform(
        XmlElement root,
        long expectedMasterUid,
        long expectedOwnerUid,
        string expectedUserId
    )
    {
        var player = SelectSingle(root, "player");
        if (player == null)
        {
            throw new SaveImportException("Post-transform validation: no <player> element.");
        }

        // Exactly one <player> with the master identity.
        if (root.SelectNodes("player")?.Count != 1)
        {
            throw new SaveImportException(
                "Post-transform validation: expected exactly one <player>."
            );
        }
        if (ReadScalar(player, "name") != ServerFarmerIdentity.Name)
        {
            throw new SaveImportException(
                "Post-transform validation: master <name> is not 'Server'."
            );
        }
        if (ReadScalar(player, "isCustomized") != "true")
        {
            throw new SaveImportException("Post-transform validation: master is not customized.");
        }
        if (ReadUid(player) != expectedMasterUid)
        {
            throw new SaveImportException("Post-transform validation: master UID mismatch.");
        }

        // Bucket-A personal-progress containers blanked + relationship cleared on the master.
        AssertScalarZero(player, "farmingLevel");
        AssertScalarZero(player, "miningLevel");
        AssertScalarZero(player, "combatLevel");
        AssertScalarZero(player, "foragingLevel");
        AssertScalarZero(player, "fishingLevel");
        AssertAbsentOrEmpty(player, "items");
        AssertAbsentOrEmpty(player, "questLog");
        AssertAbsentOrEmpty(player, "cookingRecipes");
        AssertAbsentOrEmpty(player, "craftingRecipes");
        foreach (var rel in RelationshipClears)
        {
            if (SelectSingle(player, rel) != null)
            {
                throw new SaveImportException(
                    $"Post-transform validation: master still carries relationship field <{rel}>."
                );
            }
        }

        // Moved owner present in <farmhands> with isCustomized + stamped userID.
        var farmhands = SelectSingle(root, "farmhands");
        var ownerEntry = farmhands
            ?.SelectNodes("Farmer")
            ?.Cast<XmlElement>()
            .FirstOrDefault(f => ReadUid(f) == expectedOwnerUid);
        if (ownerEntry == null)
        {
            throw new SaveImportException(
                "Post-transform validation: demoted owner not found in <farmhands>."
            );
        }
        if (ReadScalar(ownerEntry, "isCustomized") != "true")
        {
            throw new SaveImportException(
                "Post-transform validation: demoted owner is not customized."
            );
        }
        if (ReadScalar(ownerEntry, "userID") != expectedUserId)
        {
            throw new SaveImportException(
                "Post-transform validation: demoted owner userID was not stamped."
            );
        }

        // The new master UID is absent from the FULL post-transform farmer-ID set (all farmhands +
        // the moved owner).
        var allOther = CollectAllFarmerUids(farmhands, ownerEntry);
        if (allOther.Contains(expectedMasterUid))
        {
            throw new SaveImportException(
                "Post-transform validation: new master UID collides with a farmhand/owner UID."
            );
        }
    }

    /// <summary>
    /// Builds a standalone <c>SaveGameInfo</c> document (a single <c>&lt;Farmer&gt;</c> root) from
    /// the post-transform master <c>&lt;player&gt;</c>, mirroring the engine's
    /// <c>SaveGame.serialize</c> SaveGameInfo write. Used so the list/info views and external
    /// tooling don't show the old human host after a swap. Returns null if the player can't be
    /// resolved (caller skips the regen — it is not boot-critical on this server's direct-load path).
    /// </summary>
    public static XmlDocument? BuildSaveGameInfo(XmlDocument transformedSave)
    {
        var player =
            transformedSave.DocumentElement == null
                ? null
                : SelectSingle(transformedSave.DocumentElement, "player");
        if (player == null)
        {
            return null;
        }

        var info = new XmlDocument();
        var farmerRoot = info.CreateElement("Farmer");
        info.AppendChild(farmerRoot);
        foreach (XmlNode child in player.ChildNodes)
        {
            farmerRoot.AppendChild(info.ImportNode(child, deep: true));
        }
        return info;
    }

    // ── Detection / collision helpers ──────────────────────────────────────────────

    /// <summary>
    /// True if <paramref name="player"/> is a Server master produced by a prior import. Requires the
    /// conjunction a vanilla played <c>&lt;player&gt;</c> cannot have: name="Server" +
    /// isCustomized=true AND the clone-blank fingerprint — fields the transform actually CLEARS:
    /// empty <c>items</c>, empty <c>questLog</c>, and no <c>spouse</c>. (NOT <c>friendshipData</c>:
    /// the engine seeds it on every farmer and the transform KEEPS it as a bucket-B master-gated
    /// field, so a real blank master also has it populated.) A real played "Server" farmer would
    /// have at least an inventory item.
    /// </summary>
    private static bool IsAlreadyServerMaster(XmlElement player)
    {
        if (ReadScalar(player, "name") != ServerFarmerIdentity.Name)
        {
            return false;
        }
        if (ReadScalar(player, "isCustomized") != "true")
        {
            return false;
        }
        return IsAbsentOrEmpty(player, "items")
            && IsAbsentOrEmpty(player, "questLog")
            && SelectSingle(player, "spouse") == null;
    }

    /// <summary>
    /// Returns the name of the first farmhand (or the owner) whose <c>&lt;userID&gt;</c> equals
    /// <paramref name="userId"/>, or null if none collides.
    /// </summary>
    private static string? FindUserIdConflict(
        XmlElement farmhandsContainer,
        XmlElement originalPlayer,
        string userId
    )
    {
        foreach (var farmer in EnumerateFarmers(farmhandsContainer, originalPlayer))
        {
            if (ReadScalar(farmer, "userID") == userId)
            {
                var name = ReadScalar(farmer, "name");
                return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
            }
        }
        return null;
    }

    private static HashSet<long> CollectAllFarmerUids(
        XmlElement? farmhandsContainer,
        XmlElement? owner
    )
    {
        var set = new HashSet<long>();
        if (farmhandsContainer != null)
        {
            foreach (XmlElement f in farmhandsContainer.SelectNodes("Farmer")!)
            {
                set.Add(ReadUid(f));
            }
        }
        if (owner != null)
        {
            set.Add(ReadUid(owner));
        }
        return set;
    }

    private static IEnumerable<XmlElement> EnumerateFarmers(
        XmlElement farmhandsContainer,
        XmlElement originalPlayer
    )
    {
        foreach (XmlElement f in farmhandsContainer.SelectNodes("Farmer")!)
        {
            yield return f;
        }
        yield return originalPlayer;
    }

    /// <summary>
    /// Generates an engine-style random 64-bit UniqueMultiplayerID (matching
    /// <c>Utility.RandomLong</c>'s full-long shape) not present in <paramref name="existing"/>.
    /// </summary>
    private static long GenerateUniqueId(HashSet<long> existing)
    {
        var rng = new Random();
        var bytes = new byte[8];
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            // Unbiased full 64-bit draw (matches engine Utility.RandomLong's NextBytes
            // shape; a two-int rng.Next draw excludes int.MaxValue). Avoid 0 (no-owner sentinel).
            rng.NextBytes(bytes);
            var candidate = BitConverter.ToInt64(bytes, 0);
            if (candidate != 0 && !existing.Contains(candidate))
            {
                return candidate;
            }
        }
        throw new SaveImportException(
            "Could not generate a collision-free UniqueMultiplayerID after 1000 attempts."
        );
    }

    // ── Low-level XML helpers (element-name selection; the save uses no XML namespace) ──

    private static XmlElement? SelectSingle(XmlElement parent, string name) =>
        parent.SelectSingleNode(name) as XmlElement;

    private static XmlElement GetOrCreateFarmhandsContainer(XmlElement root, XmlElement player)
    {
        var existing = SelectSingle(root, "farmhands");
        if (existing != null)
        {
            return existing;
        }
        // A single-player save has no <farmhands> element. Create an empty one (the deserializer
        // reads <farmhands>/<Farmer> entries; an empty/absent container yields no farmhands).
        var container = root.OwnerDocument!.CreateElement("farmhands");
        root.InsertAfter(container, player);
        return container;
    }

    /// <summary>
    /// Re-creates <paramref name="source"/> as a new element named <paramref name="newName"/> with
    /// all children moved over (XmlDocument can't rename an element in place).
    /// </summary>
    private static XmlElement RenameElement(XmlDocument doc, XmlElement source, string newName)
    {
        var renamed = doc.CreateElement(newName);
        foreach (XmlAttribute attr in source.Attributes)
        {
            renamed.SetAttribute(attr.Name, attr.Value);
        }
        while (source.FirstChild != null)
        {
            renamed.AppendChild(source.FirstChild); // moves the node (removes from source)
        }
        return renamed;
    }

    /// <summary>
    /// Sets the text of the single child element named <paramref name="name"/>, creating it (as the
    /// first child) if absent. For NetInt/NetBool/NetString fields whose serialized form is plain
    /// element text. If multiple same-named elements exist (shouldn't for these), the first wins and
    /// the rest are removed to keep the document unambiguous.
    /// </summary>
    private static void SetScalarElement(XmlElement parent, string name, string value)
    {
        var nodes = parent.SelectNodes(name)!.Cast<XmlElement>().ToList();
        XmlElement target;
        if (nodes.Count == 0)
        {
            target = parent.OwnerDocument!.CreateElement(name);
            parent.PrependChild(target);
        }
        else
        {
            target = nodes[0];
            for (var i = 1; i < nodes.Count; i++)
            {
                parent.RemoveChild(nodes[i]);
            }
        }
        target.InnerText = value;
    }

    private static void RemoveChildElements(XmlElement parent, string name)
    {
        foreach (var node in parent.SelectNodes(name)!.Cast<XmlElement>().ToList())
        {
            parent.RemoveChild(node);
        }
    }

    private static long ReadUid(XmlElement farmer)
    {
        var text = ReadScalar(farmer, "UniqueMultiplayerID");
        return long.TryParse(text, out var uid) ? uid : 0;
    }

    private static string ReadScalar(XmlElement parent, string name) =>
        SelectSingle(parent, name)?.InnerText ?? "";

    private static bool IsAbsentOrEmpty(XmlElement parent, string name)
    {
        var el = SelectSingle(parent, name);
        return el == null || !el.HasChildNodes || string.IsNullOrWhiteSpace(el.InnerXml);
    }

    private static void AssertAbsentOrEmpty(XmlElement parent, string name)
    {
        if (!IsAbsentOrEmpty(parent, name))
        {
            throw new SaveImportException(
                $"Post-transform validation: master <{name}> is not empty after clearing."
            );
        }
    }

    private static void AssertScalarZero(XmlElement parent, string name)
    {
        var text = ReadScalar(parent, name);
        if (text != "0" && !string.IsNullOrEmpty(text))
        {
            throw new SaveImportException(
                $"Post-transform validation: master <{name}> is '{text}', expected 0."
            );
        }
    }
}

/// <summary>Thrown when a save-import XML transform fails. Always surfaced to the operator as a
/// Warn (never Error — Error is server-side test poison); the caller writes nothing on throw.</summary>
internal class SaveImportException : Exception
{
    public SaveImportException(string message)
        : base(message) { }
}

/// <summary>Thrown by the re-import guard when the save's <c>&lt;player&gt;</c> is already a
/// host-swapped Server master. Distinguished from <see cref="SaveImportException"/> so the command
/// can route a same-id re-run to a true no-op vs. a different-id re-point.</summary>
internal sealed class SaveImportAlreadySwappedException : SaveImportException
{
    public SaveImportAlreadySwappedException(string message)
        : base(message) { }
}
