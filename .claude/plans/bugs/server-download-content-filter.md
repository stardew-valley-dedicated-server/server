# Content Filtering & Asset Stripping for Dedicated Server

## Context

User error report:

```
sdvd-server | [app ] [01:39:58 ERROR game] Failed to spawn NPC 'Vincent'.
sdvd-server | [app ] Microsoft.Xna.Framework.Content.ContentLoadException: Asset does not appear to be a valid XNB file. Did you process your content for Windows?
```

Reproduced locally with a different asset:

```
[15:52:59 ERROR game] Couldn't create the 'MermaidHouse' location. Is its data in Data/Locations invalid?
sdvd-server      | [app           ] System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.
sdvd-server      | [app           ]  ---> Microsoft.Xna.Framework.Content.ContentLoadException: Asset does not appear to be a valid XNB file. Did you process your content for Windows?
sdvd-server      | [app           ]    at Microsoft.Xna.Framework.Content.ContentManager.GetContentReaderFromXnb(...)
sdvd-server      | [app           ]    at xTile.Display.XnaDisplayDevice.LoadTileSheet(TileSheet tileSheet)
sdvd-server      | [app           ]    at StardewValley.GameLocation.loadMap(String mapPath, Boolean force_reload)
sdvd-server      | [app           ]    at StardewValley.GameLocation..ctor(String mapPath, String name)
sdvd-server      | [app           ]    at StardewValley.Game1.CreateGameLocation(String id, CreateLocationData createData)
sdvd-server      | [app           ]    at StardewValley.Game1.AddLocations()
```

Goal: aggressively strip game textures/audio/fonts from the server download to reduce
image size, while keeping the server bootable regardless of what the filter elides.

---

## Existing safety net: the manifest is already pruned

The download path already keeps `ContentHashes.json` in agreement with what is on
disk, which makes the game's own guarded loads degrade gracefully instead of
crashing. The mechanism:

- `LocalizedContentManager.DoesAssetExist<T>()`
  (`LocalizedContentManager.cs:329-360`) returns `_manifest.Contains(item)` — it
  checks the in-memory manifest loaded from `ContentHashes.json`
  (`LocalizedContentManager.cs:143-170`), **not** the filesystem.
- `LoadImpl<T>()` (`LocalizedContentManager.cs:367-374`) gates every load on
  `DoesAssetExist`: a name absent from the manifest throws a clean
  `ContentLoadException("Could not load …")` that the game's localized→English
  fallback can catch, rather than a raw XNB-parse failure on a missing file.
- `PruneContentManifest()` (`tools/steam-service/SteamAuthService.cs:930-968`),
  called immediately after download (`SteamAuthService.cs:1423`), rewrites
  `ContentHashes.json` to drop entries for any file the filter skipped. Manifest
  and filesystem stay consistent, so `DoesAssetExist` never reports a stripped
  file as present.

**Consequence for this work:** the manifest/filesystem disagreement is handled.
Expanding the filter (Section 2) does not need a separate manifest fix — the prune
step already covers any newly-stripped directory. The remaining crash class is
narrower (Section 1).

---

## The remaining crash class: unguarded direct loads

`DoesAssetExist`/`LoadImpl` only protects call sites that go through the guarded
path. Some vanilla code loads a texture **directly** without an existence check, so
a stripped asset throws `ContentLoadException` before the manifest guard can fire.

`Game1.AddCharacterIfNecessary()` (`Game1.cs:7313-7354`) is the proven example. Its
try/catch (`Game1.cs:7336-7344`) performs two `Texture2D` loads:

```csharp
nPC = new NPC(
    new AnimatedSprite("Characters\\" + textureNameForCharacter, 0, size.X, size.Y),  // (1) sprite
    new Vector2(tile.X * 64, tile.Y * 64),
    locationName, direction, characterId, canBeRomanced,
    content.Load<Texture2D>("Portraits\\" + textureNameForCharacter)                  // (2) portrait
);
```

