---
paths:
  - "mod/JunimoServer/**"
---

# Don't revert peer-replicated NetField writes inside `fieldChangeEvent`

If you find yourself trying to undo a peer-replicated write to a `NetField<T>` by calling `Set(oldValue)` from inside `fieldChangeEvent`, stop — the pattern is silently a no-op when interpolation is enabled (the default). Suppress at the source instead, or disable interpolation on the field.

**Why:** `NetLong.ReadDelta` (and other `NetField` subclasses) call `setInterpolationTarget(newValue)` (`NetFieldBase.cs:261-282`). With `InterpolationWait = true` (default, set in `NetFieldBase.cs:126`), this sets `targetValue = newValue` but leaves the backing `value` field at `oldValue`. `fieldChangeEvent` then fires (`NetFieldBase.cs:278-280`) while `value` still equals `oldValue`. A handler that calls `Set(oldValue)` hits the equality guard `if (newValue != value)` (`NetLong.cs:22`) — the condition is false (`oldValue != oldValue`), so `Set` is a no-op. Interpolation then converges to the peer's `newValue` over subsequent ticks. The "Reverted ..." log line lies; the revert never took effect.

**How to apply:** Three correct patterns when you need to reject a peer-side mutation:
1. **Patch the source.** If a peer-side action triggers a vanilla replicated write you don't want, Harmony-patch the master's outbound path (e.g. `SendBuildingConstructedEvent_Prefix` for `buildingConstructedEvent`). Suppression at the source is structural; nothing to revert later.
2. **Disable interpolation on the field.** Call `field.Interpolated(interpolate: false, wait: false)` (`NetFieldBase.cs:138-143`) before any peer delta can arrive — `setInterpolationTarget` then falls through to `cleanSet(newValue)` which writes `value = newValue` immediately, and `Set(oldValue)` becomes effective. Note: `Interpolated()` only changes flags for future deltas; it doesn't cancel an in-progress tween, and `CancelInterpolation()` finalizes the peer value (`NetFieldBase.cs:199-207`), so you must disable before the delta arrives.
3. **Use `fieldChangeVisibleEvent`** (`NetFieldBase.cs:121-122`, fires at `tickImpl` line 192-195 after interpolation completes). At that point `value` has converged; you can write a counter-delta. Costs one round-trip and is rarely the right answer, but is the only option if the field MUST stay interpolated.

If you reach for option 2 or 3, document why you can't fix it at the source.
