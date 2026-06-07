# Bug: VNC input dead whenever automation is on — input gate conflates perf-suppression with the F9/F10 escape hatch

## Symptom (user report)

VNC shows the game but accepts no input, no matter what. Operator set `SERVER_FPS` (rendering visibly on), tried the (now-removed) `DISABLE_RENDERING`, tried multiple browsers — input never registers. Goal was to drive the UI over VNC (e.g. start a new farm to pick a modded `FarmType`).

## Root cause

The input gate `SuppressInput_Prefix` (`mod/JunimoServer/Services/ServerOptim/ServerOptimizerOverrides.cs:66-67`) is:

```csharp
public static bool SuppressInput_Prefix()
    => _rendering.CurrentFps != 0 && !_automationSuppressesInput;
```

and it is patched **unconditionally onto all three** input methods (`ServerOptimizer.cs:99-112`):

- `StardewValley.Game1:UpdateControlInput` — game-side input processing (reads `Game1.input`, the `InputState` layer).
- `Microsoft.Xna.Framework.Input.Keyboard:PlatformGetState` — raw device.
- `Microsoft.Xna.Framework.Input.Mouse:PlatformGetState` — raw device.

`InputState.UpdateStates()` (`decompiled/.../StardewValley/InputState.cs:25-37`) feeds **both** the game and SMAPI from `Keyboard.GetState()` / `Mouse.GetState()` → `PlatformGetState`. So suppressing at the device layer blinds **SMAPI's `Input.ButtonPressed` pipeline too** — including F9/F10. Since `EnableAutoMode()` sets `_automationSuppressesInput = true` on every save load (`AlwaysOn.cs:150, 388-395`), the device layer is dead whenever automation is on. **The F9 "toggle automation off to restore input" recovery path is self-defeating: F9's own delivery channel is suppressed.** Only the off-VNC `host-auto` console command can toggle it.

## Regression origin

Commit `c7dbf85` ("feat(mod): extract JunimoServer.Shared and add diagnostics tracing") merged the previously-separate input prefixes into one and changed the device-layer patches from *conditional* to *unconditional + automation-driven*. Pre-refactor (`c7dbf85^:ServerOptimizer.cs:89-107`):

- `UpdateControlInput` was **always** patched with `UpdateControlInput_Prefix` (live-gated on rendering + automation) — game-side only.
- `Keyboard`/`Mouse` `PlatformGetState` were patched with `Disable_Prefix` (hardcoded `return false`) **only if `_disableRendering` was true at boot** (`if (_disableRendering)`), read once from the now-removed `Env.DisableRendering`.

So in the old "boot rendering-enabled, drive via VNC" path, the device layer was **never patched**, SMAPI kept seeing keys, and F9 worked. The escape hatch existed because device-layer suppression and the live automation gate were decoupled. The refactor coupled them.

## Design intent (from the user)

