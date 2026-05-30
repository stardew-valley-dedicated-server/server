# Filter `farmerDelta` payload for unauthenticated players

## Context

`mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:337-339`
currently whitelists `Multiplayer.farmerDelta` (message type 0) for unauthenticated
players with this comment:

```csharp
case Multiplayer.farmerDelta: // 0
    // ALLOW - this contains farmer creation data (name, appearance)
    return true;
```

The comment is wrong on two counts. `farmerDelta` is **not** scoped to creation data —
it carries arbitrary deltas to the player's `NetFarmerRoot`, including stat,
inventory, and mail mutations. And the filter does not constrain what subset of
fields the client may dirty inside the delta. An unauthenticated client with a
modified game DLL can send a `farmerDelta` that mutates any networked field on
their `Farmer` object before they enter the password.

## Verification against decompiled sources

### `farmerDelta` carries the full Farmer NetField tree

`decompiled/sdv-1.6.15-24356/StardewValley/Multiplayer.cs:1567-1576` — vanilla's
`processIncomingMessage` for type 0:

```csharp
case 0:
{
    long id = msg.Reader.ReadInt64();
    NetFarmerRoot netFarmerRoot = farmerRoot(id);
    if (netFarmerRoot != null)
    {
        readObjectDelta(msg.Reader, netFarmerRoot);
    }
    break;
}
```

`readObjectDelta` calls `root.Read(reader)` (`Multiplayer.cs:879-882`), which
walks `NetFields.Read` (`Netcode/NetFields.cs:131-153`). That implementation
reads a `BitArray` indicating which fields are dirty in this delta, then iterates
the fields and calls `Read` on each one whose bit is set. There is no server-side
authorization check — every `NetField` on the `Farmer` object is reachable from a
peer-supplied delta.

`Farmer.initNetFields` (`Farmer.cs:2087-2255`) calls `base.initNetFields()` first,
which adds `Character`'s fields (`Character.cs:540-569` — `position`,
`facingDirection`, `currentLocationRef`, `name`, `modData`, etc.), then appends
~120 more on the Farmer itself: stats (`experiencePoints`, `farmingLevel`,
`maxStamina`, `maxItems`, `netDeepestMineLevel`, `netQiGems`), inventory
(`netItems`, `boots`, `hat`, `leftRing`, `rightRing`, `trinketItems`),
progression (`mailReceived`, `eventsSeen`, `triggerActionsRun`, `friendshipData`,
`achievements`, `cookingRecipes`, `craftingRecipes`, `professions`),
house/cabin (`houseUpgradeLevel`, `daysUntilHouseUpgrade`, `homeLocation`),
and others.

`Money` itself is **not** in this set — it's stored in `Farmer._money` (private,
non-net) and replicated through `Farmer.team.money` on the team root (message
type 13/`teamDelta`, which is already blocked by our default-deny branch). The
audit's mention of "money" via `farmerDelta` is incorrect; the rest of the
audit's claim (position, inventory, stats) is correct.

### The delta is rebroadcast to every other peer

`decompiled/sdv-1.6.15-24356/StardewValley/Network/GameServer.cs:746-749` shows
that after `processIncomingMessage` runs, the server unconditionally calls
`rebroadcastClientMessage` for any type that `isClientBroadcastType` returns
true for. `Multiplayer.cs:216-239` lists type `0` as one of those — so an
unauth player's cheated delta also propagates to every other connected client,
not just the server's local farmer object.

If our Harmony prefix on `GameServer.processIncomingMessage` returns `false`,
**both** the local apply (`Game1.multiplayer.processIncomingMessage`) and the
rebroadcast (`rebroadcastClientMessage`) are skipped, because both live in the
same vanilla method (`GameServer.cs:694-750`). This is the only intercept point
that controls both.

### Persistence path