1. **Sprite** (`Characters/Vincent`) — `AnimatedSprite.LoadTexture()`
   (`AnimatedSprite.cs:203-217`) calls `DoesAssetExist<Texture2D>` first and skips
   silently if absent. Guarded.
2. **Portrait** (`Portraits/Vincent`) — a bare `content.Load<Texture2D>(...)`
   constructor argument with **no existence check**. This is the crash point: the
   catch logs `Failed to spawn NPC 'Vincent'` and the NPC is never added.

The `MermaidHouse` reproduction is the same shape one layer down: with rendering
enabled, the real display device's `XnaDisplayDevice.LoadTileSheet` loads a
tilesheet texture directly during `GameLocation.loadMap`, bypassing the manifest
guard.

The interceptor in Section 1 closes this class generally — any direct
`Texture2D` load of a stripped asset gets a placeholder instead of an exception.

---

## Content Inventory

Approximate scale-of-the-problem context derived from a scan of the
`ContentHashes.json` shipped with SDV 1.6.15. The filter regexes target directory
prefixes; correctness does not depend on exact counts.

| Category     | Approx total | Server needs?           |
| ------------ | ------------ | ----------------------- |
| Textures     | ~700         | No (rendering disabled) |
| Data/Strings | ~2200        | Base English only       |
| Maps         | ~560         | Base only (pathfinding) |
| Audio        | ~4 wavebanks | No                      |
| Fonts        | ~55          | No                      |

Texture directory prefixes in the manifest: `Characters/` (NPC sprites,
`Characters/Monsters/`, `Characters/Farmer/`), `Portraits/`, `LooseSprites/`,
`Animals/`, `Buildings/`, `TileSheets/`, `TerrainFeatures/`, `Minigames/`,
`Effects/`.

Data prefixes that must be retained (English): `Data/`, `Strings/`,
`Characters/Dialogue/`, `Characters/schedules/`, all of `Maps/` (base, no locale
suffix), and `ContentHashes.json` itself.

---

## Feasibility

**Verdict: feasible.** Reasons:

1. **Rendering defaults off.** `ServerOptimizer` installs a `NullDisplayDevice` and
   suppresses frame drawing when `SERVER_FPS == 0`, which is the default
   (`Env.cs:47-48`; `0` or unset disables rendering, `N > 0` throttles draws at N
   fps). `NullDisplayDevice.LoadTileSheet`
   (`mod/JunimoServer.Shared/NullDisplayDevice.cs:11-13`) is a no-op, so tilesheet
   textures are never loaded for rendering at the default.

2. **Clients load their own textures.** In SDV multiplayer the host sends game
   state (positions, items, events) over the network, not textures. Each client
   loads content from its own local install.

3. **Game logic only needs data files.** NPC spawning, pathfinding, events,
   dialogue, and schedules read from `Data/`, `Strings/`, `Characters/Dialogue/`,
   and `Characters/schedules/`. Map layouts come from `Maps/`. Texture files are
   purely visual.

4. **SMAPI can intercept content loads.** The `AssetRequested` event fires before
   disk access. A handler can substitute a 1×1 dummy `Texture2D` for any texture
   whose `.xnb` is missing, closing the unguarded-direct-load crash class.

---

## Proposed Implementation

Order: ship the mod-side interceptor first (closes the crash class), then expand
the download filter. This keeps the server bootable while the regex set is tuned.

### 1. Content interceptor service (mod-side)

New `ModService`: `mod/JunimoServer/Services/ServerOptim/ContentInterceptor.cs`.
`ModService` subclasses are auto-discovered and DI-constructed by `ModEntry`
(`ModEntry.cs:184-217`); no manual registration is needed. Take `IModHelper` in the
constructor and subscribe in `Entry()`:

- Subscribe to `Helper.Events.Content.AssetRequested`.
- For a `Texture2D` request whose `.xnb` is missing on disk, provide a 1×1
  `Texture2D` via `e.LoadFrom(..., AssetLoadPriority.Low)`.
- `AssetLoadPriority.Low` lets other mods supplying real assets take precedence.
- If the `.xnb` exists on disk, do nothing — SMAPI resolves normally.

```csharp
private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
{
    if (e.DataType != typeof(Texture2D)) return;

    string xnbPath = Path.Combine(Constants.GamePath, "Content", e.Name.Name + ".xnb");
    if (File.Exists(xnbPath)) return;

    e.LoadFrom(
        () => new Texture2D(Game1.graphics.GraphicsDevice, 1, 1),
        AssetLoadPriority.Low);
}
```

This catches every direct `Texture2D` load of a stripped asset — the NPC portrait,
a rendering-enabled tilesheet load, or anything else — so the server keeps running
with a placeholder pixel.

### 2. Expand the steam download filter

`BuildSkipPatterns` (`tools/steam-service/SteamAuthService.cs:1056-1077`) builds the
`Regex[]` tested by `ShouldSkipFile` (`:1079-1087`) against depot file names. The
existing patterns are **`Content/`-prefixed** (e.g. `Content/Fonts/{family}.*`,
`Content/XACT/Wave Bank*.xwb`), so new patterns must keep that prefix and match the
depot path, not a content-root-relative path.

Add patterns to drop the texture, font, and audio directories:

```
Content/Characters/(?!Dialogue|schedules|Farmer).*   ← NPC sprites, monsters (keep Farmer/ — see risk)
Content/Portraits/.*                                  ← all portrait textures
Content/LooseSprites/.*                               ← UI sprites
Content/Animals/.*                                    ← animal sprites
Content/Buildings/.*                                  ← building textures
Content/TileSheets/.*                                 ← tile sheet textures
Content/TerrainFeatures/.*                            ← terrain textures
Content/Minigames/.*                                  ← minigame art
Content/Effects/.*                                    ← visual effects
Content/Fonts/.*                                      ← all fonts (extends the CJK-only filter)
Content/XACT/.*                                       ← all audio (extends the wavebank filter)
```

`PruneContentManifest` (`SteamAuthService.cs:930`) already runs after download, so
the newly-stripped entries are removed from `ContentHashes.json` automatically. No
manifest change is required here.

Retain:

- `Content/Data/` (base English; localized variants already filtered)
- `Content/Strings/` (base English)
- `Content/Characters/Dialogue/` (base English)
- `Content/Characters/schedules/` (base English)
- `Content/Characters/Farmer/` (see risk below)
- `Content/Maps/` (base; needed for pathfinding/collision)
- `Content/VolcanoLayouts/`
- `Content/ContentHashes.json` (manifest)

Before merge, parse the live `ContentHashes.json`, run each entry through
`ShouldSkipFile` with the new patterns, and confirm every retained prefix above
survives and the intended texture/font/audio prefixes drop. Any retained file the
regex would drop is a regression.

### 3. Validate runtime behavior (gates)

- [ ] Server boots at the default `SERVER_FPS=0` against a stripped `Content/`
      directory; no `Failed to spawn NPC` errors in the log.
- [ ] Server boots with `SERVER_FPS>0` (rendering enabled) against the same
      stripped directory; tilesheet/texture loads hit the dummy texture without
      crashing.
- [ ] A real client connects, joins, and the day advances at least once.
- [ ] Image size reduction measured (compare `docker images` before/after).
- [ ] Run the existing E2E suite (`make test`) — the runner uses real clients, so
      any regression in client-side asset assumptions surfaces here.

---

## Edge Cases & Risks

### Tilesheet loading with rendering enabled

At the default `SERVER_FPS=0`, `NullDisplayDevice.LoadTileSheet` is a no-op, so
tilesheets are never loaded. With `SERVER_FPS>0` the real display device runs and
loads tilesheets via `Game1.content.Load<Texture2D>()`. The interceptor returns the
dummy: drawing produces garbage pixels but does not crash. An operator running with
rendering enabled and stripped content accepts that the rendered output is
meaningless.

