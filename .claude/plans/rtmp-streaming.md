# Plan: Live-stream the server's view to YouTube / Twitch (RTMP)

## What this is

A first-iteration feature to broadcast what the server host renders to an RTMP target
(YouTube Live, Twitch). "As-is" means exactly the game view the server draws — the same
1280x720 X display the modern image already captures for WebRTC — with no overlays, no
scene composition, and no per-viewer interaction.

This is a **modern-image-only** feature. The classic production image ships only TigerVNC
(`docker/rootfs/etc/services.d/xvnc/run`) and has no ffmpeg, go2rtc, or audio stack. The
modern image (`docker/modern/`) already captures the X display with ffmpeg and runs go2rtc,
so it is the only place this rides. It therefore depends on the modern image reaching a
deployable state (see `.claude/plans/docker-modern/`); it does not depend on any specific
phase of that roadmap.

## Why this is small

Most of the pipeline already exists in the modern image:

- The capture recipe is proven: `ffmpeg -f x11grab -video_size 1280x720 -framerate 30 -i :99
  ... -tune zerolatency` in `docker/modern/rootfs/etc/go2rtc/config.yaml`, and the same
  x11grab->libx264 shape in the test recorder (`tests/JunimoServer.Tests/Helpers/ContainerRecorder.cs`).
- An s6 service already owns "capture the display": the `streaming` service
  (`docker/modern/rootfs/etc/s6-overlay/s6-rc.d/streaming/run`) waits for X on `:99` and
  launches go2rtc.

What's missing is only: an RTMP egress process, a silent audio track, config plumbing, and
turning rendering on. No new capture technology.

## What to build

1. **A dedicated ffmpeg egress process, not a go2rtc RTMP push.** go2rtc stays as-is for
   low-latency WebRTC viewing (`:8555`/`:1984`); a separate long-running ffmpeg reads the
   same `:99` display and pushes FLV to the RTMP ingest. This is the simplest, fully-standard
   path (`ffmpeg -f x11grab -i :99 ... -f flv rtmp://<ingest>/<key>`) and avoids coupling the
   broadcast to go2rtc's browser-viewing config. Run it as its own s6 service (mirror the
   `streaming` service's X-readiness wait and `xvfb` dependency), gated off by default.

2. **A silent audio track for iteration one.** RTMP ingests (YouTube especially) expect an
   audio stream. The modern image's PipeWire sink is a null sink and nothing captures game
   sound, so v1 injects silence: `-f lavfi -i anullsrc=r=44100:cl=stereo` encoded to AAC
   alongside the video. Capturing real game audio from a PipeWire monitor source is explicit
   future work, not v1.

3. **Require rendering to be on, and fail loud if it isn't.** `SERVER_FPS` defaults to `0`
   (`mod/JunimoServer/Env.cs`), at which the server installs a `NullDisplayDevice` and paints a
   static "Rendering Disabled" frame (`mod/JunimoServer.Shared/RenderingController.cs`) — so a
   naive stream would broadcast a black screen. The stream service must not silently ship that:
   when streaming is enabled, treat `SERVER_FPS=0` as a misconfiguration and log a clear error
   (rendering can also be toggled at runtime via the `/rendering` API and `RenderingCommand`,
   but the deploy-time default must be `SERVER_FPS>0`).

4. **Config plumbing following the existing conventions.** Per
   `.claude/rules/deployment-config-is-env-not-settings-file.md`, operator knobs are
   feature-first, unprefixed env vars in `docker/modern/docker-compose.yml`:
   - `STREAM_ENABLED` (default `false`) — the on/off gate.
   - `STREAM_RTMP_URL` — the ingest base URL (e.g. `rtmp://a.rtmp.youtube.com/live2`). Not a
     secret.
   - `STREAM_KEY` — the stream key. This **is** a secret; wire it through the compose `secrets:`
     block like `steam_username`/`steam_password` already are, not as a plain `environment:`
     value, and have the deploy write it from a secret (not a `vars.*`).

   Document the two non-secret knobs where operators find the rest: the modern `.env` example
   and `docs/admins/configuration/environment.md`. Each documented knob is a contract, so land
   the docs with the consumer, not after.

## Constraints and costs to accept

- **Rendering CPU.** The whole server design is headless (`SERVER_FPS=0`). Streaming forces
  real per-frame draw + H.264 encode, which is the actual ongoing cost of this feature — not a
  blocker, but the tradeoff to state up front. Measure encode load at the chosen fps before
  quoting numbers.
- **One resolution/fps.** v1 streams the native 1280x720 at whatever `SERVER_FPS` is set to; no
  transcoding ladder, no separate broadcast resolution.
- **No audio content.** v1 is silent. This is a deliberate scope cut, called out so a viewer's
  "why no sound" is answered by design, not by a bug hunt.

## Explicitly out of scope for v1

- Real game audio in the stream.
- Overlays, scene composition, or a branded frame.
- Multi-destination / simulcast.
- Backporting any of this to the classic image.

## Done when

- With `STREAM_ENABLED=true`, `STREAM_RTMP_URL`, `STREAM_KEY`, and `SERVER_FPS>0`, the modern
  image goes live on the target platform showing the actual game view.
- With `STREAM_ENABLED=false` (default), no egress process runs and behaviour is unchanged.
- With streaming enabled but `SERVER_FPS=0`, the service logs a clear rendering-off error rather
  than silently streaming the "Rendering Disabled" frame.
- go2rtc WebRTC viewing still works unchanged alongside the RTMP egress.
