# Save Import Host Swap

## Context

When users import their local Stardew Valley saves into JunimoServer, their personal farmer occupies the host slot (Player 1). JunimoServer's `AlwaysOnServer` takes full control of the host farmer -- hiding it, automating sleep/festivals, and blocking human players from the Farmhouse. Users need their personal farmer moved to a farmhand slot so they can rejoin as a farmhand.

Currently `GameLoaderService` calls `SaveGame.Load(saveName)` with no preprocessing. This feature adds an automatic XML preprocessing step that detects non-server hosts and swaps them into a farmhand slot before load.

---

## Architecture

```
SaveImportService : ModService             <- orchestration (file I/O, logging)
  |- PrepareForDedicatedServer(saveName)
  '- depends on: IModHelper, IMonitor

HostSwapProcessor                          <- internal static, pure XML logic, zero game deps
  |- IsServerHost(XmlDocument) -> bool
  |- PerformSwap(XmlDocument, long newServerId) -> SwapResult
  '- depends on: System.Xml only
     (ID generation delegated to caller via parameter)

ServerFarmerIdentity                       <- shared constants (single source of truth)
  '- consumed by both GameCreatorService (new-game host setup)
                  and HostSwapProcessor (imported-host detection + clone overwrite)
```

- `SaveImportService` is a `ModService` -- auto-discovered by `ModEntry.LoadServices()` reflection, injected into `GameLoaderService` via DI constructor.
- `HostSwapProcessor` is a `static class` with `internal` visibility. Contains only `System.Xml` logic -- no game/SMAPI dependencies.
- `ServerFarmerIdentity` is `internal static` -- holds the server farmer's literal values (`Name = "Server"`, `DisplayName = "Server"`, `FavoriteThing = "Junimos"`, `WhichPetType = "Cat"`, plus `IsCustomized = true`). Extracted **once** from `mod/JunimoServer/Services/GameCreator/GameCreatorService.cs:113-120` (verified). After this plan lands, `GameCreatorService` reads from `ServerFarmerIdentity` instead of hard-coding the literals.

### Verification approach

Per `CLAUDE.md` ("Helpers are integration-tested, not unit-tested ... there is no unit-test layer"), this plan does **not** introduce a unit-test project. Instead:

- `HostSwapProcessor` stays `internal` for production-side use only.
- A focused E2E test in `tests/JunimoServer.Tests/` loads a fixture save, runs the importer, and asserts post-swap structure on disk (e.g. main save XML has `<player><name>Server</name>`, `<farmhands>` contains the original host, cabin `farmhandReference` updated when applicable).
- Fixture saves (`save-with-cabin.xml`, `save-no-cabin.xml`) live alongside the test under `tests/JunimoServer.Tests/Fixtures/SaveImport/`.

---

## Algorithm

Strict phase ordering: **Parse -> Gather -> Build -> Apply -> Write -> Log**

All work is done in-memory on the `XmlDocument` until the final write phase. If any phase fails, abort immediately without modifying files. Writes use temp-file-then-rename for atomicity.

### Phase 1: Parse & Detect (SaveImportService)

1. Build paths: `savesDir/{saveName}/{saveName}` (main save), `savesDir/{saveName}/SaveGameInfo`
2. Verify both files exist; abort with warning if not
3. Load main save XML into `XmlDocument`
4. Call `HostSwapProcessor.IsServerHost(doc)` -- case-insensitive check of `<player><name>` against "server"
5. If already server host -> log info, return early

### Phase 2: Gather & Validate (HostSwapProcessor.PerformSwap)

6. Extract `<player>` node (old host)
7. Read old host's `<UniqueMultiplayerID>` value
8. Find all cabins: XPath `//Building[buildingType='Cabin']`
9. For each cabin:
   - Read `indoors/farmhandReference/uid/long` -> get farmhand ID
   - Find matching `<Farmer>` in `/SaveGame/farmhands` by `<UniqueMultiplayerID>`
   - Check availability: `<isCustomized>false</isCustomized>` AND (`<userID>` empty or missing). The runtime `CabinManagerService.IsCabinAvailable` (`mod/JunimoServer/Services/CabinManager/CabinManagerService.cs:537-577`) also rejects when `owner.isActive()` is true or when an `excludePeer` matches; both are runtime-only signals and irrelevant to a static save XML on disk. We re-implement only the offline-static portion.
