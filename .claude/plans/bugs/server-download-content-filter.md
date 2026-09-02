# Content Filtering & Asset Stripping for Dedicated Server

**Status:** validation
**Priority:** 1 (low)
**GitHub Issue(s):** none
**Area:** steam-service, docker
**Related:** [`corrupt-content-self-heal.md`](corrupt-content-self-heal.md)
**Observed:** production report: `Failed to spawn NPC 'Vincent'` (`ContentLoadException` on a stripped portrait); the `MermaidHouse` tilesheet path found by reading
**Next step:** design sign-off on the `Texture2D` interceptor and the expanded skip patterns before Phase 1 starts

## Symptom

The existing manifest protection handles asset loads that pass through the game's guarded `LocalizedContentManager` path.

`LocalizedContentManager.DoesAssetExist<T>()` checks the in-memory manifest loaded from `ContentHashes.json`.

`LoadImpl<T>()` uses that check before attempting the underlying load.

Because `PruneContentManifest()` removes entries for files intentionally skipped during the Steam download, guarded loads do not attempt to parse an XNB that was deliberately removed.

However, some vanilla code performs direct `Texture2D` loads without first calling `DoesAssetExist<T>()`.

These direct loads can still fail with:

```text
ContentLoadException: Asset does not appear to be a valid XNB file
```

and cause otherwise valid server operations to fail.

### Confirmed failure: Vincent

`Game1.AddCharacterIfNecessary()` contains:

```csharp
nPC = new NPC(
    new AnimatedSprite("Characters\\" + textureNameForCharacter, 0, size.X, size.Y),
    new Vector2(tile.X * 64, tile.Y * 64),
    locationName,
    direction,
    characterId,
    canBeRomanced,
    content.Load<Texture2D>("Portraits\\" + textureNameForCharacter)
);
```

The two texture loads have different behavior.

#### Character sprite

```text
Characters/Vincent
        ↓
AnimatedSprite.LoadTexture()
        ↓
DoesAssetExist<Texture2D>()
        ↓
guarded
```

If the sprite has been stripped, the guarded path can recognize that it is absent.

#### Portrait

```text
Portraits/Vincent
        ↓
content.Load<Texture2D>()
        ↓
no explicit DoesAssetExist check
        ↓
direct XNB load
        ↓
ContentLoadException
```

This is the proven cause of the `Failed to spawn NPC 'Vincent'` failure.

### Confirmed failure: MermaidHouse

With rendering enabled, `MermaidHouse` can load its tilesheet through xTile's `XnaDisplayDevice.LoadTileSheet()`.

That path performs a direct texture load rather than relying on the normal guarded asset-existence check.

Therefore, stripping a tilesheet can produce the same raw missing-XNB failure.

## Fix

### Goal

Aggressively reduce the dedicated-server `Content/` footprint by removing visual, audio, and font assets that server gameplay does not require, while ensuring intentionally stripped assets cannot cause the server to crash.

The implementation must:

1. Establish the missing-texture fallback first.
2. Expand the existing Steam content filter.
3. Verify the filter against the **real** `ContentHashes.json` for the supported Stardew Valley version.
4. Verify that `PruneContentManifest()` remains synchronized with the actual filesystem after a real filtered download.
5. Test both headless and rendering-enabled server modes.
6. Explicitly test known unguarded texture-loading paths.
7. Verify server-side consumers of retained texture data.
8. Run the real E2E test suite.
9. Measure the actual Docker and `Content/` reduction.
10. Handle required assets through the smallest possible exceptions rather than weakening the optimization.

#### Architectural constraint

The existing `PruneContentManifest()` mechanism is the **only manifest-filtering mechanism**.

**Do not introduce a second manifest-filtering mechanism.**

The responsibilities are intentionally separated:

```text
Steam download
      ↓
BuildSkipPatterns / ShouldSkipFile
      ↓
unwanted files are not downloaded
      ↓
PruneContentManifest()
      ↓
ContentHashes.json reflects files that remain
      ↓
guarded game loads use the existing manifest protection
```