On disconnect, vanilla calls `saveFarmhand` (`Multiplayer.cs:984`) which clones
`otherFarmers.Roots[id]` into `farmhandData[id]`
(`NetWorldState.cs:748-755`). Any state the unauth player wrote during their
lobby phase is therefore **persistent** across reconnects. The mod's existing
`SaveFarmhand_Prefix` in `NetworkTweaker.cs:608-625` confirms the race window
exists by forcing `isCustomized=true` on save when name is set.

### `playerIntroduction` (type 2) path is also reachable

`decompiled/sdv-1.6.15-24356/StardewValley/Network/LidgrenServer.cs:269-283`
shows that the **first** type-2 message from a peer is interpreted as
`checkFarmhandRequest` and reads the full farmer state via
`Game1.multiplayer.readFarmer`. This sets `Game1.otherFarmers.Roots[id]` to a
client-supplied `NetFarmerRoot` (`Multiplayer.cs:905-916` `addPlayer`).

Subsequent type-2 messages from the same peer go through
`Game1.multiplayer.processIncomingMessage` (`Multiplayer.cs:1668-1670`
`receivePlayerIntroduction` → `addPlayer`), which **replaces** the entire root
in `otherFarmers.Roots`. The current whitelist allows type 2 from unauth
players. This is also exploitable by the same threat model. **In scope of M6
because it shares the root cause** (peer-supplied farmer state accepted unfiltered
during the unauth window) and because filtering only `farmerDelta` while
leaving `playerIntroduction` open leaves an obvious bypass.

### Why "snapshot then revert with `Set(oldValue)`" doesn't work directly

`netfield-revert-pattern.md` warns that `Set(oldValue)` from a `fieldChangeEvent`
is a no-op when interpolation is on. The same applies after a delta read: at
the point `setInterpolationTarget(newValue)` returns, `value` still equals
`oldValue` and `targetValue` equals `newValue`, so `Set(oldValue)` hits the
`if (newValue != value)` guard at `NetLong.cs:22` (and the equivalent in other
NetField subclasses) and is dropped. Server-side roots **are** interpolated —
`Multiplayer.cs:901` sets `InterpolationTicks = defaultInterpolationTicks` (15)
on every farmer root including peer-managed ones.

The viable path is to use `WriteFull` / `ReadFull` on each field. `ReadFull`
calls `ReadDelta` followed by `CancelInterpolation` (`NetFieldBase.cs:288-293`),
which forces `value = targetValue` after the read — bypassing the equality
guard cleanly. This works uniformly for `NetField<T>`, `NetCollection`,
`NetList`, `NetDictionary`, `NetHashSet`, `NetRef`, and event types because
every concrete `INetSerializable` implements both methods.

Caveat: `NetDictionary.ReadFull` (`NetDictionary.cs:880-898`) clears `dict`
then re-adds entries. It fires `addedEvent` for each restored key but does not
fire `removedEvent` for keys that were added by the malicious delta and then
overwritten on restore. For the Farmer's net dictionaries this is benign on
the server side (no rendering/UI subscribers); document but accept.

### Allowed-fields whitelist

The unauthenticated client legitimately needs to mutate three categories of
fields so the auth UX works:

1. **Identity completion** — required for `OnUpdateTicked` to detect that a new
   player has finished `CharacterCustomization` (line 514-516) and start the
   auth-timeout clock.
2. **Appearance** — for shared-lobby cosmetic fidelity (multiple unauth players
   in the same `Lobby_Shared` cabin should see each other's chosen hair/shirt).
3. **Movement within the lobby cabin** — `position`, `facingDirection`, sprite
   state.

Concrete whitelist (matches `Farmer.initNetFields` and `Character.initNetFields`):

```
// Identity
isCustomized, userID, name, farmName, favoriteThing, uniqueMultiplayerID,
platformType, platformID

// Appearance
farmerRenderer, netGender, bathingClothes, shirt, pants, hair, skin, shoes,
accessory, facialHair, hairstyleColor, pantsColor, newEyeColor, prismaticHair,
shirtItem, pantsItem

// Movement / sprite (Character base)
sprite, position.NetFields, facingDirection, netSpeed, netAddedSpeed, scale,
swimming, hidden
```

`currentLocationRef` is **not** whitelisted. The server's view of the unauth
player's location should remain whatever `addPlayer` set it to (the lobby
cabin, via the lobby-redirect logic in `FarmhandSenderService`). Letting the
client mutate it would let them claim to be in the farmhouse, mines, etc.