- Rendering on/off via `.env` `SERVER_FPS` (0 = off / perf mode, N>0 = on), independently changeable at runtime via the existing `rendering <fps>` console command and `POST /rendering?fps=N` — no regression to that surface.
- **Rendering OFF**: input has no logical meaning (operator can't see anything to drive) → suppress fully. This is the performance optimization AND the accidental-interference guard, together.
- **Rendering ON**: operator can see → the F9 (auto-mode) and F10 (visibility) toggles must work seamlessly over VNC; general input works once the operator drops automation via F9. **Pressing F9/F10 IS the explicit intent** — no new command/endpoint.
- Retain the existing server optimizations (NullDisplayDevice swap, `Disable_Prefix` on music/butterflies/etc.). Only the input gate changes.

## The model

The device layer (`Keyboard/Mouse:PlatformGetState`) is the **single source** feeding every input consumer. Verified against decompiled source: `Game1._update` captures `GetKeyboardState()`/`input.GetMouseState()` once (`Game1.cs:4004-4006`) and routes that *same* state to free-roam control (`UpdateControlInput`, only when no menu is open — `Game1.cs:4287`), menus (`updateActiveMenu`), text entry (`updateTextEntry`), minigames, and chat. SMAPI's `Input.ButtonPressed` reads the same source independently: `SCore` update loop → `SInputState.TrueUpdate()` → `base.GetKeyboardState()` → `InputState._currentKeyboardState`, set by `UpdateStates()` → `Keyboard.GetState()` → `PlatformGetState`.

So there is exactly one chokepoint, and the fix lives there: **suppress all input at the device layer, carving out only F9/F10 so SMAPI's `ButtonPressed` still fires for them.** No game-side `UpdateControlInput` patch is needed — when the device returns empty state, every game consumer (control AND menus AND chat) sees nothing; only SMAPI's handler sees the carve-out keys.

Suppression is active whenever `fps==0` **or** automation is on:

| Render state | Keyboard device returns | Mouse device returns | UI interaction (control + menus + chat) | F9/F10 |
|---|---|---|---|---|
| `fps==0` (perf, default) | `default` (nothing) | `default` | blocked | inert (operator can't see anyway) |
| `fps>0`, automation **on** (default after save load) | only F9/F10 if physically down | `default` | **blocked** | **fire** (SMAPI sees them) |
| `fps>0`, automation **off** (operator pressed F9) | full real state | full real state | works | fire |

Why this satisfies every requirement:

- **fps==0 (perf mode):** device returns empty → zero input cost, full guard, F9/F10 inert (nothing to see). Matches "input off makes no logical sense when rendering off." Identical to today's suppression behavior at fps 0.
- **fps>0, automation ON:** device returns *only* F9/F10 → all keyboard/mouse UI interaction is blocked (you cannot click menus, type in chat, or move the character over VNC), but SMAPI sees F9/F10 → the toggles fire. This is the accidental-interference guard, complete: view-only except the two hotkeys we explicitly set up.
- **fps>0, automation OFF (operator pressed F9):** device passes full real state → seamless control over VNC.

SMAPI's `ButtonPressed` reading the device layer independently of `UpdateControlInput` is confirmed at source (`SInputState.TrueUpdate` → `base.GetKeyboardState` → `_currentKeyboardState` ← `PlatformGetState`), and corroborated by the pre-refactor behavior: that code skipped `UpdateControlInput` entirely in the rendering-enabled path yet F9 worked over VNC — the only unsuppressed thing there was `PlatformGetState`.

## Changes

### 1. Replace the prefix with device-layer postfixes (`ServerOptimizerOverrides.cs`)

The keyboard and mouse `PlatformGetState` methods return different types (`KeyboardState` / `MouseState`), so each needs its own postfix — there is no shared body to factor out. Suppression is `fps==0 || _automationSuppressesInput`. Keep `SetAutomationInputSuppression` / `_automationSuppressesInput` as-is (toggled by `EnableAutoMode`/`ToggleAutoMode`/`OnReturnedToTitle`).

The carve-out keys are the configured host-automation hotkeys, not hardcoded. `AlwaysOnConfig.HotKeyToggleAutoMode`/`HotKeyToggleVisibility` are `SButton`; convert to `Keys` once at `Initialize` via `SButtonExtensions.TryGetKeyboard`. Inject `AlwaysOnConfig` into `ServerOptimizer` (a DI singleton, `ModEntry.cs:180-181`) and pass the keys through to `Initialize`.

```csharp
private static Keys[] _carveOutKeys = Array.Empty<Keys>();

public static void Initialize(IMonitor monitor, AlwaysOnConfig config)
{
    _monitor = monitor;
    _rendering = new RenderingController(monitor, "Server");
    _carveOutKeys = new[] { config.HotKeyToggleAutoMode, config.HotKeyToggleVisibility }
        .Where(b => b.TryGetKeyboard(out _))
        .Select(b => { b.TryGetKeyboard(out var k); return k; })
        .ToArray();
}

private static bool InputSuppressed
    => _rendering.CurrentFps == 0 || _automationSuppressesInput;

// Keyboard/Mouse PlatformGetState is the single source for the game (control, menus,
// chat, minigames) AND SMAPI's ButtonPressed pipeline. While suppressed, strip keyboard
// state to the host-automation hotkeys so all other input is blocked but those toggles
// still reach SMAPI — the operator's only way to drop automation from the VNC display.
public static void KeyboardState_Postfix(ref KeyboardState __result)
{
    if (!InputSuppressed) return;
    var down = Array.FindAll(_carveOutKeys, __result.IsKeyDown);
    __result = down.Length == 0 ? default : new KeyboardState(down);
}

public static void MouseState_Postfix(ref MouseState __result)
{
    if (InputSuppressed) __result = default;
}
```

The `UpdateControlInput` patch is removed — with the device returning empty state, every consumer sees no input, so a separate game-side gate is redundant.

### 2. Re-point the patches (`ServerOptimizer.cs:99-112`)

```csharp
harmony.Patch(
    original: AccessTools.Method("Microsoft.Xna.Framework.Input.Keyboard:PlatformGetState"),
    postfix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.KeyboardState_Postfix)));

harmony.Patch(
    original: AccessTools.Method("Microsoft.Xna.Framework.Input.Mouse:PlatformGetState"),
    postfix: new HarmonyMethod(typeof(ServerOptimizerOverrides), nameof(ServerOptimizerOverrides.MouseState_Postfix)));
```

Drop the `Game1:UpdateControlInput` patch. Inject `AlwaysOnConfig` into the `ServerOptimizer` constructor and pass it to `ServerOptimizerOverrides.Initialize`. Rewrite the block comment above (`ServerOptimizer.cs:94-98`) for the single device-layer carve-out. Add `using Microsoft.Xna.Framework.Input;` and `using StardewModdingAPI;` (for `SButtonExtensions.TryGetKeyboard`) to `ServerOptimizerOverrides.cs`.

### 3. Docs

- `docs/admins/configuration/environment.md` (~97-101): while rendering is on, the VNC view is input-blocked except **F9** (toggle host automation) and **F10** (toggle visibility); press F9 to drop automation and gain full keyboard/mouse control, press it again to re-arm the guard. Input is fully suppressed while rendering is off.
- `docs/admins/operations/commands.md` (host-auto section ~104-110): note F9 is the in-VNC equivalent of `host-auto` and works over VNC whenever rendering is on, even while automation is suppressing all other input.

## Out of scope (named, not silently dropped)

- **Modded `FarmType` / new-farm creation** (the user's end goal). That is a separate concern: the headless new-game path resolving a mod-added farm type by index (`"FarmType": 8`). Not touched here — file/triage separately. This plan only restores the *ability to drive the UI over VNC*, which is the prerequisite the user was blocked on.
- **Test-client**: uses `RenderingController` for draw control only; does not patch input or use `ServerOptimizerOverrides` (`tests/test-client/ModEntry.cs:53,136`). Untouched.

## Compatibility verification

- **Save-load flow:** `EnableAutoMode` still sets `_automationSuppressesInput=true`. With rendering on, the keyboard postfix strips state to F9/F10 → all UI interaction blocked (movement guard preserved — matches the intentional move-freeze in `misc-triage-bugs.md`), SMAPI still sees the toggles. With rendering off, fully suppressed.
- **Perf mode (fps==0, the production default):** `InputSuppressed` is true (the `fps==0` term), keyboard returns `default` (carve-out keys can't be physically pressed on a headless host with no VNC viewer anyway), mouse `default` → identical to today's full suppression. **No perf regression.** Postfix cost is one `Array.FindAll` over a 2-element array per device read — negligible, and only when suppressed.
- **Runtime toggle:** `InputSuppressed` reads `_rendering.CurrentFps` and `_automationSuppressesInput` live every call, so `rendering <fps>` / `POST /rendering?fps=N` and `host-auto`/F9 flip input behavior with no restart.
- **LAN vs Steam / lobby / unauthenticated players:** input gating is host-local (VNC operator only); does not touch netReady, lobby exclusion, or transport. No interaction.
- **F9/F10 delivery:** confirmed at source — SMAPI's `SInputState.TrueUpdate` reads `base.GetKeyboardState()` ← `_currentKeyboardState` ← `PlatformGetState` (our postfix runs there), independent of `UpdateControlInput`. The carve-out keys survive into the state SMAPI derives `ButtonStates` from.
- **F10 vanilla binding:** `multiplayer.StartServer()` at `Game1.cs:13289` is inside `UpdateControlInput` and gated `server == null`; inert on a running server. Letting F10 through the device layer triggers no vanilla side effect.

## Post-conditions (runtime gates — must be observed, not just compiled)

1. Boot with `SERVER_FPS=10`, save auto-loads (automation on): over VNC, **F9 toggles automation off** (visible HUD "Host automation: Disabled"), then keyboard/mouse drive the host. **F10 toggles visibility.**
2. With automation on + rendering on: keyboard/mouse do **nothing** over VNC — cannot move the character, click menus, or type in chat — but F9/F10 still fire.
3. `rendering 0` at runtime: input goes dead. `rendering 10`: F9/F10 live again — no restart. Toggling `host-auto` off then on flips between full-input and F9/F10-only without restart.
4. Boot with `SERVER_FPS=0` (default): no input processed (perf mode), unchanged from current production behavior.