The new `Texture2D` interceptor exists only to cover texture loads that bypass that guarded path.

---

---

### 1. Implement the `Texture2D` fallback

Add:

```text
mod/JunimoServer/Services/ServerOptim/ContentInterceptor.cs
```

Create a `ModService` that subscribes to:

```csharp
Helper.Events.Content.AssetRequested
```

The service should:

1. Ignore requests whose data type is not `Texture2D`.
2. Determine whether the corresponding base-game `.xnb` exists under:

```text
<GamePath>/Content/
```

3. If the file exists, do nothing and allow normal content resolution.
4. If the file does not exist, provide a 1×1 placeholder texture.
5. Use `AssetLoadPriority.Low`.
6. Do not modify `ContentHashes.json`.
7. Do not treat the fallback as proof that arbitrary texture consumers are semantically safe.

Conceptually:

```csharp
private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
{
    if (e.DataType != typeof(Texture2D))
        return;

    string xnbPath = Path.Combine(
        Constants.GamePath,
        "Content",
        e.Name.Name + ".xnb");

    if (File.Exists(xnbPath))
        return;

    e.LoadFrom(
        () => new Texture2D(Game1.graphics.GraphicsDevice, 1, 1),
        AssetLoadPriority.Low);
}
```

The production implementation may adjust path normalization or texture creation as required by the actual SMAPI/XNA lifecycle.

#### Important limitation

The interceptor is a **crash-prevention fallback**, not a general replacement for arbitrary textures.

A 1×1 texture is sufficient only when the consumer merely requires a valid `Texture2D` object.

It is **not** proof that code expecting:

* specific texture dimensions;
* particular sprite rectangles;
* particular pixel data;
* color information;
* sprite-sheet regions;

will behave correctly.

If server functionality genuinely requires the original texture data, that asset must remain in the filtered download.

Do not hide such a dependency behind the placeholder.

---

### 2. Initialization safety is a hard requirement

The placeholder creation uses:

```csharp
Game1.graphics.GraphicsDevice
```

The implementation must verify that the graphics device is initialized at the points where the stripped assets are actually requested.

Do not assume that the `AssetRequested` callback necessarily occurs after graphics initialization in every relevant startup path.

If the graphics device is unavailable when the callback needs to create the placeholder, change the implementation so the fallback is safe for the actual lifecycle.

The interceptor is not considered complete until this has been demonstrated in runtime tests.

---

### 3. Required interceptor tests before expanding the filter

Before broadening the Steam filter, prove the fallback independently.

Use intentionally missing assets and reproduce both known failures.

#### Test A — Vincent portrait

Strip:

```text
Content/Portraits/Vincent.xnb
```

Then exercise the NPC creation path that previously produced:

```text
Failed to spawn NPC 'Vincent'
```

Expected:

* no raw missing-XNB crash;
* `AddCharacterIfNecessary()` completes successfully;
* Vincent can be spawned;
* no `Failed to spawn NPC 'Vincent'` error caused by the missing portrait.

The portrait does not need to render correctly.

#### Test B — MermaidHouse tilesheet

Strip the relevant `MermaidHouse` tilesheet.

Run with:

```text
SERVER_FPS>0
```

Expected:

* `MermaidHouse` can be created;
* the direct tilesheet request does not produce the original XNB failure;
* rendering-enabled server execution remains stable.

The resulting visual output does not need to be meaningful.

---

### 4. Expand the Steam content filter

Update:

```text
tools/steam-service/SteamAuthService.cs
```

Specifically update:

```text
BuildSkipPatterns()
```

The existing filter operates on depot paths beginning with `Content/`.

Therefore **all new patterns must retain the `Content/` prefix**.

Add:

```text
Content/Characters/(?!Dialogue|schedules|Farmer).*
Content/Portraits/.*
Content/LooseSprites/.*
Content/Animals/.*
Content/Buildings/.*
Content/TileSheets/.*
Content/TerrainFeatures/.*
Content/Minigames/.*
Content/Effects/.*
Content/Fonts/.*
Content/XACT/.*
```