`modData` is not whitelisted — it can carry arbitrary client-defined keys; an
unauth player has no need for that surface.

`netItems` (inventory) is not whitelisted. An unauth player has no inventory
operations (movement and customization don't touch it). If a future feature
needs the lobby to display a held item, add the field then.

All other fields are reverted via the snapshot/restore mechanism.

## Plan

### Step 1 — Build the allowed-name set

In `PasswordProtectionService.cs`, add a `static readonly HashSet<string>`
containing the whitelisted suffixes (the part after `": "` in
`field.Name`, since `NetFields.AddField` prepends the collection name —
`NetFields.cs:103`). Match by suffix to be robust to the collection's display
name changing between SDV versions.

```csharp
private static readonly HashSet<string> AllowedFarmerFieldSuffixes = new()
{
    // Identity
    "isCustomized", "userID", "name", "farmName", "favoriteThing",
    "uniqueMultiplayerID", "platformType", "platformID",
    // Appearance
    "farmerRenderer", "netGender", "bathingClothes",
    "shirt", "pants", "hair", "skin", "shoes", "accessory", "facialHair",
    "hairstyleColor", "pantsColor", "newEyeColor", "prismaticHair",
    "shirtItem", "pantsItem",
    // Movement / sprite (Character base)
    "sprite", "position.NetFields", "facingDirection",
    "netSpeed", "netAddedSpeed", "scale", "swimming", "hidden",
};

private static bool IsAllowedFarmerField(string fullName)
{
    if (string.IsNullOrEmpty(fullName)) return false;
    var idx = fullName.IndexOf(": ", StringComparison.Ordinal);
    var suffix = idx >= 0 ? fullName.Substring(idx + 2) : fullName;
    return AllowedFarmerFieldSuffixes.Contains(suffix);
}
```

### Step 2 — Replace the broad `farmerDelta` whitelist with snapshot/filter/restore

In `ShouldProcessMessage` (the `case Multiplayer.farmerDelta` branch around
line 337), replace the unconditional `return true` with a call to
`ApplyFilteredFarmerDelta(message)` that returns `false` (so the vanilla path
plus rebroadcast are both suppressed; we apply our filtered version
in-method).

```csharp
case Multiplayer.farmerDelta: // 0
    return ApplyFilteredFarmerDelta(message);
```

```csharp
/// <summary>
/// Applies an unauthenticated player's farmerDelta with snapshot/restore
/// filtering. Returns false to suppress vanilla processing AND rebroadcast
/// (GameServer.processIncomingMessage handles both — see GameServer.cs:746-749).
/// </summary>
private bool ApplyFilteredFarmerDelta(IncomingMessage message)
{
    var startPos = message.Reader.BaseStream.Position;
    long farmerId;
    try { farmerId = message.Reader.ReadInt64(); }
    catch
    {
        // Malformed delta; drop silently.
        message.Reader.BaseStream.Position = startPos;
        return false;
    }

    if (farmerId != message.FarmerID)
    {
        // Cross-farmer delta from an unauth peer: not legitimate.
        _monitor.Log(
            $"[Auth] Dropping farmerDelta from {message.FarmerID} targeting {farmerId}",
            LogLevel.Warn);
        return false;
    }

    var farmerRoot = Game1.multiplayer.farmerRoot(farmerId);
    if (farmerRoot?.Value == null)
    {
        return false;
    }

    var farmer = farmerRoot.Value;
    var version = farmerRoot.Clock.netVersion;

    // Snapshot every non-allowed field's full state.
    var snapshots = new List<(INetSerializable field, byte[] bytes)>();
    foreach (var field in farmer.NetFields.GetFields())
    {
        if (IsAllowedFarmerField(field.Name)) continue;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        field.WriteFull(bw);
        snapshots.Add((field, ms.ToArray()));
    }

    // Apply the delta normally, mutating the live root.
    try
    {
        Game1.multiplayer.readObjectDelta(message.Reader, farmerRoot);
    }
    catch (Exception ex)
    {
        // Malformed payload from an unauth peer — restore snapshots and drop.
        _monitor.Log(
            $"[Auth] Malformed farmerDelta from {message.FarmerID}: {ex.Message}",
            LogLevel.Warn);
        RestoreSnapshots(snapshots, version);
        return false;
    }

    RestoreSnapshots(snapshots, version);
    return false;
}

private static void RestoreSnapshots(
    List<(INetSerializable field, byte[] bytes)> snapshots,
    NetVersion version)
{
    foreach (var (field, bytes) in snapshots)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        field.ReadFull(br, version);
    }
}
```

The `(farmerId != message.FarmerID)` guard prevents an unauth peer from
crafting a delta with another player's ID in the payload. Vanilla doesn't
check this; we should.

### Step 3 — Block `playerIntroduction` (type 2) from unauth players post-connect

The current whitelist allows type 2 unconditionally. Per `addPlayer`
(`Multiplayer.cs:905-916`), a subsequent type-2 from an already-connected peer
**replaces** their entire `NetFarmerRoot` in `otherFarmers.Roots` with the
client-supplied root — full bypass of any state we kept in place.

The first type-2 a connection sends does NOT route through
`processIncomingMessage` (it goes directly to `checkFarmhandRequest` per
`LidgrenServer.cs:273-283` because `peers` doesn't yet contain the farmer
ID). So blocking type 2 in our prefix only affects subsequent
re-introductions, which legitimate clients do not send.

Change:

```csharp
case Multiplayer.playerIntroduction: // 2
    // BLOCK - the first type-2 went through checkFarmhandRequest before
    // we get here. Subsequent type-2 from the same peer would replace
    // their entire NetFarmerRoot via addPlayer (Multiplayer.cs:905-916),
    // bypassing the farmerDelta filter.
    return false;
```

Verify this doesn't break the normal join flow: trace
`LidgrenServer.cs:269-283`. The condition for routing to `processIncomingMessage`
is `peers.ContainsLeft(message.FarmerID) && peers[message.FarmerID] == peer`.
That's only true after the connection has been approved (which happens in the
`approve` callback at `LidgrenServer.cs:281`, called from
`GameServer.checkFarmhandRequest:575`). Hence type-2 in
`processIncomingMessage` is always a re-introduction, not the initial
handshake. Blocking it is safe.

