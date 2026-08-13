---
paths:
  - "mod/JunimoServer/**"
---

# A Harmony patch's reachability is its registering constructor's reachability

Whether a Harmony patch is active depends on whether the **service constructor that calls `harmony.Patch(...)` runs to completion** — not on whether the patched method is a universal choke point.

`PasswordProtectionService`'s constructor logs "Password protection is DISABLED" and **`return`s early when `!IsEnabled`** (i.e. `ServerPassword` is empty). Every `harmony.Patch(...)` call is below that return, so on a **passwordless server NONE of its patches apply** — including `checkFarmhandRequest`, `processIncomingMessage`, `sendMessage`, and the `GameServer.playerDisconnected` postfix. The patched method being a single choke point all transports route through is irrelevant if the patch was never registered.

So always-on / transport-level behavior must NOT live in `PasswordProtectionService` — it is auth-only and fully no-ops without a password (the common operator config). Put such patches in an unconditionally-constructed service: `CabinManagerService` (patches `GameServer.sendServerIntroduction`, `GameServer.playerDisconnected`, `Utility.getHomeOfFarmer` unconditionally) or `NetworkTweaker`.

**Why:** The abandoned-claim disconnect heal was first wired into `PasswordProtectionService.OnPlayerDisconnected` — correct that `playerDisconnected` is a universal choke point, but its patch only registers with a password set, so the heal never fired on the passwordless default. Caught only by the E2E test, not build or static review.

**How to apply:** Before hosting a patch (or any always-required behavior) in a service, check whether it must run on configs where that service early-returns. If universal, register it in a service whose constructor always completes.

## The other reachability bound: patches never run on farmhand clients

Players connect with unmodded vanilla clients (CLAUDE.md), so a patch reaches only code executing in the **server process**. Patching a method that vanilla resolves client-side for the behavior you're changing — `Game1.warpFarmer`'s local warp resolution, menu logic, totem targets, `getMailboxPosition` during interaction — changes nothing for farmhands; only the host's own calls see it. The per-player-farms plan initially routed farmhand warps via a `Game1.warpFarmer` prefix — dead code for every farmhand, caught only on re-review. Before proposing a patch, classify where the target method executes for the affected actor: server-simulated logic (NPC movement, overnight processing, night events, save/load) is patchable; client-side resolution must instead ride the server→client control surface.

## The third bound: the target's shape and timing must admit a patch at all

Even a correctly-registered, server-side target can be structurally unpatchable. Four shapes to check **before** proposing a Harmony fix against SMAPI or game internals — when all fail, the fix is a source patch (build SMAPI/the game from source), not Harmony:

- **A method-local can't be reached.** A `bool x = … % 60 == 0` local gating logic inside a private method is invisible to prefix/postfix; only an IL transpiler can touch it, and a transpiler matching a bare constant inside a hot core-loop method is the most version-fragile patch you can own (an upstream re-JIT breaks the match silently). SMAPI's one-second divisor is exactly this.
- **A patch downstream of a guard can suppress, not synthesize.** If vanilla only *calls* your target when an upstream guard passes (e.g. `ManagedEvent.Raise` invoked only on the 60th tick), a prefix there can drop calls but cannot *create* the calls the guard skipped. You can't make an event fire more often by patching its raise.
- **A method on a generic type shares one JIT body across reference-type instantiations.** Per Harmony's own docs, patching `Foo<SomeRefType>.Bar` patches `Bar` for *every* reference-type `Foo<T>` — you cannot scope it to one instantiation without a runtime type/name check on a per-call hot path.
- **Code that runs before `LoadMods` is out of reach.** SMAPI owns the process entry point (`Program.Main`) and loads mods late in `Game.Run()`; anything firing during `Game.Initialize()` (e.g. `Game1.InitializeSounds`, `SGameLogger` output) has already run before any mod `Entry` installs a patch. There is no "before SMAPI" seam for a mod, and referencing upstream Harmony directly doesn't change *when your code first executes* — only a `DOTNET_STARTUP_HOOKS`-style pre-`Main` hook could, at much higher fragility.

**Why:** A full session concluded that SMAPI's one-second-divisor bug *and* an early XACT-init log line both look mod-Harmony-patchable but aren't — the first three shapes killed the divisor, the timing bound killed the log line. Three confident "yes, Harmony can do this" answers were each wrong until verified against the 4.5.2 DLL, Harmony 2.2.2's generics docs, and the launch chain (`SCore.RunInteractively` → `Game.Run` → `InitializeSounds`, with `LoadMods` firing later). The fix landed as a source patch built into SMAPI, not a mod patch.

**How to apply:** Before proposing a Harmony patch against SMAPI/game internals, decompile the target (`ilspycmd` against the on-disk DLL) and check the four shapes: is the thing you need to change a local? is your seam downstream of a guard you can't move? is the declaring type generic with ref-type args? does the target run before `LoadMods`? Any yes means Harmony can't cleanly do it — reach for a source patch and verify the built artifact ([`runtime-post-conditions-are-gates.md`](universal/runtime-post-conditions-are-gates.md)).
