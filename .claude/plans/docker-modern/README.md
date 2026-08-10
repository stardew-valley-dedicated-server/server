# Plan: Modern Docker image to production — Debian glibc → shell-free → distroless

## What this is

A staged roadmap to take the experimental modern server image (`docker/modern/`) from
its current Alpine/musl WIP state to a hardened, production-ready image. The work is split
into three phases that ship independently and in order, with risk decreasing at each step.

The modern image's value is its rendering/streaming/init modernization — SwiftShader + Mesa
Zink instead of llvmpipe/LLVM, Xvfb + go2rtc WebRTC instead of TigerVNC, s6-overlay instead
of the jlesage cinit base. None of that depends on musl. Musl was chosen for size, but it is
also the single reason the image cannot do Steam Datagram Relay (SDR), and the reason it carries
a family of compatibility shims. This roadmap keeps the modernization and drops the musl tax.

## The three phases

1. **Debian glibc base** (`phase-1-debian-glibc-base.md`) — rebase the runtime stage from
   `alpine:edge` to `debian:13-slim`. This unlocks SDR (real `steamclient.so` works on glibc)
   and lets the whole musl-workaround family be deleted. Also folds in the LLVM-size fix, a
   real-client SDR check, and first-time test coverage. After this phase the modern image is
   production-viable.

2. **Remove shell dependencies** (`phase-2-remove-shell-dependencies/`) — a series of
   independently-shippable changes that each remove a runtime dependency on a shell: move the
   command path from the SMAPI stdin FIFO to the existing HTTP API, replace the SMAPI
   download/install shell with a small C# tool, convert the s6 run-scripts from bash to execline,
   and move boot-time glue and env validation out of shell. Done on the Debian base, where a
   shell is still present to fall back on for debugging.

3. **Distroless** (`phase-3-distroless.md`) — once nothing in the runtime path shells out,
   swap the base to a distroless glibc image for maximum hardening (no shell, no package
   manager in the runtime). This becomes a near-mechanical base swap plus copying the traced
   library closure, gated by a checklist that phase 2 produces.

## Why this order

- **Phase 1 first** because SDR is required for production and only works on glibc. Until the
  base is glibc, the image cannot ship. It is also the highest-value, lowest-risk single step:
  it deletes more code (the shims) than it adds.
- **Phase 2 is really "remove the shell while you still have one."** The FIFO→HTTP switch, the
  SMAPI C# tool, and the execline conversions are exactly the shell dependencies that block
  distroless. Doing them on Debian — where `docker exec sh` still works for debugging — is what
  de-risks phase 3. Each change ships and is validated on its own.
- **Phase 3 falls out** of phase 2. When the "nothing in the runtime path shells out" checklist
  is empty, flipping the base is low-drama.

## Entry gates

- **Enter phase 2** after phase 1 ships: the modern image runs on Debian glibc, a real vanilla
  Steam client has joined over SDR, and a smoke test guards it.
- **Enter phase 3** when phase 2's checklist is clean — no runtime process invokes a shell, all
  command input is over HTTP, SMAPI install is a binary, and the s6 run-scripts are execline.
  Also an explicit sign-off that losing `docker exec sh` (debug via logs + HTTP API, or a
  separate shell-having sidecar) is acceptable.

## Facts that hold across all phases

- **The `steam-auth` sidecar does not change.** It is already its own glibc container
  (`tools/steam-service/`), separate from the game image. Only the main server image is in
  motion here. The compose topology in `docker/modern/docker-compose.yml` stays the same shape.
- **SDR works on glibc, proven at the SDK level.** With a glibc runtime, `libsteam_api.so` loads
  the real `steamclient.so`, `SteamInternal_GameServer_Init` succeeds, and an anonymous
  GameServer logon completes — the same path that crashes on musl inside Valve's own tier1
  allocator. Real-client join validation is a phase-1 task.
- **glibc version and the execstack requirement.** Debian 13 is glibc 2.41, which refuses to
  `dlopen` executable-stack libraries; the repo already handles this with `ExecstackPatcher`
  (`tools/steam-service/ExecstackPatcher.cs`), see `.claude/rules/glibc-execstack-dlopen.md`.
  Keep the glibc line consistent between phase 1 and phase 3 so this handling doesn't toggle.
- **Image size is dominated by payload, not the base.** Every candidate base is 7–9 MB. The
  image is filled by the .NET runtime, the X/Mesa/graphics stack, and — today — a 183 MB LLVM
  that the SwiftShader+Zink design is supposed to eliminate. The size and CVE wins are in the
  payload (phase 1 LLVM cut, optional feature trimming), not the base choice.

## Status

Not started. The build currently succeeds on Alpine only after the two-line
`JunimoServer.Shared` restore fix in `docker/modern/Dockerfile` (copy the Shared csproj for
layer caching, drop `--no-restore`) — carry that fix into phase 1.
