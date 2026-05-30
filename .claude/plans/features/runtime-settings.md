# Runtime-Modifiable Settings

## Context

`ServerSettingsLoader` reads `server-settings.json` at startup and exposes typed accessors. A small runtime-mutation surface already exists alongside it:
- `ServerSettingsLoader.SetVerboseLogging(value)` → mutates POCO + `Save()` (`mod/JunimoServer/Services/Settings/ServerSettingsLoader.cs:76-80`).
- `SettingsCommand` invokes that setter for `settings verbose [on|off]` (`mod/JunimoServer/Services/Commands/SettingsCommand.cs:308`).
- `PersistentOptions` mirrors a subset (`MaxPlayers`, `CabinStrategy`, `ExistingCabinBehavior`) into per-mod global SaveData and re-syncs on construction (`mod/JunimoServer/Services/PersistentOption/PersistentOptions.cs:8-65`).
- `!changewallet` already toggles `SeparateWallets` at runtime (`mod/JunimoServer/Services/Commands/ChangeWalletCommand.cs:14-33`).
- `ApiService` reads (does not mutate) `_settings.SeparateWallets` at `mod/JunimoServer/Services/Api/ApiService.cs:3260`.

This plan extends the existing setter pattern to cover the remaining mutable fields, rather than introducing a parallel layer.

## Goals

1. Allow operators to change specific settings at runtime (without restart).
2. Persist runtime-changed values to `server-settings.json` so they survive restarts.
3. Protect immutable settings from runtime changes.

## Mutable vs Immutable Settings

### Immutable (creation-time only)
These are baked into the save file at creation time and **must not** be changed at runtime:
- `FarmName` — embedded in save file path
- `FarmType` — determines farm map, cannot change after creation
- `StartingCabins` — only meaningful during initial game creation
- `SpawnMonstersAtNight` — set once at creation, stored in save

### Mutable (runtime-changeable)
These can safely be changed between runs or at runtime:
- `MaxPlayers` — adjusts `Game1.netWorldState.Value.CurrentPlayerLimit` (existing live-write at `mod/JunimoServer/Services/NetworkTweaks/NetworkTweaker.cs:676`).
- `CabinStrategy` — already supports migration via `DetectAndMigrateStrategyChange()`; persisted in `PersistentOptions`.
- `SeparateWallets` — already runtime-toggleable via `!changewallet` (`mod/JunimoServer/Services/Commands/ChangeWalletCommand.cs:14-33`). Out of scope for this plan; listed for completeness.
- `ExistingCabinBehavior` — only acts on startup, safe to change between runs; persisted in `PersistentOptions`.

### Currently classified immutable — keep immutable
- `ProfitMargin` lives in the "game creation settings (immutable after game created)" region of `ServerSettingsLoader.cs:32`. It's set once at world creation and stored in the save (`Game1.player.difficultyModifier`). Changing it mid-world is technically possible but produces split-economy semantics across hosts/farmhands. **Keep it immutable** unless an operator case demands otherwise — if reclassified later, do so as a deliberate change with the migration impact documented, not as an aside in this plan.

## Architecture

### Pattern: extend the existing setter surface

Add a setter on `ServerSettingsLoader` for each newly-mutable field, mirroring `SetVerboseLogging` (`ServerSettingsLoader.cs:76-80`). The setter (1) mutates the in-memory POCO, (2) calls `Save()` to persist to `server-settings.json`, and (3) calls a one-line in-game apply hook. `SettingsCommand` (`SettingsCommand.cs:308`) is the dispatcher and gains one branch per new field.

`PersistentOptions.SyncFromSettings` already mirrors `MaxPlayers`/`CabinStrategy`/`ExistingCabinBehavior` into per-mod global save data on construction (`PersistentOptions.cs:58-64`). After the new setters land, that sync stays one-way (settings → persistent) on every load — no second source of truth.

```
server-settings.json  ←──── ServerSettingsLoader.Set*  (mutate + Save)
                                       │
                                       ▼
                              in-memory ServerSettings
                                       │
                                       ▼
                       PersistentOptions.SyncFromSettings
                                       │
                                       ▼
                            consumers (services, ApiService reads, commands)
```

### Why not a separate `RuntimeSettings` / `RuntimeOverrides` layer

Two layers (settings file + per-save override JSON) would create a second writer for the same fields and bifurcate persistence. The repo already has one home for mutable server settings (`ServerSettingsLoader` + `Save()`) and one home for global per-mod state (`PersistentOptions`). Adding a third (per-save overrides) is unbacked scaffolding per `.claude/rules/universal/simplest-solution.md`. If per-save overrides are needed for a specific field later, add a per-save SaveData entry just for that field — don't generalize speculatively.

## Implementation Steps

1. **Add setters on `ServerSettingsLoader`** for `MaxPlayers`, `CabinStrategy`, `ExistingCabinBehavior` (mirroring `SetVerboseLogging`'s shape: mutate + `Save()`). Each setter also performs the in-game apply:
   - `SetMaxPlayers(n)` → also write `Game1.netWorldState.Value.CurrentPlayerLimit = n` (per the existing live-write at `NetworkTweaker.cs:676`).
   - `SetCabinStrategy(...)` → relies on existing `DetectAndMigrateStrategyChange` on next load; no in-game apply needed beyond `Save()`.
   - `SetExistingCabinBehavior(...)` → no immediate apply (only acts on startup); just persist.
2. **Re-run `PersistentOptions.SyncFromSettings(settings)`** after a setter mutates a field that `PersistentOptions` mirrors. Add a call in each setter, or expose a single `OnSettingsChanged` event that `PersistentOptions` subscribes to. Pick the simplest of the two given the field count is small.
3. **Extend `SettingsCommand`** with new branches `settings set <key> <value>`, `settings get <key>`, `settings reset <key>`. Reuse the existing `settings verbose [on|off]` parsing as the model.
4. **Reject immutable keys explicitly** in `SettingsCommand` — `farmtype`, `farmname`, `startingcabins`, `spawnmonstersatnight`, `profitmargin` produce a clear `LogLevel.Warn` (not Error per `.claude/rules/debugging.md`) message naming the constraint.
5. **Out of scope**:
   - Wallet toggle (already `!changewallet` at `ChangeWalletCommand.cs:14`).
   - Per-save overrides (no consumer requires them today; revisit only when one does).
   - Verbose logging setter (already exists).

## Testing

- Verify `settings set maxplayers 5` updates `Game1.netWorldState.Value.CurrentPlayerLimit` immediately and survives a restart via `server-settings.json`.
- Verify `settings set cabinstrategy CabinStack` persists and triggers migration on next load.
- Verify `settings set farmtype 3` is rejected with a clear `Warn`-level message.
- Verify `settings get maxplayers` reflects the post-set value.
- Verify `settings reset` is meaningful only if per-save overrides are added later — for the file-only pattern, the reset would require a "default" snapshot. Confirm the desired semantics before implementing or drop the subcommand.
