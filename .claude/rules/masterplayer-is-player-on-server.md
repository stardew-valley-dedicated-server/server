---
paths:
  - "mod/JunimoServer/**"
---

# On this dedicated server `Game1.MasterPlayer` IS `Game1.player`

The server hard-sets `Game1.multiplayerMode = 2` on both the new-game path (`GameCreatorService.cs:149`) and the load path (`GameLoaderService.cs:51`), so `Game1.IsMasterGame` (`Game1.cs:1808` — `multiplayerMode == 2`) is **always true** here. `Game1.MasterPlayer` (`Game1.cs:1847`) returns `serverHost.Value` only in the `!IsMasterGame` branch; on the master it returns `player`. So on this server the two always resolve to the **same `Farmer` object** — there is no host-vs-master divergence to defend against.

**Why:** The `cc-door-unlock-duplicate-mail` plan justified a duplicate-mail bug with "`Game1.player` and `Game1.MasterPlayer` are not guaranteed to be the same object on a dedicated host." That premise is false under `multiplayerMode = 2`, which made the plan's entire Incident/Impact narrative wrong. The duplicate-mail smell was real but for a different reason (the guard read `eventsSeen` while the write targeted `mailReceived`), not because host and master can split.

**How to apply:** When reasoning about any `Game1.MasterPlayer` access in mod code — mail flags, `eventsSeen`, `caveChoice`, any write or guard — treat it as the same farmer as `Game1.player`. Don't accept a bug explanation or design that hinges on the two being distinct objects; verify the actual mechanism instead. If a future change ever makes the server a non-master game (`multiplayerMode != 2`), this invariant breaks and every such assumption must be revisited.
