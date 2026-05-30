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
image size, while making the mod handle missing assets gracefully so the server boots
cleanly regardless of what the filter happens to elide.

---

## Analysis: The Vincent Error

### Error code path

The error triggers inside `Game1.AddCharacterIfNecessary()` (`Game1.cs:7313-7354`).
Within its try/catch (`Game1.cs:7336-7344`), there are exactly two `Texture2D` loads:

```csharp
nPC = new NPC(
    new AnimatedSprite("Characters\\" + textureNameForCharacter, 0, size.X, size.Y),  // (1) sprite
    new Vector2(tile.X * 64, tile.Y * 64),
    locationName, direction, characterId, canBeRomanced,
    content.Load<Texture2D>("Portraits\\" + textureNameForCharacter)                  // (2) portrait
);
```

1. **Sprite texture** (`Characters/Vincent`) — loaded by `AnimatedSprite` constructor.
   Guarded: `AnimatedSprite.LoadTexture()` (`AnimatedSprite.cs:203`) calls
   `Game1.content.DoesAssetExist<Texture2D>(textureName)` first and silently skips if
   false.
2. **Portrait texture** (`Portraits/Vincent`) — loaded directly as a constructor
   argument. **No existence check.** This is the most likely crash point.

If either load throws, the catch block logs `"Failed to spawn NPC 'Vincent'"` and the
NPC is never added to the world.

### Why the current download filter is not the cause

The current `SkipPatterns` (`tools/steam-service/SteamAuthService.cs:830-838`) only
strips:

- Audio wave banks (`Content/XACT/Wave Bank*.xwb`)
- Asian language fonts (`Content/Fonts/{Chinese,Korean,Japanese}/*`)
- Localized `.xnb` files (`*.(de-DE|es-ES|fr-FR|...).xnb`)

None of those match `Characters/Vincent.xnb` or `Portraits/Vincent.xnb`. The original
crash is most likely caused by a transient download corruption or a platform mismatch
(non-`linux` xnb in the linux content dir), not the filter. The fix below addresses
both: a mod-side safety net that catches any missing/corrupt asset, plus an expanded
filter that turns "all textures missing" from a guess into a guarantee.

### Key mechanism: manifest vs filesystem

`DoesAssetExist<T>()` (`LocalizedContentManager.cs:329`) checks the
`ContentHashes.json` manifest (a HashSet of asset names), NOT the filesystem. So if
we strip a file but keep the manifest, the game thinks the asset exists, tries to
read it from disk, and throws `ContentLoadException`. The mod-side interceptor must
short-circuit that path for missing-on-disk assets.

---

## Content Inventory

The inventory below is approximate and was derived from a one-off scan of
`ContentHashes.json` shipped with SDV 1.6.15. It is intended as scale-of-the-problem
context, not as a load-bearing source for the filter. The filter regexes target
directory prefixes; correctness does not depend on exact counts.

| Category     | Approx total | Server needs?           |
| ------------ | ------------ | ----------------------- |
| Textures     | ~700         | No (rendering disabled) |
| Data/Strings | ~2200        | Base English only       |
| Maps         | ~560         | Base only (pathfinding) |
| Audio        | ~4 wavebanks | No                      |
| Fonts        | ~55          | No                      |

Texture directory prefixes seen in the manifest: `Characters/` (NPC sprites,
`Characters/Monsters/`, `Characters/Farmer/`), `Portraits/`, `LooseSprites/`,
`Animals/`, `Buildings/`, `TileSheets/`, `TerrainFeatures/`, `Minigames/`, `Effects/`.

Data prefixes that must be retained (English): `Data/`, `Strings/`,
`Characters/Dialogue/`, `Characters/schedules/`, plus all of `Maps/` (base, no
locale suffix) and `ContentHashes.json` itself.

---

## Feasibility Assessment

**Verdict: feasible.** Reasons:

