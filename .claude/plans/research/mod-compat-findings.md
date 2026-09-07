# Mod Compatibility Analysis — "Stardew Valley VERY Expanded" collection

Compatibility triage of the Nexus collection `tckf0m` ("Stardew Valley VERY
Expanded", 95 mods, built for online co-op) against JunimoServer's server-side
systems. Kept as a starting point for the next full compatibility sweep.

## Method

Triage + manual review against JunimoServer's architecture. **No headless boot test was
run** — verdicts are reasoned from mod behavior + our server model, not runtime-confirmed.
The only definitive test of "loads headless without crashing / doesn't fight our patches"
is booting each in the server container; that was intentionally out of scope for this pass.

## Compatibility frame (corrected)

The right axis is **NOT** "is the client vanilla." JunimoServer *supports* unmodded vanilla
clients, but content mods are normal modded co-op: install on server **and** matching
clients (already documented in `docs/features/mods.md`: "Content mods — install on server
AND clients"). Custom NPCs/maps/items/bundles are therefore **orthogonal** to server
compatibility — they are not incompatibilities.

A mod is JunimoServer-incompatible only if it fights our **server-side systems**:

- headless / hostless operation (no human host farmer; no real render/input on the host),
- host automation (auto-advances events, festivals, day transitions, menus),
- cabin/lobby management,
- save + Docker-volume provisioning,
- our Harmony patches on transition / always-on / warps / netReady.

Client-only UI/visual mods are **moot on the server** (they no-op headless) but run fine on
each client — harmless, not incompatible.

## Findings

### A. Clear infra conflicts (highest confidence — likely exclude / flag)

| Mod | Why |
|-----|-----|
| **Multi Save - Continued** | Changes save-folder layout/structure; collides with JunimoServer's save + volume provisioning. Highest-confidence conflict. |
| **Better Always Active (No Pause on Transitions - Load Screens - Window)** | JunimoServer is already an always-on headless host with its own pause/transition handling. Overlapping patches on the same behavior risk fighting our day-transition and host-automation guards. |

### B. Host-automation risk (not incompatible per se — needs boot test)

JunimoServer drives the host automatically; there is no human to click through prompts.
Mods that add **host-side interactive events / festivals / cutscenes / menus** can *wedge
the auto-host* if our automation doesn't know how to advance them. Not content-incompatible,
but the class most likely to hang startup or a day transition:

- Expansions with new events/festivals: **Stardew Valley Expanded**, **Ridgeside Village**,
  **East Scarp**, **Sword and Sorcery**, **Community Center Reimagined**.
- Anything gating progression on a host confirmation prompt.

These belong in a "test before production, watch for host-automation hangs" bucket, not a
flat exclude list.

### C. Content mods — compatible (install on server + clients)

Standard modded co-op. No server incompatibility. Includes all expansions, custom NPCs,
custom items/frameworks, maps, bundles:

- **Expansions/maps/locations:** Stardew Valley Expanded, Ridgeside Village, East Scarp,
  Sword and Sorcery, Downhill Project, Ginger Island Extra Locations - Redux, Solarium at
  the Spa Revisited, Additional Farm Cave, Integrated Minecarts, Buildable Ginger Island Farm.
- **Custom NPCs:** Creative Differences (Rodney), Eli and Dylan, Leilani, Lurking in the
  Dark (Sen), Nora the Herpetologist, Yagisan's Custom NPCs, Clint Reforged, Marnie
  Deserves Better, (CP) I Fixed Him (Shane), No Pam Enabling, Tidy Pam.
- **Custom items / entities / frameworks:** Item Extensions, Trinket Tinker, Livestock
  Bazaar, Custom Companions, Farm Type Manager, Mail Framework Mod, Unlockable Bundles,
  Animated Gemstones, Tiny Totem Statue Obelisks.
- **Content/asset packs:** the `Animated *` set (Clothes/Fish/Food/Furniture/Slime
  Eggs/RSV/East Scarp), tilesheets (DaisyNiko's, HxW, Lumisteria In/Outdoor), Standardized
  Seed Sprites, Cuter Slimes Refreshed, Customizable Slime Hutch, Seasonal Outfits ×3 +
  Mariner-to-Mermaid, They Deserve It Too ×2, Add Berry Seasons to Calendar, Anniversary
  on Calendar, Predict Reaction To Gift, Part of the Community, Seasonal Cute Characters.

### D. Frameworks / libraries (server-safe deps)

Load headless fine; required by others. Safe **iff** the content they feed is also on
clients (see C):

- SMAPI, Content Patcher, SpaceCore, Generic Mod Config Menu, StardewUI Continued,
  Mistycore, Mapping Extensions and Extra Properties (MEEP), Content Patcher Animations,
  Button's Extra Trigger Action Stuff (BETAS), Font Settings, Event Limiter.

### E. Client-only UI / visual (moot on server; run on client)

No server incompatibility; no-op headless. Do **not** need to be on the server at all:

- **UI/HUD:** Chests Anywhere, Lookup Anything, Quest Helper, UI Info Suite 2 Alternative,
  Convenient Inventory, NPC Map Locations, Remapping - Minimap Project, World Maps
  Everywhere, World Navigator - GPS, Deluxe Journal Continued, Mini Bars (Healthbars),
  Social Page Order Redux, Skip or Socialize, Better Crafting, Better Signs, Dynamic
  Reflections, Deluxe Journal.

### F. Gameplay tweaks — plausibly safe AND functional server-side (boot-test to confirm)

Server-authoritative behavior, no obvious host-interaction or infra conflict:

- Automate, Chests Anywhere (host storage), Friends Forever, Faster Path Speed, Sitting
  Restores Energy, SinZational Speedy Solutions, Convenient Inventory, Event Limiter.

## Recommendation for the docs "non-incompatible mods" list

1. Present buckets, not a flat allowlist (matches the user's stance that a guaranteed
   known-good list is too much to maintain).
2. Only **2 mods** are clear exclusions on server-infra grounds: **Multi Save - Continued**
   and **Better Always Active**.
3. Add a caveat callout for bucket B (expansions with host-interactive events) — "test for
   host-automation hangs before production."
4. Everything else is either standard content-on-both-sides (already covered by the existing
   "Content Mods" warning in `mods.md`) or harmless client-only.

## Open item

The genuinely useful confirmation — booting buckets A/B/F in the headless container to see
what actually loads / hangs / fights our patches — was scoped out. Flag if the docs work
wants that run.