10. Record first available cabin's `<uniqueName>` and its `<Farmer>` node (or null if none)
11. Collect all existing `UniqueMultiplayerID` values across player + all farmhands
12. Generate new server farmer ID: `SaveImportService` calls `Utility.RandomLong()` (the game's own ID generator) and passes it to `HostSwapProcessor.PerformSwap(doc, newServerId)`. The processor validates no collision with existing IDs. If collision (astronomically unlikely), `SaveImportService` retries with a new ID. This keeps the processor pure/testable while using the actual game implementation at runtime.

### Phase 3: Build nodes in-memory (HostSwapProcessor)

13. **Server farmer**: Deep-clone the `<player>` node. Overwrite identity fields only (values from the shared `ServerFarmerIdentity` constants):
    - `<name>` -> `ServerFarmerIdentity.Name` ("Server")
    - `<displayName>` -> `ServerFarmerIdentity.DisplayName` ("Server")
    - `<favoriteThing>` -> `ServerFarmerIdentity.FavoriteThing` ("Junimos")
    - `<whichPetType>` -> `ServerFarmerIdentity.WhichPetType` ("Cat")
    - `<isCustomized>` -> "true"
    - `<homeLocation>` -> "FarmHouse"
    - `<UniqueMultiplayerID>` -> new generated ID
    - `<userID>` -> "" (empty)

    All other data (mail, events, house upgrades, skills, items, relationships) is inherited from the clone. This is intentional: the Server farmer is a hidden bot controlled by `AlwaysOnServer`. Leftover personal data is inert.

    **Caveat — verify on first integration run.** `GameCreatorService.cs:136` adds `eventsSeen.Add("60367")` during fresh new-game host setup. A cloned host already has its own `eventsSeen` history, so the bootstrap event may or may not be present depending on the source save. Cloning guarantees a complete `Farmer` XML for `XmlSerializer`, but the world-state implications of a populated `eventsSeen` on the Server farmer should be verified once with an E2E run before declaring safe.

14. **Old host as farmhand**: Modify the original `<player>` node in-place:
    - Clear `<userID>` -> "". Prevents auth mismatch across Galaxy/Steam ID spaces. Steam SDR passes `""` as userId to `checkFarmhandRequest` (`SteamGameServerNetServer.cs:268`), so `authCheck` (`GameServer.cs:501-504`) returns true regardless. Fresh `userID` binding happens on first connect.
    - **If unclaimed cabin found**: Set `<homeLocation>` to that cabin's `<uniqueName>`
    - **If no cabin**: Set `<homeLocation>` to "" (empty). See No-Cabin Flow below.
    - All personal data (inventory, skills, relationships, mail, events, achievements) preserved unchanged

15. **Cabin linkage** (only if unclaimed cabin was found):
    - Set `farmhandReference/uid/long` -> old host's `UniqueMultiplayerID`
    - Set `farmhandReference/defined/boolean` -> "true"

### Phase 4: Apply to XML document (HostSwapProcessor)

16. Replace `<player>` children with Server farmer clone's children (element stays `<player>`)
17. Create new `<Farmer>` element, copy old host's children into it (element name change: `<player>` content -> `<Farmer>` wrapper. Both represent the same `Farmer` type, just different XML element names per `XmlSerializer` conventions)
18. In `<farmhands>` list:
    - If used existing unclaimed farmhand: replace that `<Farmer>` with the new one
    - If no unclaimed farmhand: append new `<Farmer>` to `<farmhands>`

### Phase 5: Write (SaveImportService)

19. Write modified `XmlDocument` to a temp file (`{saveName}.tmp`), then `File.Move` (overwrite) to replace the original. If the write or move fails, the original is intact. Produces plain UTF-8 XML, compatible with `SaveGame.TryReadSaveFile` (`SaveGame.cs:605-647`).
20. Generate `SaveGameInfo`: Create new `XmlDocument` with `<Farmer>` root element, copy all children from the new `<player>` node. Write to `SaveGameInfo.tmp`, then move to `SaveGameInfo`. (The game's `SaveGameInfo` is a standalone `Farmer` serialization with `<Farmer>` root. Different root name from `<player>` in the main save, same content structure.)

### Phase 6: Log (SaveImportService)

21. `"Host swap: '{oldHostName}' moved to farmhand. Server farmer created as host."`
22. If no cabin: `"No cabin available for '{oldHostName}'. Will be auto-assigned on first connect."`

---

## No-Cabin Flow (Verified Safe)

When no unclaimed cabin exists, the old host enters `<farmhands>` with empty `homeLocation`. Connect-time flow verified through decompiled source:

1. **Save load** -> `CabinManagerService.OnSaveLoaded` -> `EnsureAtLeastXCabins()` invoked at `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs:132` (definition at `:480`).
2. **Client connect** -> `FarmhandSenderService.SendAvailableFarmhands_Prefix`:
   - `EnsureAtLeastXCabins(reservedIds.Count + 1)` (`mod/JunimoServer/Services/AuthService/FarmhandSenderService.cs:199`) guarantees a cabin exists.
   - `IsFarmhandAvailable(farmhand)` (`FarmhandSenderService.cs:209`) -> game's `GameServer.IsFarmhandAvailable` -> calls `TryAssignFarmhandHome` (`NetWorldState.cs:781`).
3. **Auto-assign** -> `TryAssignFarmhandHome`: empty `homeLocation` -> falls through to `ForEachBuilding` loop (line 798) -> finds empty cabin -> `CanAssignTo` returns true (owner is unclaimed, `Cabin.cs:84`) -> `AssignFarmhand` sets `homeLocation` + `farmhandReference` (`Cabin.cs:102-103`).
4. **Selection** -> Old host has `isCustomized=true` -> enters `claimedFarmers` list (line 230) -> shown in character selection.
5. **Auth** -> Steam SDR passes `""` as userId (`SteamGameServerNetServer.cs:268`) -> `authCheck("", farmhand)` -> userId is empty -> returns true (`GameServer.cs:504`).
6. **Lobby/password** -> `IsLobbyCabinFarmhand` lives in `mod/JunimoServer/Services/AuthService/FarmhandSenderService.cs:420`; `GetCabin()` returns null (no cabin yet) -> returns false -> not filtered. Password protection patches `checkFarmhandRequest` as a prefix at `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:84`. Captures spawn info, doesn't block.

---

## Data Handling

The Server farmer is a **clone** of the old host. All progression data (mail flags, events, house upgrades, skills, items) inherited automatically. No explicit transfer logic needed.

The old host (now farmhand) keeps all original data unchanged. Duplicates between host and farmhand are harmless:

- **Host-specific mail** (`ccDoorUnlock`, `ccPantry`, `ccCraftsRoom`, etc.): Checked against `Game1.player` (Server farmer) for world-state gates. Farmhand copies just prevent cutscene re-triggers.
- **Host-specific events** (`65` cave type, `611439` CC unlock, etc.): Same, checked on host for world decisions.
- **House upgrade level**: `FarmHouse.upgradeLevel` is an `[XmlIgnore]` property that reads from `owner.houseUpgradeLevel` (`FarmHouse.cs:106-118`). Since `Cabin extends FarmHouse` and does NOT override `upgradeLevel`, the cabin automatically reflects its owner's upgrade level. The old host's `houseUpgradeLevel` (e.g., 3 for fully upgraded) is preserved on their farmer data, so their cabin will automatically have the correct layout (kitchen, nursery, cellar access). **No CabinManager changes needed.**

---

## Edge Cases

| Case | Handling | Verified by |
|------|----------|-------------|
| Already server host | Case-insensitive name match -> skip, log info | `IsServerHost` unit test |
| No cabins | Empty `homeLocation`, auto-assigned on connect | No-cabin flow analysis + unit test |
| All cabins occupied | Same as no-cabin. `EnsureAtLeastXCabins` creates new one | Same flow |
| Multiple runs (idempotency) | Detection skips re-run (host already named "Server"). | `IsServerHost` check on already-swapped save |
| Player-to-player marriages | `UniqueMultiplayerID` preserved -> marriage refs intact | Structural analysis |
| Farmhouse contents | Belong to FarmHouse location, not farmer. Server farmer inherits. | Known trade-off, document |
| Cabin upgrade level | `houseUpgradeLevel` is on the Farmer, cabin reads it via `owner.houseUpgradeLevel`. Old host's cabin auto-upgrades. | Verified in `FarmHouse.cs:106-118`, `Cabin` does not override |
| Old host `userID` | Cleared. Fresh binding on connect. | Auth flow analysis |
| ID collision | `Utility.RandomLong()` + collision check against all existing IDs. Retry on collision. | Unit test (with injected ID) |
| `SaveGameInfo` | Regenerated with `<Farmer>` root from new `<player>` | SaveGame.cs analysis |
| Write failure | Temp-file-then-rename: original untouched if write fails | Atomic write pattern |
| XML namespaces | `XmlDocument` preserves `xsi:type` + NS when cloning | Built-in behavior |
| User literally named "Server" | Detected as server host -> skipped. Extremely unlikely. | Acceptable |
| Save file missing | Abort with warning log | Explicit check in Phase 1 |

---

## Files to Create

### `mod/JunimoServer/Services/SaveImport/SaveImportService.cs`
`ModService`. Auto-discovered via DI. Orchestrates file I/O, logging.
- Constructor: `(IModHelper helper, IMonitor monitor)`
- `PrepareForDedicatedServer(string saveName)`: called by `GameLoaderService.LoadSave()`

### `mod/JunimoServer/Services/SaveImport/HostSwapProcessor.cs`
`internal static class`. Pure XML logic, `System.Xml` only.
- `IsServerHost(XmlDocument doc)` -> `bool`
- `PerformSwap(XmlDocument doc, long newServerId)` -> `SwapResult`
- Private helpers: `FindUnclaimedCabinSlot`, `BuildServerFarmerFromClone`, `PrepareOldHostAsFarmhand`, `UpdateCabinLinkage`, `SetChildElementValue`, `GetChildElementValue`
- `SwapResult` record: `OldHostName` (string), `CabinAssigned` (bool), `NewServerId` (long)

### `mod/JunimoServer/ServerFarmerIdentity.cs` (or co-located with `GameCreatorService`)
`internal static class`. Single source of truth for the server farmer's identity literals. Consumed by both `GameCreatorService` (new-game host setup) and `HostSwapProcessor` (imported-host detection + clone overwrite). Replaces the inline literals at `GameCreatorService.cs:113-118`.

### `tests/JunimoServer.Tests/SaveImport/SaveImportE2ETests.cs` (E2E, no unit-test project)
Loads each fixture save via the importer (or a thin direct call into `HostSwapProcessor` exposed through `InternalsVisibleTo("JunimoServer.Tests")` if needed) and asserts:

| Scenario | Asserts |
|----------|---------|
| Already-server host | `IsServerHost` skip, no file mutation |
| Case-insensitive server name | "SERVER", "server", "Server" all detected |
| Non-server host | Detected as needing swap |
| Server farmer identity after swap | name, displayName, favoriteThing, whichPetType, isCustomized, homeLocation, empty userID — all match `ServerFarmerIdentity` |
| New server ID is unique | Differs from every pre-existing `UniqueMultiplayerID` |
| Old host moved to `<farmhands>` | Original name appears in `<farmhands><Farmer>` |
| Old host data preserved | Inventory, skills, name intact |
| Old host `userID` cleared | Empty string |
| With unclaimed cabin | `<homeLocation>` matches cabin `uniqueName`, `farmhandReference/uid` = old host ID, farmhands list size unchanged |
| Without cabins | `<homeLocation>` empty, farmhands list grows by 1 |

### `tests/JunimoServer.Tests/Fixtures/SaveImport/save-with-cabin.xml`
Minimal valid save XML: `<SaveGame>` with `<player>`, `<farmhands>` (1 unclaimed `<Farmer>`), 1 cabin `<Building>`.

### `tests/JunimoServer.Tests/Fixtures/SaveImport/save-no-cabin.xml`
Save with no cabins and empty `<farmhands>` list.

---

## Files to Modify

### `mod/JunimoServer/JunimoServer.csproj`
Add `InternalsVisibleTo` for the existing E2E test project (only if `HostSwapProcessor`'s static methods need to be invoked directly from tests; otherwise the `SaveImportService` public surface is enough):
```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
    <_Parameter1>JunimoServer.Tests</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

### `mod/JunimoServer/Services/GameCreator/GameCreatorService.cs`
- Replace inline server-farmer literals at `:113-118` with reads from `ServerFarmerIdentity`. Behavior unchanged; eliminates the "kept in sync" smell.

### `mod/JunimoServer/Services/GameLoader/GameLoaderService.cs`
- Add `SaveImportService` constructor parameter
- Call `_saveImportService.PrepareForDedicatedServer(saveName)` before `SaveGame.Load(saveName)` in `LoadSave()`

### `mod/JunimoServer/Services/Commands/SavesCommand.cs`
- Add `HostName` field to `SaveInfo` class
- In `ReadSaveGameInfo`: read `//player/name` from main save XML (already loaded at line 197)
- In `ShowSaveInfo`: display `Host: {name} (will be auto-converted on load)` vs `Host: Server (ready)`
- In `SelectSave` preview: same display
- No new service dependency needed. Reuses existing XML parsing.

---

## Reference Files (read-only, for implementation)
- `mod/JunimoServer/Services/GameCreator/GameCreatorService.cs:113-120` -- server farmer values (extract into `ServerFarmerIdentity`)
- `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs:480` -- `EnsureAtLeastXCabins` definition; `:132` call from `OnSaveLoaded`; `:537-577` -- `IsCabinAvailable`
- `mod/JunimoServer/Services/AuthService/FarmhandSenderService.cs:199` -- `EnsureAtLeastXCabins(reservedIds.Count + 1)`; `:209` -- `IsFarmhandAvailable`; `:420` -- `IsLobbyCabinFarmhand`
- `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:84` -- `checkFarmhandRequest` Harmony prefix
- `mod/JunimoServer/Services/Commands/SavesCommand.cs:166-230` -- `XmlDocument` parsing pattern to follow
- `mod/JunimoServer/Services/SteamGameServer/SteamConstants.cs` -- constants class pattern
- `mod/JunimoServer/ModEntry.cs:123-131` -- service auto-discovery
- `mod/JunimoServer/ModService.cs` -- base class
- `decompiled/.../SaveGame.cs:54-58` -- `<player>`, `<farmhands>` XML structure
- `decompiled/.../SaveGame.cs:500-517` -- `SaveGameInfo` is `Farmer` serialized with `<Farmer>` root
- `decompiled/.../Cabin.cs:21-22,80-103` -- `farmhandReference`, `CanAssignTo`, `AssignFarmhand`
- `decompiled/.../NetWorldState.cs:781-809` -- `TryAssignFarmhandHome` auto-assignment logic
- `decompiled/.../GameServer.cs:495-506,508-519` -- `authCheck`, `IsFarmhandAvailable`
- `decompiled/.../NetFarmerRef.cs` -- XML shape: `<defined><boolean>`, `<uid><long>`

---

## Verification

### E2E tests
```bash
make test FILTER=SaveImportE2ETests
```

### Manual integration test
1. Copy a real local save (personal farmer as host) into server's saves directory
2. Run `saves info <name>` -> verify shows `Host: {name} (will be auto-converted on load)`
3. Run `saves select <name> --confirm` -> start server
4. Verify:
   - No errors in server log
   - VNC shows "Server" as host
   - Connect as original host -> appears in character selection -> joins
   - Community Center doors unlocked, cave type preserved
   - Restart server -> swap does NOT re-run (idempotent)
5. Test no-cabin edge case: save with zero cabins -> verify farmhand auto-assignment on connect
