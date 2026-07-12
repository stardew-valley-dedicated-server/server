# Ban Lightning Smite — a supernatural execution effect on ban

## Context

When an admin bans a player (`!ban "name"`), the server today just calls `Game1.server.ban(...)`
and sends a private confirmation to the admin (`mod/JunimoServer/Services/Commands/BanCommand.cs:54-55`).
Nobody else sees anything; the banned player vanishes silently.

This adds a fun, game-native homage (à la WoW's GM-summoned death): the offender is struck by a
**lightning bolt** at the spot they stood, and a **thunderclap is heard server-wide** — including by
players on entirely different maps, where hearing thunder out of nowhere reads as *unnatural*. A chat
eulogy announces the judgment. Then the ban fires.

Server-side only. No client mod, no new assets — it reuses vanilla sprites, sounds, and the existing
multiplayer broadcast routing.

## The core mechanic (verified against decompiled `sdv-1.6.15-24356`)

Two facts make the design work, both confirmed at source:

1. **`GameLocation.playSound(audioName, position?, pitch?)` broadcasts to every client *in that
   location*** — not just to whoever shares the host's camera. Chain:
   `GameLocation.playSound` (`GameLocation.cs:1581`) → `Game1.sounds.PlayAll` (`SoundsHelper.cs:115`)
   → `location.netAudio.Fire(...)` (`SoundsHelper.cs:123`) → serializes name+position+pitch and sends
   to peers (`NetAudio.cs:49-57`). `CanSkipSoundSync` returns `false` immediately on a networked
   server (`SoundsHelper.cs:157-159` — the local-co-op-only skip never triggers here), so every call
   takes the network path. **The headless host does not need to be in the location.**

2. **Message routing targets the farmers *in* the location, not the host's view.** Both `playSound`
   (via `netAudio`) and `Multiplayer.broadcastSprites` (`Multiplayer.cs:460`, message type 7) route
   through `broadcastLocationMessage` (`Multiplayer.cs:415-453`), whose payload-delivery loop is
   `foreach (Farmer farmer in loc.farmers)` (`:430`) plus a `loc.buildings → indoors.farmers` loop
   for cabin/building interiors (`:434-444`). So firing into *any* loaded location reaches the remote
   clients standing there.

Therefore: enumerate every online player, resolve each one's authoritative location, **dedupe by
location**, and `playSound` into each distinct location → a thunderclap heard by the whole server.
Add `broadcastSprites` (the bolt) only at the offender's location → the visual is local to the scene.

### Supporting facts

- **`Game1.getOnlineFarmers()`** returns the `FarmerCollection` of connected farmers, host included
  (`Game1.cs:10961`).
- **`Farmer.currentLocation`** is backed by a synced `NetLocationRef`, so it's authoritative for
  *remote* farmhands on the host (resolves via the synced location name / isStructure). Covers main
  maps, building/cabin interiors, mine levels, and volcano floors — all tracked through each farmer's
  own `currentLocation`, so iterating farmers (not `Game1.locations`) is the reliable enumeration.
- **Dedupe is mandatory.** `playSound` is *per-location*; iterating players without deduping fires it
  twice in a shared location (double thunderclap). Dedupe by `GameLocation` reference.
- **The bolt sprite self-expires** — `Utility.drawLightningBolt` builds `TemporaryAnimatedSprite`s
  with a finite frame life (`Utility.cs:3290-3305`); each client prunes its own copy. No cleanup.

## Design

Fired inside `BanCommand`'s callback, **before** `Game1.server.ban(...)` (the ban disconnects the
target on the same tick, so the scene must be captured and broadcast first):

1. **Capture the scene.** Read `targetFarmer.currentLocation` and `targetFarmer.Position` *before*
   the ban (after disconnect the Farmer is gone). If `currentLocation` is null (edge case — player
   mid-warp / not fully loaded), skip the visual and the per-location fan-out, send the eulogy, ban.