1. **Rendering is opt-in disabled.** `ServerOptimizer` (`mod/JunimoServer/Services/ServerOptim/ServerOptimizer.cs`)
   installs a `NullDisplayDevice` and suppresses frame drawing when
   `DISABLE_RENDERING=true`. With rendering disabled, tile rendering is a no-op.

2. **Clients load their own textures.** In SDV multiplayer, the host sends game
   state (positions, items, events) over the network — not textures. Each client
   loads content from its own local install.

3. **Game logic only needs data files.** NPC spawning, pathfinding, events,
   dialogue, and schedules read from `Data/`, `Strings/`, `Characters/Dialogue/`,
   and `Characters/schedules/`. Map layouts come from `Maps/`. Texture files are
   purely visual.

4. **SMAPI can intercept all content loads.** The `AssetRequested` event fires
   before disk access. A handler can substitute a 1×1 dummy `Texture2D` for any
   asset whose `.xnb` is missing, preventing `ContentLoadException` entirely.

---

## Proposed Implementation

Order: ship the mod-side interceptor first (safety net), then expand the download
filter. This keeps the server bootable while we tune the regex set.

### 1. Content interceptor service (mod-side safety net)

New service: `mod/JunimoServer/Services/ServerOptim/ContentInterceptor.cs`
(register from `ModEntry`).

- Subscribe to `helper.Events.Content.AssetRequested`.
- For any `Texture2D` request where the corresponding `.xnb` is missing on disk,
  provide a 1×1 `Texture2D` via `e.LoadFrom(() => ..., AssetLoadPriority.Low)`.
- Use `AssetLoadPriority.Low` so other mods providing real assets take precedence.
- If the real `.xnb` exists on disk, do nothing — let SMAPI resolve normally.

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

The interceptor is the safety net: even if the filter strips something the game
later reaches for, the server keeps running with a placeholder pixel.

### 2. Expand the steam download filter

Update `SkipPatterns` in `tools/steam-service/SteamAuthService.cs:830-838` to drop
all texture, font, and audio directories:

```
Characters/(?!Dialogue|schedules|Farmer).*    ← NPC sprites, monsters (but keep Farmer/ — see risk below)
Portraits/.*                                  ← all portrait textures
LooseSprites/.*                               ← UI sprites
Animals/.*                                    ← animal sprites
Buildings/.*                                  ← building textures
TileSheets/.*                                 ← tile sheet textures
TerrainFeatures/.*                            ← terrain textures
Minigames/.*                                  ← minigame art
Effects/.*                                    ← visual effects
Fonts/.*                                      ← all fonts (extends the existing Asian-only filter)
XACT/.*                                       ← all audio (extends the existing wavebank filter)
```

Retain:

- `Data/` (base English; localized variants already filtered)
- `Strings/` (base English)
- `Characters/Dialogue/` (base English)
- `Characters/schedules/` (base English)
- `Characters/Farmer/` (see risk below)
- `Maps/` (base; needed for pathfinding/collision)
- `VolcanoLayouts/`
- `ContentHashes.json` (manifest; required for `DoesAssetExist`)

Verify the regex set against the live `ContentHashes.json` before merge: parse the
manifest and count entries that match each retain/drop rule. Any retained file
that the regex would actually drop is a regression.

### 3. Validate the runtime behavior (gates, not aspirational text)

Before declaring the change shippable:

- [ ] Server boots with `DISABLE_RENDERING=true` against a stripped Content/
      directory; no `Failed to spawn NPC` errors in the log.
- [ ] Server boots with `DISABLE_RENDERING=false` against the same stripped
      directory; tile rendering paths hit the dummy texture without crashing.
- [ ] A real client connects, joins, and the day advances at least once.
- [ ] Image size reduction measured (compare `docker images` before/after).
- [ ] Run the existing E2E suite (`make test`) — the runner uses real clients,
      so any regression in client-side asset assumptions surfaces here.

