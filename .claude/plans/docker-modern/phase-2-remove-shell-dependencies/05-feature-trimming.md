# Task 2.5: Trim features that aren't needed (optional, reversible)

## Goal

Optionally drop runtime features that aren't required, each of which removes packages, image size,
and CVE surface. These are independent, reversible decisions — none is a prerequisite for distroless,
but each makes the final image smaller and cleaner. Decide each on its own merits.

## Candidates

### Screen streaming (go2rtc + ffmpeg)

`go2rtc` and `ffmpeg` exist to stream the server's screen over WebRTC for remote viewing/debugging
(`docker/modern/rootfs/etc/s6-overlay/s6-rc.d/streaming/run`, `docker/modern/rootfs/etc/go2rtc/config.yaml`).
If watching the server's screen isn't needed in production, dropping both removes the go2rtc binary,
ffmpeg, and ffmpeg's historically heavy CVE surface. The game still runs headless into Xvfb without
them. Keep them only if remote screen-viewing is an actual requirement.

### Audio (PipeWire)

PipeWire provides a null audio sink (`docker/modern/rootfs/etc/s6-overlay/s6-rc.d/pipewire/run`). The
game may try to open an audio device; if it tolerates a dummy driver (an SDL dummy-audio setting or
no device) without crashing, PipeWire and its libraries can go. This one needs a boot test to confirm
the game starts cleanly with no audio device before committing.

### Debug tooling

Any interactive debug tools carried for convenience (the tmux CLI is removed in task 2.1; check for
others) are candidates to drop, especially since distroless removes the shell they'd rely on anyway.

## What must stay

The graphics path is not optional: Xvfb, the custom Mesa Zink build, SwiftShader, and the Vulkan
loader. The game is a rendering engine and initializes a graphics device even though the server
suppresses drawing — it needs an X display and a GL/Vulkan stack to start at all. openbox stays too:
per `.claude/rules/modern-docker.md` a window manager is required, or a window resize during startup
crashes the game with a null Farmer.

## Guidance

If a feature is dropped, do it cleanly — remove the s6 service, the packages, and the config — rather
than leaving a disabled service in place. If a feature is genuinely uncertain, keep it and note why,
rather than shipping a half-removed stub.

## Done when

- Each candidate has an explicit keep/drop decision.
- Dropped features are fully removed (service, packages, config), and the image still boots and
  serves.