2. **Strike at the scene** (offender's location): `broadcastSprites` the lightning bolt at the
   offender's tile + `playSound("thunder")` there. Seen *and* heard by everyone present.
3. **Thunder everywhere else**: from `getOnlineFarmers()`, collect distinct `currentLocation`s,
   exclude the scene location (already struck in step 2) and any null, and `playSound("thunder")`
   into each. A thunderclap with no visible source on every other map.
4. **Eulogy**: one `SendPublicMessage` to all (themed line, see below).
5. **Ban**: `Game1.server.ban(targetFarmer.UniqueMultiplayerID)` + the existing private confirmation
   to the admin.

### Scope

Audio is map-wide: thunder fires into every distinct online-player location. The bolt fires only at
the offender's location. Hearing thunder on a map where nothing is happening is the effect — a
sourceless clap reads as unnatural, which is the point. A bolt sprite is only worth broadcasting where
someone can see it, so the visual stays scene-only.

### Eulogy text

Keep it ASCII (the send primitive tags messages `en`; non-Latin glyphs would render as boxes per
`.claude/rules/chat-font-language-tag.md`). Stardew-flavored options to pick from:

- `⚡ The Valley has judged {name}.` (the ⚡ is in SmallFont? — verify; if it boxes, use text only)
- `A bolt from the heavens strikes {name} down. They have been banished from the Valley.`
- `{name} angered the spirits and has been cast out.`

Final wording is a content choice — settle it during implementation. If the ⚡ glyph isn't in
`SmallFont.en`, drop it for a plain-text line.

## Files

### New: `mod/JunimoServer/Services/Commands/BanEffect.cs` (or a method on a small helper)

A self-contained static helper so `BanCommand` stays a thin command registration. Responsibilities:

- `PlaySmite(IModHelper helper, Farmer target)`:
  - Capture `scene = target.currentLocation`, `pos = target.Position`.
  - If `scene != null`: build the bolt sprites and `multiplayer.broadcastSprites(scene, bolts)`;
    `scene.playSound("thunder")`.
  - Map-wide thunder: `Game1.getOnlineFarmers()` → `.Select(f => f.currentLocation)`
    → `.Where(l => l != null && l != scene)` → `.Distinct()` → `playSound("thunder")` each.

`target` here is a real `Farmer` (returned by `helper.FindPlayerIdByFarmerNameOrUserName`,
`ModHelperExtensions.cs:22`), so `currentLocation`/`Position` are available. Note the admin in the
command (`msg.SourceFarmer`) is a `long` ID (`ReceivedMessage.cs:16`), not a `Farmer` — don't conflate
the two.

Bolt construction: mirror `Utility.drawLightningBolt` (`Utility.cs:3290-3305`) — same Cursors source
rect `(644, 1078, 37, 57)`, same vertical stack, same 16-arg string-texture
`TemporaryAnimatedSprite("LooseSprites\\Cursors", ...)` constructor — but collect the sprites into an
array and pass them to `broadcastSprites` instead of adding to `temporarySprites` locally (the vanilla
helper only does the local add, which would NOT replicate). Verified the string-texture sprite
round-trips: `TemporaryAnimatedSprite.Write` serializes `textureName` (`:1514`) and the client calls
`loadTexture()` on read, so the bolt reconstructs on every client. `Write` throws for *subclasses*
(`:1170`) but the bolt is the base type, so it's fine. `pos` for the strike is the farmer's pixel
`Position` (what `drawLightningBolt`'s `strikePosition` expects — it offsets in pixels), not a tile.

Get the `Multiplayer` instance via the existing `helper.GetMultiplayer()` extension
(`mod/JunimoServer/Util/ModHelperExtensions.cs:17`).

### Modify: `mod/JunimoServer/Services/Commands/BanCommand.cs`

Between the host-guard check (`:48-52`) and `Game1.server.ban(...)` (`:54`), insert:

```csharp
BanEffect.PlaySmite(helper, targetFarmer);
Game1.server.ban(targetFarmer.UniqueMultiplayerID);
helper.SendPublicMessage($"A bolt from the heavens strikes {targetFarmer.Name} down.");
helper.SendPrivateMessage(msg.SourceFarmer, "Banned: " + targetFarmer.Name);
```

(Eulogy is `SendPublicMessage` = all clients; the existing admin confirmation stays as
`SendPrivateMessage`.) Order matters: smite + eulogy before `ban()`.

**Thread context.** The command callback runs on the game thread — `ChatCommands.OnChatMessage`
(`ChatCommands.cs:143`) is driven by `ChatWatcher.receiveChatMessage_Postfix`, a Harmony postfix on
`ChatBox.receiveChatMessage` (game-thread UI). So `broadcastSprites` / `playSound` are called on the
right thread directly; no marshalling needed. (If a future caller invokes the ban from an off-thread
path — e.g. the HTTP API — it would need `RunOnGameThreadAsync`; not the case for the chat command.)

## What this does NOT change

- **Kick** (`KickCommand.cs`) — intentionally left silent; the spectacle is reserved for permanent bans.
  (Could be added later behind the same helper if desired.)
- **Auth-timeout / max-attempt / desync kicks** (`PasswordProtectionService`, `DesyncKicker`) — not bans,
  no effect.
- **No sound asset added** — `"thunder"` is a stock game cue. No sprite asset added — Cursors is vanilla.

## Caveats (honest)

- **The banned player likely sees/hears nothing** — they're disconnected on the same tick. The show is
  for the survivors (which is the better joke). Adding a deliberate delay between smite and `ban()` to
  let the victim witness it is possible but opens a window for them to act — out of scope unless asked.
- **Audio is server-wide; visual is scene-only** — by design (per the "unnatural thunder" concept).
  There is no native "draw a sprite on every client regardless of map" that wouldn't be pointless
  (no viewers off-scene); the cross-map payload is deliberately sound-only.
- **Glyph availability** — verify any non-ASCII eulogy char against `SmallFont.en` before using it.
- **The headless host is in `getOnlineFarmers()`.** `FarmerCollection`'s enumerator yields
  `Game1.player` first (`FarmerCollection.cs:49-51`) — on this server that's the dedicated `FakeFarmer`,
  whose `currentLocation` is whatever map the server is "viewing". The map-wide loop will therefore fire
  a (harmless, viewer-less) thunderclap into the host's location. Acceptable, but if we want to suppress
  it, exclude `Game1.player` in the `.Where(...)`. Decide during implementation — leaning *exclude it*,
  since a clap into a map with zero real players is wasted.
- **`"thunder"` cue id is confirmed** — the vanilla storm path plays it directly:
  `Game1.playSound("thunder")` (`Farm.doLightningStrike` `:1619`), with `"thunder_small"` at `:1613` as
  the quieter variant. So `scene.playSound("thunder")` is a valid stock cue. (`drawLightningBolt` itself
  plays no sound — the storm fires the bolt and the thunder separately, which is exactly why we pair
  `broadcastSprites` + `playSound` rather than expecting the bolt to be audible on its own.)

## Verification

1. `dotnet build mod/JunimoServer/JunimoServer.csproj` — mod builds (requires `GAME_PATH` in `.env`).
2. Manual / E2E: with ≥2 connected clients in **different** locations (e.g. one on the Farm, one in the
   Mines), ban a third. Confirm:
   - The Farm client sees the bolt at the offender's tile **and** hears thunder.
   - The Mines client hears thunder with no visual (the "unnatural" cue).
   - The eulogy appears in every client's chat.
   - The target is actually banned (`!listbans` shows them; they can't rejoin).
3. Edge case: ban a player who is alone in a location — confirm no double-thunder (dedupe works) and
   no exception when `getOnlineFarmers()` includes the headless host (whose `currentLocation` is the
   server's current map — harmless, just gets a thunderclap too, or is naturally deduped if it equals
   the scene).

Note: there is no unit-test layer in this repo (E2E only). A scripted E2E that asserts on the broadcast
is possible but heavy; the manual multi-client walk above is the practical gate. If an automated check
is wanted, assert the eulogy lands in all clients' chat history via the existing chat-history test
helpers — the sprite/sound itself isn't observable through the HTTP API.
