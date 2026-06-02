---
paths:
  - "mod/JunimoServer/**"
  - "mod/JunimoServer.Shared/**"
---

# To render Stardew into a small framebuffer, zoom out via the patched `desired*` getters — don't poke the scale fields

Making the game fit a framebuffer smaller than 1280×720 needs two things: resize the window (`Game1.game1.SetWindowSize(w, h)`) AND zoom the world+UI out by `height / Game1.defaultResolutionY` (so the 1280×720 logical layout downscales into the small backbuffer instead of showing a zoomed-in crop). The zoom must be installed as a Harmony postfix on the `Options.desiredBaseZoomLevel` and `Options.desiredUIScale` getters — returning the target scale in every game mode — NOT by writing the scale fields directly.

**Why:** Cost ~3 failed iterations during the test-resolution work:

1. Setting `singlePlayerBaseZoomLevel` once on `GameLaunched` got reverted ~10s later: it's a persisted option (`[XmlElement("zoomLevel")]`, `Options.cs:278`), and the save load resets it to 1.0.
2. A field-poke + per-`UpdateTicked` re-pin produced a visible "too big then snaps smaller" flicker, and menus stayed oversized — because `Options.desiredUIScale`'s getter hard-returns `1f` when `Game1.gameMode != 3` (`Options.cs:507`), and `Game1.Update`'s per-tick reconciliation (`Game1.cs:3743-3757`) forces `baseUIScale`/`baseZoomLevel` back to the `desired*` values every frame, fighting the poke.
3. The working fix postfixes both `desired*` getters to return the target (when scale ≠ 1), so the game's own reconciliation drives `baseZoomLevel`/`baseUIScale` to it in every mode and calls `refreshWindowSettings()` itself — revert-proof, no re-pinning.

The render math: `screen` RT is sized `window/zoomLevel` then drawn back at `scale=zoomLevel` (`Game1.cs:2517, 14407`), so `zoomLevel < 1` zooms *out*. Verified visually down to 320×180 (scale 0.25): title/customization/lobby menus and gameplay all scale, `(0,0)` overlay pixel stays white, full suite green. The `<1280/<720` floor in `SetWindowSize` (`Game1.cs:2436`) is Windows-only, so Linux containers resize freely.

**How to apply:** When a mod needs to change zoom or UI scale persistently (display sizing, headless rendering), don't write `singlePlayerBaseZoomLevel`/`baseUIScale`/`desired*` and expect it to stick — `Game1.Update` reconciles them from the `desired*` getters every tick, and persisted-option fields get clobbered on save load. Harmony-postfix the getter(s) so the game reads your value as source of truth; let its own `refreshWindowSettings()` reconciliation apply it. Same shape as `netfield-revert-pattern.md` (patch the source of the revert, don't fight it tick-by-tick) but a different mechanism: per-tick option reconciliation, not NetField interpolation. Always verify the result by inspecting a recorded frame at the target resolution — the symptom ("content too big") is visual, so the gate must be too.