### `Characters/Farmer/` is consumed at runtime

`FarmerRenderer.textureChanged()` (`FarmerRenderer.cs:347`) calls
`farmerTextureManager.Load<Texture2D>(textureName.Value)` for a texture in
`Characters/Farmer/`, then reads pixel data via `GetData(...)` into a new
`baseTexture`. With a 1×1 dummy the copy succeeds but downstream consumers that
index sprite rectangles get garbage. The server-side consumer is `MapService`
(`mod/JunimoServer/Services/Map/MapService.cs:189-216`), which reflection-reads
FarmerRenderer's `baseTexture` and `hairStylesTexture` and crops a 16×16 rect for
the player-avatar export to the test/admin UI.

**Decision:** keep `Characters/Farmer/` (17 files). The savings are negligible
against the rest of the texture set, and stripping it silently degrades the
map/avatar export. The regex `Content/Characters/(?!Dialogue|schedules|Farmer).*`
reflects this.

### Texture pixel reads for game logic

Some vanilla code reads pixel data from textures (e.g. `FarmerRenderer`
recoloring). On a headless host with rendering disabled this is dead work; with
rendering enabled it produces visual garbage. Neither crashes given the
interceptor. If a specific feature regresses, exclude its directory from the filter
rather than reverting the whole change.

### Other mods on the server

Third-party SMAPI mods may expect real textures. `AssetLoadPriority.Low` means mods
that supply real assets via `e.LoadFrom(..., Low|Medium|High)` win. Mods that only
`e.Edit` an asset edit the 1×1 dummy. Acceptable for a headless host; document in
the admin docs when the change ships.

---

## Related Files

| File                                                                       | Role                                                       |
| -------------------------------------------------------------------------- | ---------------------------------------------------------- |
| `tools/steam-service/SteamAuthService.cs:1056`                             | `BuildSkipPatterns` — the `Content/`-prefixed skip regexes |
| `tools/steam-service/SteamAuthService.cs:1079`                             | `ShouldSkipFile` — applies the regexes to depot file names |
| `tools/steam-service/SteamAuthService.cs:930`                              | `PruneContentManifest` — keeps `ContentHashes.json` in sync |
| `decompiled/sdv-1.6.15-24356/StardewValley/LocalizedContentManager.cs:329` | `DoesAssetExist<T>()` — manifest check                     |
| `decompiled/sdv-1.6.15-24356/StardewValley/LocalizedContentManager.cs:367` | `LoadImpl<T>()` — guards loads on `DoesAssetExist`         |
| `decompiled/sdv-1.6.15-24356/StardewValley/Game1.cs:7313`                  | `AddCharacterIfNecessary()` — unguarded portrait load      |
| `decompiled/sdv-1.6.15-24356/StardewValley/AnimatedSprite.cs:203`          | `LoadTexture()` — sprite load (`DoesAssetExist` guarded)   |
| `decompiled/sdv-1.6.15-24356/StardewValley/FarmerRenderer.cs:347`          | `textureChanged` — pulls `Characters/Farmer/*`             |
| `mod/JunimoServer/ModEntry.cs:184`                                         | `ModService` auto-discovery + DI construction              |
| `mod/JunimoServer/Services/ServerOptim/ServerOptimizer.cs`                 | Rendering disable + `NullDisplayDevice` install            |
| `mod/JunimoServer.Shared/NullDisplayDevice.cs:11`                          | `LoadTileSheet` no-op                                      |
| `mod/JunimoServer/Services/Map/MapService.cs:189-216`                      | Reflection-reads FarmerRenderer textures                   |
| `mod/JunimoServer/Env.cs:47`                                               | `SERVER_FPS` — `0` (default) disables rendering            |