These remove:

* NPC and monster sprites;
* portraits;
* loose/UI sprites;
* animal graphics;
* building graphics;
* tilesheets;
* terrain graphics;
* minigame graphics;
* visual effects;
* fonts;
* audio/XACT content.

#### Characters exception

The `Characters` rule deliberately retains:

```text
Content/Characters/Dialogue/
Content/Characters/schedules/
Content/Characters/Farmer/
```

and removes the other character-asset directories.

The negative lookahead must therefore remain logically equivalent to:

```text
Content/Characters/(?!Dialogue|schedules|Farmer).*
```

Do not replace this with a broader `Content/Characters/.*` rule.

---

### 5. Content that must remain

Retain:

```text
Content/Data/
Content/Strings/
Content/Characters/Dialogue/
Content/Characters/schedules/
Content/Characters/Farmer/
Content/Maps/
Content/VolcanoLayouts/
Content/ContentHashes.json
```

Localized variants must continue to follow the repository's existing localization filtering behavior.

Do not accidentally remove base-English data while expanding the visual-content filter.

---

### 6. Why `Characters/Farmer/` remains

`Characters/Farmer/` is intentionally retained even though most visual content is unnecessary to a headless server.

`FarmerRenderer` consumes these textures and reads their pixel data.

The server's `MapService` also consumes data derived from `FarmerRenderer`, including texture information used for player-avatar/map functionality.

This creates a real server-side dependency that is different from ordinary rendering-only textures.

The directory is small compared with the total visual-content footprint.

Therefore:

```text
Keep Characters/Farmer/
```

Do not remove it merely because the server normally runs without rendering.

If a future investigation proves that particular Farmer assets can be removed individually without affecting server functionality, they can be considered for narrower filtering later.

That is not required for this optimization.

---

### 7. Preserve the existing manifest/filesystem relationship

Do not add another manifest-generation or manifest-filtering mechanism.

The intended flow remains:

```text
Steam download
      ↓
ShouldSkipFile()
      ↓
unwanted content is skipped
      ↓
PruneContentManifest()
      ↓
ContentHashes.json removes entries for files not present
```

The interceptor must **not** edit:

```text
ContentHashes.json
```

The interceptor exists solely for the different class of failure:

```text
guarded game load
    ↓
existing ContentHashes protection

unguarded Texture2D load
    ↓
SMAPI AssetRequested
    ↓
placeholder if the base-game XNB is absent
```

This separation must remain explicit in the implementation.

Upgrade caveat: `PruneContentManifest()` only drops manifest entries for files that are *absent*; it does not delete files already on disk. On a persistent volume, a base-game file downloaded before the skip pattern existed will remain on disk and in `ContentHashes.json`, so the footprint reduction and skipped-file invariant only hold on a clean download. Decide at sign-off whether to require a clean volume or add a one-time cleanup pass for base-game files that now match `ShouldSkipFile()`.

---

### 8. Verify the filter against the real manifest

Before merging the expanded filter, use the actual:

```text
Content/ContentHashes.json
```

from the supported Stardew Valley version.

For the currently targeted version, use the real SDV 1.6.15 content manifest rather than relying on an inferred inventory.

Run every manifest entry through the same filtering logic used by:

```text
ShouldSkipFile()
```

Generate a validation report containing:

* manifest entry count before filtering;
* manifest entry count after filtering;
* number of entries removed;
* number of entries retained;
* removed entries grouped by category/prefix;
* retained entries grouped by category/prefix;
* total files removed;
* approximate bytes removed, where size information is available.

Explicitly verify that these remain:

```text
Data/
Strings/
Characters/Dialogue/
Characters/schedules/
Characters/Farmer/
Maps/
VolcanoLayouts/
ContentHashes.json
```

Explicitly verify that these are removed as intended:

```text
Portraits/
LooseSprites/
Animals/
Buildings/
TileSheets/
TerrainFeatures/
Minigames/
Effects/
Fonts/
XACT/
```

