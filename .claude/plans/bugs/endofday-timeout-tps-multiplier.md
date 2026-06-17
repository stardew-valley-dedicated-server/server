# Shipping-menu `EndOfDayTimeOut` never fires (TPS multiplier blows the window past a day)

Status: open. Root cause confirmed in current code.

## Incident

The shipping-menu / end-of-day timeout is meant to close a stuck `ShippingMenu`
and flip the server `offline` if a save hangs. In practice it never triggers, so
a server that gets stuck with a dangling `ShippingMenu` stays stuck indefinitely.

## Root cause

`AlwaysOn.OnUnvalidatedUpdateTick` (`Services/AlwaysOnServer/AlwaysOn.cs:375-384`)
increments `shippingMenuTimeoutTicks` once per tick and compares it against
`Config.EndOfDayTimeOut * Env.ServerTps`:

```csharp
if (shippingMenuTimeoutTicks >= Config.EndOfDayTimeOut * Env.ServerTps)
{
    Game1.options.setServerMode("offline");
}
```

- `Config.EndOfDayTimeOut` defaults to `120000` (`AlwaysOnConfig.cs:38`).
- `Env.ServerTps` defaults to `60` (`Env.cs:35`).

The counter is already in *ticks* (incremented per tick). Multiplying the
threshold by `ServerTps` treats the config value as seconds and converts to
ticks a second time, so the effective threshold is
`120000 × 60 = 7,200,000` ticks. At 60 TPS that is ~33 hours of wall-clock; at
the `SERVER_TPS=5` test/CI value it is ~16.7 days. Either way the in-game day
ends (and `Game1.timeOfDay == 610` resets the counter at `:386-394`) long before
the timeout is reached. The shipping-menu timeout is dead code.

This is the same class of bug the festival timeouts already fixed: those were
converted to `TicksToSeconds(Config.X)` so the config value (ticks at 60 TPS) is
read as a *duration* rather than re-scaled by live TPS
(`AlwaysOnFestivals.cs:182`, `TicksToSeconds(int ticks) => ticks / 60.0`). Only
the EndOfDay/shipping path still has the raw `* Env.ServerTps` multiplier.

## Fix

Make the comparison consistent with how `Config.EndOfDayTimeOut` is defined and
with the festival path. Two equivalent options:

- **Drop the multiplier** and treat `Config.EndOfDayTimeOut` as a tick count
  directly: `if (shippingMenuTimeoutTicks >= Config.EndOfDayTimeOut)`. With the
  current default that is `120000` ticks = 2000s (~33 min) at 60 TPS, but scales
  inversely with TPS — at `SERVER_TPS=5` it becomes ~6.7 hours, which is still
  effectively never. So a raw tick count couples the wall-clock timeout to TPS.
- **Convert to a wall-clock duration** like the festival path: compare elapsed
  *seconds* against `TicksToSeconds(Config.EndOfDayTimeOut)`, so the timeout is
  TPS-independent. Preferred — it matches `AlwaysOnFestivals` and keeps the
  shipping-menu window stable regardless of `SERVER_TPS`.

Either way, **revisit the default**. A sane shipping-menu timeout is on the order
of minutes (e.g. ~2000s / 33 min), not 33 hours. If switching to the
seconds-based form, set `EndOfDayTimeOut` to a value that `TicksToSeconds`
renders to that target (e.g. `120000` → 2000s).

Note: `Config.EndOfDayTimeOut != 0` is the disable guard (`:375`), so keep `0`
meaning "no timeout" whichever form is chosen.

## Verification

Per `runtime-post-conditions-are-gates.md` (a timeout that "should fire" is a
runtime observation, not a static fact):

1. Drive the host into a stuck `ShippingMenu` (e.g. a save that does not advance
   past the menu) with `IsAutomating` true.
2. Confirm `setServerMode("offline")` is invoked within the configured wall-clock
   window — measure against `TicksToSeconds(Config.EndOfDayTimeOut)`, not 33h.
3. Confirm the `Game1.timeOfDay == 610` reset still flips the server back
   `online` and clears `shippingMenuTimeoutTicks` on a normal (non-stuck) day, so
   the fix does not falsely trip during ordinary day transitions.
4. Run at `SERVER_TPS=5` (the CI/test value) to confirm the timeout window does
   not change with TPS (only relevant if the seconds-based form is chosen).

## Related files

| File | Role |
| --- | --- |
| `Services/AlwaysOnServer/AlwaysOn.cs:375-394` | `OnUnvalidatedUpdateTick` — the broken `* Env.ServerTps` comparison + the `timeOfDay == 610` reset |
| `Services/AlwaysOnServer/AlwaysOn.cs:359-370` | `OnSaving` — sets `_isShippingMenuActive = true` |
| `Services/AlwaysOnServer/AlwaysOnConfig.cs:38` | `EndOfDayTimeOut = 120000` default |
| `Env.cs:35` | `ServerTps` default 60, `Math.Max(1, …)` floor |
| `Services/AlwaysOnServer/AlwaysOnFestivals.cs:182` | `TicksToSeconds` — the pattern the festival timeouts already use; mirror it here |