### Step 4 — Tests

Test plan (E2E, in `tests/JunimoServer.Tests/`):

1. **`UnauthFarmerDelta_StatChange_DoesNotPersist`** — connect a farmhand with
   a modified-client payload that sets `experiencePoints` for skill 0 to a
   high value via `farmerDelta`. Disconnect without authenticating. Reconnect
   with the host's API and inspect `farmhandData[id].Value.experiencePoints` —
   assert unchanged.
2. **`UnauthFarmerDelta_NameAndIsCustomized_StillReplicate`** — connect a new
   farmhand, complete CharacterCustomization (which dirties `name`, `shirt`,
   etc., plus `isCustomized`), authenticate. Assert
   `Game1.otherFarmers[id].Name` is the chosen name and `isCustomized` is
   `true` server-side.
3. **`UnauthFarmerDelta_InventoryInjection_Reverts`** — pre-stage an unauth
   client whose `netItems` payload includes a stack of Iridium Bars in slot 0. Send the delta. Inspect `farmer.Items[0]` server-side after the call —
   assert it's `null` (or whatever the saved-state default is). Test must NOT
   actually place the item server-side.
4. **`UnauthPlayerIntroduction_RepeatedSend_Blocked`** — after a successful
   join, send a second type-2 message with a freshly-constructed
   `NetFarmerRoot` carrying boosted stats. Assert `Game1.otherFarmers.Roots[id]`
   is unchanged.
