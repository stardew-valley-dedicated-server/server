# Phase 1: Rebase the modern image to a Debian glibc base

## Goal

Move the modern image's runtime stage from `alpine:edge` to `debian:13-slim` so that Steam
Datagram Relay works, and delete the family of musl compatibility shims that only exist because
of Alpine. After this phase the modern image can actually be used in production: vanilla Steam
clients can join over SDR, which they cannot on the musl image.

This phase also folds in two things that are cheapest to do while the base layer is already being
rewritten (the LLVM size cut) and one thing that must be true before anyone relies on the image
(a real-client SDR check), plus first-time test coverage so phases 2 and 3 aren't refactoring a
completely untested image.

## Why glibc

On musl, the real `steamclient.so` (Valve's proprietary, glibc-only blob) crashes inside its own
tier1 memory allocator once the Steam engine threads spin up — so the image ships a stub
(`docker/modern/rootfs/opt/lib/steamclient_stub.c`) that makes `GameServer.Init()` fail
gracefully, leaving only Galaxy-invite joins. On glibc the same binary loads and logs on cleanly.
SDR is required for production, so the base must be glibc. Musl is not required for anything.

## Tasks

### 1.1 — Rebase the runtime stage to `debian:13-slim`

Change the final runtime stage in `docker/modern/Dockerfile` (the `FROM alpine:edge AS server`
stage) to a Debian 13 slim base. Translate the `apk add` package set to the Debian equivalents:
the X stack (Xvfb, openbox, the X client libraries), the Vulkan loader, the Mesa runtime
libraries the custom Zink build needs, PipeWire, ffmpeg, ca-certificates, and the .NET runtime
dependency `libicu` (SMAPI fails at startup without ICU — see
`.claude/rules/image-runtime-deps-must-be-explicit.md`).

The `swiftshader-builder` stage already uses a Debian (glibc) base, so its output runs on a glibc
runtime unchanged. The `mesa-builder` stage, however, currently builds on `alpine:edge` (musl) — a
musl-linked Mesa will not load in a glibc runtime, so this stage must also be rebased to a glibc base
(or the Zink runtime libraries sourced from Debian packages). Keep this coordinated with task 1.3:
whichever way Mesa is rebuilt, preserve `llvm=disabled` and make sure no distro Mesa package pulls
LLVM back in.

Carry over the build fix already needed on Alpine: copy `mod/JunimoServer.Shared`'s csproj into
the mod-builder stage for layer caching and drop the `--no-restore` on the mod build.

Verify by booting the image far enough that the .NET/SMAPI process actually starts, not just
container init.

### 1.2 — Delete the musl workarounds

With a glibc base, remove the shims that only exist for musl. Each is dead weight on glibc:

- The steamclient stub and the shell logic that installs it on musl
  (`docker/modern/rootfs/opt/lib/steamclient_stub.c`, and the musl branch of `init_steam_sdk` in
  `docker/modern/rootfs/opt/bin/start-game.sh`). On glibc, link the real `steamclient.so` from the
  game volume instead.
- The pthread symbol shim (`docker/modern/rootfs/opt/lib/pthread_shim.c`) and its `LD_PRELOAD`.
- `gcompat` and the shim-compile step in the Dockerfile.
- The SMAPI `RunSynchronously` musl-deadlock Harmony patch described in
  `.claude/rules/modern-docker.md` (the `SModHooks.StartTask` workaround) — it addresses a
  musl-only ThreadPool behavior.
- The musl detection and try/catch in `mod/JunimoServer/Services/AuthService/AuthService.cs` can
  stay harmless, but its musl branch becomes dead; note it for cleanup.

Update `.claude/rules/modern-docker.md` once these are gone — several of its invariants describe
musl-only behavior that no longer applies. (Follow `.claude/rules/delete-the-plan-when-its-code-lands.md`
for the plan itself when this work merges.)

### 1.3 — Cut the stray 183 MB LLVM

The modern image builds a custom Mesa Zink with `llvm=disabled`, specifically to avoid the ~183 MB
LLVM that llvmpipe drags in. But the runtime package install also pulls a distro Mesa package
(`mesa-gl` on Alpine) whose dependency chain reinstalls the full `llvm` library behind the custom
build's back. The result is the image carrying the exact 183 MB the architecture was designed to
remove — roughly a fifth of the image.

On the Debian rebase, install only the minimal Mesa runtime libraries the custom Zink `.so` links
against, and confirm no installed package pulls `llvm`. Verify the custom `zink_dri.so` is the
driver actually loaded (not a distro llvmpipe), and that no `libLLVM` is present in the final image.
This is a larger size win than the base change itself.

### 1.4 — Keep ExecstackPatcher for glibc 2.41

Debian 13 is glibc 2.41, which refuses to `dlopen` a library with an executable stack. The repo
already solves this with `ExecstackPatcher` (`tools/steam-service/ExecstackPatcher.cs`); make sure
the modern image runs it against the Steam libraries the way the production path does. Do not use
the `glibc.rtld.execstack` tunable — per `.claude/rules/glibc-execstack-dlopen.md` it breaks .NET
stack-bounds detection under emulated amd64. Note: the real `steamclient.so` in the current game
volume already has a non-executable stack, but keep the patcher for whichever library in the chain
needs it and for version drift.

### 1.5 — Validate SDR with a real vanilla Steam client

The SDK-level proof (that `GameServer.Init` and anonymous logon succeed on glibc) is necessary but
not sufficient — it does not prove a player actually joins over the relay. Boot the rebased image
and have a real, unmodified Steam client join the server through SDR (not Galaxy invite, not direct
IP). This is the product-critical outcome the whole phase exists to unlock; treat it as the gate
for calling phase 1 done. Confirm the join reached the server via SDR by inspecting the server log
(`SteamGameServerService` logging the relay network status and the connection), not just by the
client appearing connected.

### 1.6 — Add smoke-test / CI coverage for the modern image

The modern image is not built by the Makefile or CI, and Renovate excludes `docker/modern/` — which
is why its SMAPI version has already drifted behind the production image. Phases 2 and 3 will
refactor this image heavily; without a test it is a blind refactor.

Add at least a smoke test: build the modern image and boot it far enough to confirm the server
process starts, the HTTP health endpoint responds, and (ideally) a client can join. Wire it into CI
so regressions across the later phases are caught. This does not need to be the full E2E suite —
just enough signal that the image still works.

## Definition of done

- The modern image builds and runs on `debian:13-slim`.
- All musl shims are gone from the tree and the image.
- No `libLLVM` in the final image; the custom Zink driver is the one in use.
- A real vanilla Steam client has joined over SDR, confirmed in the server log.
- A smoke test guards the image in CI.

## Not in this phase

Shell removal (phase 2) and distroless (phase 3). Phase 1 keeps the existing shell-based
`start-game.sh` and s6 bash run-scripts — it only changes the base and the Steam/graphics layers.
