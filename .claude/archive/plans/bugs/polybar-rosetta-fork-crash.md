# Server container aborts on ARM hosts: polybar dies on any fork under Rosetta → supervisor tears the container down

Status: root-caused + fix planned (this plan). Not implemented yet.

## Incident

First run against a remote Apple-Silicon host (`.env.test` fleet = single `mac` entry,
OrbStack daemon, `docker_preflight` reports `architecture: aarch64`, amd64 images run
under Rosetta). Every server container exited with code 1 before the game ever started;
all tests failed/canceled as infrastructure (run `2026-07-12T00-22-07Z_3d5be35`:
0 passed / 32 failed / 118 canceled, `failure.json` on server-0..5).

Container log (identical on every server-N):

```
[supervisor    ] starting service 'polybar'...
[supervisor    ] service 'polybar' failed to be started: not ready after 10000 msec, giving up.
[supervisor    ] stopping service 'polybar'...        ← teardown of everything follows
```

The `gpu` host flag is unrelated (tried both). client-0 was unaffected (test-client
image has no polybar service).

## Root cause chain

1. `services.d/gui/polybar.dep` gates the `gui` service (game startup) on polybar
   readiness; a not-ready service makes the jlesage supervisor abort the container.
   (This also disproves `.claude/rules/startup-cold-start-measurement.md:18` —
   "polybar + resize-handler … don't gate startup". They do gate it, fatally.)
2. `services.d/polybar/is_ready` = `pgrep polybar`. It stayed false for the whole 10s
   window because polybar dies ~instantly after launch, every respawn.
3. **polybar 3.5.5 crashes with a spurious "Termination signal received, shutting
   down..." the moment it forks any child process under Rosetta** (multithreaded
   translated process + fork → stray SIGINT/TERM/QUIT to the parent). Our bar config
   includes `module/taskbar` (`custom/script` → `taskbar.sh`), which forks at startup.

Bisection evidence (all reproduced directly on the mac, `sdvd/server:local`,
Xvnc up, with and without openbox):

| Variant | Result |
|---|---|
| Full config (taskbar module) | DEAD < 3s, "Termination signal received" |
| Minimal config, `internal/date` only | ALIVE |
| Full config minus taskbar (launcher / rendering-toggle / date) | ALIVE |
| Full config, taskbar → trivial `custom/script` `exec = echo hi` | DEAD — **any fork kills it** |
| `taskbar.sh` standalone (no polybar) | fine — the script is innocent |

Click actions also fork, so even a "fixed" bar would crash on every button press
under Rosetta. Combined with the bar being pure eyecandy, the decision is to
**remove polybar and the surrounding GUI garnish entirely** rather than patch around it.

## Pixel-safety evidence

Frames extracted from a healthy local recording
(`runs/2026-06-30T19-28-04Z_864566d/containers/server-0/full_recording.mp4`, frame 5
and last-3s) show the game window at 0,0 filling the entire 640×360 display. Polybar,
wallpaper, and openbox margins are never visible in recordings — no test, recorder,
or validator can depend on them (`grep` of tests/tools: zero hits). Removal only
uncovers game pixels.

## Fix inventory

### Delete

- `docker/rootfs/etc/services.d/polybar/` (run, is_ready, respawn, disabled)
- `docker/rootfs/etc/services.d/resize-handler/` + `opt/base/bin/resize-handler.sh` —
  it never performed the VNC resize itself (TigerVNC/xrandr handles that natively);
  its only jobs were "restart polybar" and "re-apply wallpaper" on resize, both dead
  after this cleanup
- `docker/rootfs/etc/services.d/gui/` — our overlay dir only holds `polybar.dep` +
  `resize-handler.dep`; the base image's own `gui` service is untouched
- `docker/rootfs/root/.config/polybar/` (config.ini, user_modules.ini)
- `docker/rootfs/opt/base/bin/taskbar.sh`
- `docker/rootfs/opt/base/bin/toggle-rendering.sh` — only caller was the bar's click
  button; function survives via `POST /rendering?fps=N` or
  `echo "rendering 10" > /tmp/smapi-input`
- `docker/rootfs/data/images/wallpaper-junimo-server.png`, `wallpaper-sdv.png`
  (`junimo.png` stays — WebVNC icon)
- `docker/rootfs/opt/base/etc/openbox/keybinds.xml` (contains only commented-out rofi
  binds) + its `xi:include` in `rc.xml.template:564`

### Edit

- `docker/Dockerfile`
  - apt line: drop `polybar`, `rofi`, `slop`, `wmctrl`, `xwallpaper`, `exa`, `scrot`,
    `x11-utils` (only consumers were taskbar.sh/resize-handler.sh xprop/xev/xrandr;
    smoke test confirms the base image doesn't need it)
  - drop the whole "Install polybar theme" section (adi1090x clone, shades configs,
    fantasque/iosevka fonts — all polybar-only)
- `docker/rootfs/startapp.sh` — delete `init_gui()` (polybar comment block,
  `colors-dark.sh` theme call, wallpaper) and its call site
- `docker/rootfs/opt/base/etc/openbox/rc.xml.template` — remove the reserved-margins
  block (17px bottom/left/right + "Top is controlled by polybar" comment); margins are
  inert for the fullscreen game window
- `.claude/rules/startup-cold-start-measurement.md:18` — the "polybar +
  resize-handler … don't gate startup" claim is wrong and becomes moot; fix the line

### Keep (verified consumers)

| Package | Consumer |
|---|---|
| `curl` | image `HEALTHCHECK`, startapp.sh |
| `tcpdump` | in-image `netdebug` tool spawns it |
| `htop`, `nano` | operator-shell utilities (explicitly retained; cheap) |
| `procps` | `pgrep` in base scripts |
| locales + CJK cont-init font | game fonts (`chat-font-language-tag.md`) |
| tmux, ffmpeg, mesa-utils, TigerVNC | CLI attach / recording / rendering |

## Verification

1. `make build-server` locally; then artifact check per
   `verify-edit-landed-in-artifact.md`: `docker create` + `docker cp` — confirm
   `/etc/services.d/polybar` is absent from the image (rootfs COPY is server-image only).
2. Smoke on the mac: run the image; supervisor must reach `app`/`gui` with no 10s
   polybar abort; xvnc/openbox up.
3. Full loop: `make test FILTER=<one small class>` against the mac fleet.

## Expectation

This removes the container-fatal blocker on ARM hosts. Whether Stardew + SMAPI itself
runs correctly under Rosetta is the next unknown — step 3 above is where it surfaces.

## Related, not in this plan

Image-build failures surface poorly in the CLI: build output streams as
`SetupStepStatus.InProgress` (spinner-only in `CIRenderer`), and the thrown
`InvalidOperationException` carries only the exit code, so the operator sees
`Building server image failed with exit code 2.` without the underlying
`docker buildx` error line (and `make`'s `*** Error` trailer is noise when it does
appear). Fix separately in `DockerImageBuilder.RunBuildCommand`: keep a bounded tail
of build output, filter `^make(\[\d+\])?: \*\*\*` lines, include the tail in the
exception message.
