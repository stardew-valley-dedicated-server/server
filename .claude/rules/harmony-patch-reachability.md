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