5. **`AuthenticatedFarmerDelta_PassesThrough`** — after `!login`, send a
   normal stat-changing delta (e.g., `experiencePoints[0]++` from gameplay).
   Assert it applies. (Sanity check that we haven't broken authenticated
   players.)

Place under `tests/JunimoServer.Tests/Auth/PasswordProtectionTests.cs` if
that file exists, or create a sibling. Use the existing client-mod hook
points for crafting the malicious payload — `tests/test-client/` already has
infrastructure for sending raw multiplayer messages.

### Step 5 — Out-of-scope discoveries

While auditing M6, two adjacent issues surfaced that share the threat model
but should be tracked separately rather than absorbed into this fix:

- **`checkFarmhandRequest` accepts client-supplied initial state** with
  `Game1.multiplayer.addPlayer(farmer)` at `GameServer.cs:577` — the very
  first packet from a connecting client carries the entire `NetFarmerRoot`
  via `LidgrenServer.cs:275`'s `readFarmer`, and that state goes straight
  into `otherFarmers.Roots`. Filtering this requires a different patch site
  (a `checkFarmhandRequest` prefix) and overlaps with the existing
  `EnsureRealCabinAssignment` cleanup at the postfix. Track in a follow-up.
- **Lidgren channels & batching** — multiple `farmerDelta`s can be batched
  into one Lidgren data message (`LidgrenServer.cs:261-283`). The filter
  applies per-message and is correct in a batch context, but worth noting
  that an attacker can send many deltas per second; consider per-peer rate
  limiting on the unauth path. Track in a follow-up.

Both are listed because hiding them inside M6 would make the fix's scope
opaque and risk regressions; surfacing them per
`adversarial-review-split-findings.md` keeps the M6 change tight.

## Verification (post-conditions to run before declaring done)

These are runtime checks per `runtime-post-conditions-are-gates.md` — do not
declare M6 done from static review only.

1. **Cheating attempt fails** (test 1, 3 above): build the mod, run the
   `UnauthFarmerDelta_StatChange_DoesNotPersist` and inventory-injection
   tests. Both must pass.
2. **Customization still works** (test 2): the new-player customization
   replication test must pass — verifies `isCustomized=true` and chosen
   appearance arrive on the server through the whitelist.
3. **No regression for authenticated players** (test 5): authenticated
   farmerDelta application unchanged.
4. **Manual smoke test in shared lobby**: two unauth clients in `Lobby_Shared`
   complete customization with different appearances; assert each sees the
   other's chosen hair/shirt (rebroadcast still works for whitelisted
   fields). The rebroadcast carries the original delta bytes including
   non-whitelisted fields, but those non-whitelisted fields will be reverted
   on every receiver's next inbound delta from the server only if the server
   sends one — note that the **other unauth peer also sees the cheated
   non-whitelisted fields** because rebroadcast is unfiltered. This is
   acceptable: the server's persisted state is clean (the only source of
   truth at save time), and the visual lie is bounded to the unauth window.
   Document in a code comment near the filter.

## Risks and trade-offs

- **Snapshot cost**: `WriteFull` on every non-whitelisted field per inbound
  delta. Farmer has ~120 net fields, of which ~25 are whitelisted, leaving
  ~95 to snapshot. Empty `NetList`/`NetDictionary` write only a count, so
  this is cheap; large mail/event collections write all entries. Estimated
  worst case: a few KB per delta, allocated in `MemoryStream` buffers. With
  `farmerDeltaBroadcastPeriod = 1` (NetworkTweaker default) and 60 TPS, that's
  60 × ~few-KB/sec per unauthenticated player — measured worst-case impact
  bounded by `AuthTimeoutSeconds` (default 60-120s). Acceptable; auth is
  not a hot path.
