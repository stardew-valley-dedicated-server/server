# 06 — `!cabin` preview / cancel / confirm

Lowest priority; investigation-first. The open TODO at `mod/JunimoServer/Services/Commands/CabinCommand.cs:56-60` sketches it:

- `!cabin move [direction]` — show the cabin as a placement "ghost" without committing (no warp-target updates, no persistence)
- `!cabin cancel` — discard, restore the pre-ghost position
- `!cabin confirm` — commit (today's `!cabin` behavior: validate, write intent, relocate)

## Why it's gated on investigation

Both candidate mechanisms are unproven:

1. **Native building-move mode.** The vanilla game has a Robin building-move flow (`CarpenterMenu`). Unknown whether the server can trigger it on a *client* for the client's own cabin, and whether its placement callback can be intercepted server-side. Check the decompiled sources (`decompiled/sdv-1.6.15-24356/`, `CarpenterMenu`/`BuildingPaintMenu` and their network messages) before designing anything — per the host-automation rule, the game often already has the deterministic path.
2. **Ghost via `locationIntroduction` manipulation.** The existing interceptor (`CabinManagerService.OnLocationIntroductionMessage`) can place a building anywhere in the per-peer message copy — but that fires only on (re)introduction, not live. A live ghost would need location-delta forging or a client-side resync trigger; unknown cost and fragility.

## Investigation steps (timebox before committing to a design)

1. Read the decompiled building-move flow end-to-end: who validates, what message commits the move, can a farmhand move a building they "own"?
2. If client-triggerable: prototype "server grants temporary move permission for exactly the player's own cabin" — that may replace the whole subcommand UX with the native drag-and-place UI, which is strictly better than chat-driven directions.
3. If not: assess whether a chat-only flow (`move right` → re-show → `confirm`) is worth shipping without any visual ghost — arguably the validator's instant accept/reject reply (PR #403) already covers the "will it fit?" need, which was the TODO's original motivation.

## Exit criteria

A written recommendation: native-move grant / chat-flow / drop the TODO. "Drop" is a legitimate outcome — footprint validation already removed most of the pain this was sketched for; record the decision and delete the TODO if so.