Also verify:

* `Characters/` visual assets outside the retained exceptions are removed;
* no `Data/` path is accidentally matched by a broad regex;
* no `Strings/` path is accidentally matched;
* no `Maps/` path is accidentally matched;
* localized variants continue to follow existing rules;
* `ContentHashes.json` itself is never filtered out.

This validation must use the **actual manifest contents**.

Regex inspection alone is insufficient.

---

### 9. Verify an actual filtered Steam download

Static manifest analysis is not sufficient.

Perform an actual filtered download using the modified Steam service.

After the download and `PruneContentManifest()` have completed, verify both directions of the relationship.

#### Retained-entry invariant

For every retained `ContentHashes.json` entry:

```text
manifest entry
      ↓
corresponding Content/<entry> file exists
```

No retained manifest entry may point to a deliberately skipped file.

#### Skipped-file invariant

For every intentionally skipped Content file:

```text
filtered depot file
      ↓
file absent from filesystem
      ↓
corresponding ContentHashes entry absent
```

This proves that:

* `BuildSkipPatterns()`;
* `ShouldSkipFile()`;
* the actual Steam download;
* and `PruneContentManifest()`

remain synchronized in practice.

No second pruning mechanism should be introduced to make this pass.

---

### 10. Runtime validation — headless mode

The primary target configuration is:

```text
SERVER_FPS=0
```

or unset/default behavior equivalent to disabled rendering.

Run the server against the actually stripped `Content/` directory.

Verify:

* server boots successfully;
* no missing-XNB crash occurs during startup;
* no `Failed to spawn NPC` errors occur because of stripped visual assets;
* NPCs spawn normally;
* Vincent can spawn;
* locations can be created;
* a real client can connect;
* a real client can join normally;
* multiplayer state operates normally;
* a day can advance;
* relevant gameplay functionality remains operational;
* `FarmerRenderer`/`MapService` functionality remains intact;
* the existing E2E suite passes.

Headless operation is the primary optimization target.

---

### 11. Runtime validation — rendering-enabled mode

Use:

```text
SERVER_FPS>0
```

against the **same stripped Content directory**.

Verify:

* server boots;
* rendering-enabled initialization succeeds;
* `MermaidHouse` can load;
* locations containing stripped tilesheets can load;
* direct texture requests reach the interceptor where applicable;
* missing textures do not reproduce the original raw XNB failure;
* rendering-enabled gameplay remains stable;
* server execution does not crash merely because visual assets were intentionally stripped.

Visually correct rendering is **not** a requirement.

The requirement is runtime stability.

A placeholder texture may result in meaningless or incorrect visuals. That is an accepted consequence of running a rendering-enabled server with aggressively stripped visual content.

However, a visual defect must not be used to excuse a crash in actual server functionality.

---

### 12. Explicitly test texture consumers

Do not treat successful placeholder creation as proof that every texture-dependent system is safe.

Explicitly exercise retained server functionality involving texture dimensions or pixel data.

At minimum test:

```text
FarmerRenderer
MapService
player-avatar/map export functionality
```

Verify that:

* `Characters/Farmer/` assets remain available;
* `FarmerRenderer` can initialize the relevant textures;
* texture pixel reads succeed;
* expected sprite dimensions are available;
* `MapService` can perform its existing avatar/map operations;
* no 1×1 placeholder is accidentally substituted for a texture that should have been retained.

If another texture consumer is discovered during testing:

1. determine whether it is actually required by server functionality;
2. identify the exact asset/category;
3. retain only that required asset/category;
4. add a regression test where practical.

Do not restore unrelated visual content.

---

### 13. Run the real E2E test suite

Run:

```text
make test
```

The E2E tests must use real clients as they normally do.

The test environment for the relevant server-side run must use the stripped content set.

This is important because startup alone does not exercise:

* actual multiplayer state;
* location transitions;
* NPC creation;
* gameplay progression;
* player rendering-related server functionality;
* avatar/map functionality;
* other code paths that may indirectly request assets.