- **Field whitelist drift**: the suffix list will need maintenance if SDV
  adds farmer fields in future patches. New fields default to "blocked",
  which is the secure default — a missed update means slightly degraded
  shared-lobby cosmetics, not a security regression. Document the list's
  authority (Farmer.initNetFields + Character.initNetFields) in a code
  comment so the next maintainer knows where to re-sync.
- **`NetDictionary` event asymmetry on restore**: `ReadFull` does not fire
  `removedEvent` for keys an attacker added to dictionaries before restore
  re-adds the original keys. Farmer dictionaries (`friendshipData`,
  `triggerActionsRun`, etc.) have no server-side `OnValueRemoved`
  subscribers in this codebase; verified by grep before merging.
- **Returning-player state reset risk**: a returning (already-customized)
  farmhand's `homeLocation`, `lastSleepLocation`, `disconnectLocation` etc.
  are in the snapshot/revert set. Their saved values (from `farmhandData`)
  are present in `otherFarmers.Roots[id]` from `addPlayer` time, so the
  snapshot captures the saved values — restore is a no-op for them in the
  common case. Cheating attempts are reverted to the saved value, which is
  the correct behavior.

## Files touched

- `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs`
  — replace the type-0 and type-2 whitelist branches; add the field-name
  helper; add the snapshot/restore method.
- `tests/JunimoServer.Tests/Auth/PasswordProtectionTests.cs` (or sibling)
  — five new E2E tests per Step 4.
- No changes to `tests/test-client/` are expected (existing raw-message
  infrastructure suffices); confirm during implementation.

## Adversarial self-review

Per `plan-discipline.md` and `adversarial-review-split-findings.md`,
splitting findings:

**Inherent non-issues** (provably safe):

- Money via `farmerDelta` is impossible because `Money` is non-net (`_money`
  is a private int) and `team.money` is on `teamRoot` (type 13), already
  blocked by the default-deny branch. Audit text mentions money but the
  code path doesn't exist.
- The new-player customization race documented in `NetworkTweaker.cs:608-625`
  is unaffected: `name` and `isCustomized` are both whitelisted, so the
  existing prefix's "fix `isCustomized` when name is set" still applies.
- The lobby redirect (`FarmhandSenderService` lobby cabin in `homeLocation`
  at connect-time) survives because `homeLocation` is in the revert set and
  the snapshot captures the lobby-redirect value at `addPlayer`-time.

**Out-of-scope follow-ups** (named in Step 5, not deferred for cover):

- `checkFarmhandRequest` accepting client-supplied initial state — different
  patch site, different threat surface (initial connection vs steady-state
  delta). Listed because hiding it inside M6 would mask the bypass, not
  because the cost of fixing now is small. Estimated cost: ~30-60 LOC in a
  new prefix, plus tests; not adjacent enough to fold in.
- Per-peer rate limit on unauth-path deltas — no current incident, listed as
  a hardening recommendation.

**Compatibility verification** (per `plan-discipline.md` for shared-infra
refactors):

- LAN vs Steam transports: both go through `GameServer.processIncomingMessage`
  (`LidgrenServer.cs:271`, `SteamNetServer.cs:434+`), same prefix site, same
  semantics. No transport-specific divergence.
- Lobby/unauthenticated players: this is the entire scope; covered by tests
  1-3.
- Test TPS (`SERVER_TPS=15`): no timing-sensitive logic in the filter
  (snapshot/apply/restore is synchronous, no timers).
- FPS caps: irrelevant — message handling runs on the network thread, not
  the render path.
- Disconnect mid-operation: snapshot/restore is local to one call; no state
  carries across disconnect.
- Authenticated players: explicit early-return at the top of
  `ShouldProcessMessage` (line 331-332) keeps them on the existing
  `return true` fast path. Test 5 verifies.
- Other `processIncomingMessage` subscribers: only one — our prefix.
  `SecurityService` (referenced in `SendServerIntroduction_SendLobbyLocation_Prefix`'s
  comment) patches `sendServerIntroduction`, not `processIncomingMessage`.
  No conflict.
