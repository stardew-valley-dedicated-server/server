# Modern Docker image (`docker/modern/`) — reference

An alternative server image based on Alpine/musl. Contributor-facing musl gotchas live at [.claude/rules/modern-docker.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/modern-docker.md).

Status: WIP / experimental. Not finished, not fully tested, and not in production use — the production image remains `docker/Dockerfile` (Debian/glibc). It is not built by the Makefile or CI; it builds manually via `docker/modern/docker-compose.yml`.

## Stack

- Base: Alpine:edge
- Display: Xvfb + Openbox
- Audio: PipeWire
- Streaming: WebRTC (go2rtc)
- Init: s6-overlay

## Image size

- ~821 MB total (vs ~960 MB for the existing Debian image).
- 465 MB of that is Mesa/LLVM for software OpenGL.

## Architecture choices

- Game files are **not** baked in — runtime download via the steam-auth shared volume.
- **Wayland/Cage abandoned**: no OpenGL in headless Docker without a GPU. Use Xvfb + Openbox instead.
- **Openbox required**: without a window manager, window resize during startup crashes (null Farmer).