---

## Edge Cases & Risks

### Map tilesheet loading with rendering enabled

`NullDisplayDevice.LoadTileSheet` is a no-op, so tilesheets are not loaded for
rendering when `DISABLE_RENDERING=true`. With `DISABLE_RENDERING=false` (the
default per `Env.cs:28`), the real display device runs and *will* try to load
tilesheets via `Game1.content.Load<Texture2D>()`. The interceptor catches that
and returns the dummy. This is acceptable — drawing produces garbage pixels but
does not crash. If the operator runs with rendering enabled and stripped content,
they accept that the rendered output is meaningless.

### `Characters/Farmer/` is consumed by FarmerRenderer at runtime

`FarmerRenderer.textureChanged()` (`FarmerRenderer.cs:347`) calls
`farmerTextureManager.Load<Texture2D>(textureName.Value)` where `textureName`
points into `Characters/Farmer/`. It then reads pixel data via
`texture2D.GetData(...)` and copies into a new `baseTexture`. With a 1×1 dummy,
that copy succeeds (1 pixel) but downstream consumers that index into the
texture for sprite rectangles get garbage. The server-side path that triggers
this is `MapService` (`mod/JunimoServer/Services/Map/MapService.cs:189-216`),
which reads the FarmerRenderer's `baseTexture` and `hairStylesTexture` via
reflection and crops a 16×16 rect for the player avatar export to the test/admin
UI.

**Decision:** keep `Characters/Farmer/` (17 files). The savings are negligible
compared to the rest of the texture set, and stripping it silently degrades the
map/avatar export feature. Reflected in the regex above as
`Characters/(?!Dialogue|schedules|Farmer).*`.

### Texture data read for game logic

Some vanilla code reads pixel data from textures (e.g. `FarmerRenderer`
recoloring). On a headless host with rendering disabled this is dead work; with
rendering enabled it produces visual garbage. Neither crashes given the
interceptor. If a specific feature regresses, exclude its directory from the
filter rather than reverting the whole change.

### Other mods on the server

Third-party SMAPI mods may expect real textures. `AssetLoadPriority.Low` means
mods that supply real assets via `e.LoadFrom(..., Low|Medium|High)` win. Mods
that only `e.Edit` an asset will be editing the 1×1 dummy. Acceptable for a
headless host; document this in the admin docs if/when the change ships.

---

## Related Files

| File                                                                       | Role                                                   |
| -------------------------------------------------------------------------- | ------------------------------------------------------ |
| `tools/steam-service/SteamAuthService.cs:830-848`                          | Current `SkipPatterns` and `ShouldSkipFile`            |
| `decompiled/sdv-1.6.15-24356/StardewValley/LocalizedContentManager.cs:329` | `DoesAssetExist<T>()` — manifest check                 |
| `decompiled/sdv-1.6.15-24356/StardewValley/Game1.cs:7313`                  | `AddCharacterIfNecessary()` — NPC spawn + error log    |
| `decompiled/sdv-1.6.15-24356/StardewValley/AnimatedSprite.cs:203`          | Sprite texture loading (`DoesAssetExist` guarded)      |
| `decompiled/sdv-1.6.15-24356/StardewValley/NPC.cs:1203`                    | `TryLoadPortraits` (used elsewhere; not by the spawn)  |
| `decompiled/sdv-1.6.15-24356/StardewValley/FarmerRenderer.cs:340`          | `textureChanged` — pulls `Characters/Farmer/*`         |
| `mod/JunimoServer/Services/ServerOptim/ServerOptimizer.cs`                 | Existing rendering disable + NullDisplayDevice         |
| `mod/JunimoServer/Services/Map/MapService.cs:189-216`                      | Reads FarmerRenderer textures via reflection           |
| `mod/JunimoServer/Env.cs:28`                                               | `DISABLE_RENDERING` flag (default `false`)             |