`make test` must pass with the optimized content set.

If a test fails because a real server-required asset was stripped, identify and retain the smallest required asset/category.

Do not disable the test merely because the asset optimization caused it to fail.

---

### 14. Measure the actual reduction

Measure the deployed artifact before and after the optimization.

Record at minimum:

```text
Docker image size before
Docker image size after

Content/ size before
Content/ size after

Content file count before
Content file count after
```

Also report, where practical:

```text
visual/texture bytes removed
audio bytes removed
font bytes removed
total Content bytes removed
percentage reduction
```

The objective is a meaningful reduction in the deployed server footprint.

The optimization should not be judged solely by the number of files removed.

If the actual byte reduction is insignificant, reassess whether the additional filtering/interceptor complexity is justified.

---

### 15. Third-party SMAPI mod compatibility

The interceptor is a fallback for stripped base-game assets.

It is **not** a guarantee that arbitrary third-party server mods remain compatible with an aggressively stripped content installation.

Using:

```csharp
AssetLoadPriority.Low
```

allows a mod that supplies a real asset through the normal asset-request pipeline at a higher priority to take precedence.

However, mods that:

* assume an original texture physically exists;
* directly access the original file;
* require specific dimensions/pixels;
* edit an asset that has been replaced with a 1×1 placeholder;
* otherwise depend on visual assets being present;

may still be incompatible.

This is acceptable for the dedicated-server optimization.

Document the limitation.

If a supported server-side mod requires a particular visual asset:

1. identify the exact dependency;
2. retain the smallest possible asset/category;
3. add a regression test where appropriate.

Do not weaken the entire filter.

---

### 16. Regression handling

If any stripped asset causes a real regression:

#### Step 1 — Identify the dependency

Determine the exact asset or category being consumed.

#### Step 2 — Determine whether it is genuinely required

Distinguish between:

```text
server functionality requires asset
```

and:

```text
asset is merely being loaded unnecessarily
```

Do not retain assets merely because a load occurs if the resulting texture is not actually needed for server behavior.

#### Step 3 — Add the smallest exception

Prefer:

```text
one required asset
```

over:

```text
entire directory
```

and prefer:

```text
one required directory
```

over:

```text
all visual content
```

#### Step 4 — Add regression coverage

Where practical, add a test that exercises the dependency.

#### Step 5 — Keep unrelated stripping

Never respond to one required asset by restoring the entire visual/audio/font content set.

The target is:

```text
maximum safe stripping
+
minimum required exceptions
```

not:

```text
one regression
→ restore all content
```

---

### 17. Required implementation order

The work must proceed in this order:

#### Phase 1 — Interceptor

Implement:

```text
ContentInterceptor.cs
```

without yet relying on the expanded Steam filter.

Verify:

* graphics-device initialization safety;
* `Portraits/Vincent`;
* `MermaidHouse` with rendering enabled.

#### Phase 2 — Filter

Update:

```text
BuildSkipPatterns()
```

with the approved `Content/`-prefixed patterns.

#### Phase 3 — Static manifest validation

Run the new filtering logic against the actual supported `ContentHashes.json`.

Produce the category/count report.

#### Phase 4 — Real filtered download

Run the real Steam download.

Verify:

```text
filesystem ↔ ContentHashes.json
```

in both directions.

#### Phase 5 — Runtime tests

Run:

```text
SERVER_FPS=0
SERVER_FPS>0
```

against the stripped content set.

#### Phase 6 — Functional/E2E validation

Run:

```text
make test
```

using the stripped server environment.

#### Phase 7 — Measurement

Record the actual Docker and Content reductions.

#### Phase 8 — Narrow regressions

Only after observing real failures, add narrowly scoped exceptions.

---

## Verification

### Success criteria

The implementation is complete only when **all** of the following are true:

* [ ] Dedicated server boots with the reduced `Content/` set.
* [ ] `ContentHashes.json` accurately reflects files actually present.
* [ ] Every retained manifest entry has a corresponding file on disk.
* [ ] Every intentionally skipped content file has no corresponding manifest entry.
* [ ] `Portraits/Vincent` can be absent without causing NPC spawning to fail.
* [ ] Vincent can spawn normally.
* [ ] `MermaidHouse` can be created with its intentionally stripped tilesheet absent.
* [ ] `SERVER_FPS=0` operates normally.
* [ ] `SERVER_FPS>0` remains stable with the same stripped content set.
* [ ] Stripped texture requests do not reproduce the original raw XNB failure.
* [ ] NPCs spawn normally.
* [ ] A real client connects successfully.
* [ ] A real client can join normally.
* [ ] A day can advance.
* [ ] `FarmerRenderer` functionality remains intact.
* [ ] `MapService` functionality remains intact.
* [ ] Player-avatar/map export functionality remains intact.
* [ ] `make test` passes.
* [ ] The real supported `ContentHashes.json` has been validated against the filter.
* [ ] An actual filtered Steam download has been validated against the pruned manifest.
* [ ] Docker image size has been measured before and after.
* [ ] `Content/` size has been measured before and after.
* [ ] File-count reduction has been measured.
* [ ] Texture/audio/font byte reduction has been measured where available.
* [ ] Any required exceptions are narrow, justified, and regression-tested where practical.
* [ ] No second manifest-filtering mechanism has been introduced.

---

## Related files

| File                                                                   | Role                                                                   |
| ---------------------------------------------------------------------- | ---------------------------------------------------------------------- |
| `tools/steam-service/SteamAuthService.cs`                              | `BuildSkipPatterns()` — content filtering                              |
| `tools/steam-service/SteamAuthService.cs`                              | `ShouldSkipFile()` — applies filtering to depot paths                  |
| `tools/steam-service/SteamAuthService.cs`                              | `PruneContentManifest()` — synchronizes manifest with downloaded files |
| `decompiled/sdv-1.6.15-24356/StardewValley/LocalizedContentManager.cs` | `DoesAssetExist<T>()` — manifest existence check                       |
| `decompiled/sdv-1.6.15-24356/StardewValley/LocalizedContentManager.cs` | `LoadImpl<T>()` — guarded content loading                              |
| `decompiled/sdv-1.6.15-24356/StardewValley/Game1.cs`                   | `AddCharacterIfNecessary()` — unguarded portrait load                  |
| `decompiled/sdv-1.6.15-24356/StardewValley/AnimatedSprite.cs`          | `LoadTexture()` — guarded character sprite load                        |
| `decompiled/sdv-1.6.15-24356/StardewValley/FarmerRenderer.cs`          | Farmer texture/pixel-data consumer                                     |
| `mod/JunimoServer/ModEntry.cs`                                         | `ModService` discovery/DI                                              |
| `mod/JunimoServer/Services/ServerOptim/ServerOptimizer.cs`             | Rendering configuration                                                |
| `mod/JunimoServer.Shared/NullDisplayDevice.cs`                         | Headless tilesheet behavior                                            |
| `mod/JunimoServer/Services/Map/MapService.cs`                          | Farmer texture consumer                                                |
| `mod/JunimoServer/Env.cs`                                              | `SERVER_FPS` behavior                                                  |
| `mod/JunimoServer/Services/ServerOptim/ContentInterceptor.cs`          | Missing-texture fallback                                               |

---

## Final engineering principle

Strip the dedicated-server content as aggressively as practical, but prove every retained dependency.

Use the existing manifest pruning for guarded loads.

Use the `Texture2D` interceptor only as a targeted safety net for unguarded texture requests.

Do not use a placeholder to conceal a genuine server dependency on texture dimensions or pixel data.

When a dependency is discovered, retain the smallest possible asset or category.

The final state should therefore be:

```text
maximum safe content stripping
        +
existing manifest/filesystem synchronization
        +
targeted Texture2D fallback
        +
minimum required exceptions
        +
real runtime/E2E verification
```

and **not**:

```text
strip everything
→ encounter crash
→ restore everything
```
