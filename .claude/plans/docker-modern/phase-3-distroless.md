# Phase 3: Switch to a distroless base

## Goal

Once nothing in the runtime path invokes a shell (phase 2 complete), swap the runtime base to a
distroless glibc image for maximum hardening: no shell and no package manager in the running
container. By this point it is a near-mechanical base swap plus copying the traced library closure —
not a rewrite.

## Why distroless is a hardening choice, not a size or CVE win

Measured on this workload, distroless barely moves size or CVE:

- **Size is dominated by payload, not base.** Every base is 7–9 MB (distroless/cc ≈ 9 MB, Wolfi ≈ 7,
  Debian slim ≈ 29). The image is filled by the .NET runtime (~67 MB), the X/Mesa/graphics stack,
  and — until phase 1's LLVM cut — a 183 MB LLVM. The base swap saves single-digit MB.
- **CVE is dominated by payload, not base.** Distroless/cc and Wolfi both scan clean at zero. The
  CVEs in the final image come from .NET, Mesa, X, and ffmpeg — identical whichever base. Distroless
  gives no CVE edge over a minimal glibc base.

What distroless uniquely gives is **attack surface**: no shell, no package manager to exploit or to
live-off-the-land with. That is the reason to do phase 3, and it's only reachable because phase 2
removed the shell dependencies.

## Entry gate (from phase 2)

Do not start until all of these hold:

- [ ] No runtime process reads the SMAPI stdin FIFO; command input is over HTTP.
- [ ] SMAPI download/install is a binary, not curl+unzip+shell.
- [ ] All s6 run-scripts are execline, no `/bin/bash -c` bodies.
- [ ] Boot glue and env validation need no shell.
- [ ] The interactive CLI runs as a client, not inside the server container.

Plus an explicit sign-off that losing `docker exec sh` is acceptable (see below).

## What to build

1. **Pick the distroless variant: `cc`, not `base`.** The .NET runtime (`libcoreclr`) and
   `steamclient.so` both need `libstdc++` and `libgcc`, which the `cc` variant includes and `base`
   does not.
2. **Keep the glibc line consistent with phase 1.** Phase 1 is Debian 13 / glibc 2.41 (which needs
   `ExecstackPatcher`). Choose the distroless base to match that glibc line; if it lands on an older
   glibc, the execstack handling behaviour shifts and you lose parity with what phases 1–2 validated.
   Decide this consciously.
3. **Multi-stage: assemble on a full glibc distro, copy the closure into distroless.** Build and
   gather everything on a distro with packages (Debian, or Fedora which packages the X/Mesa/PipeWire
   stack well), then `COPY` the traced runtime library closure — the game, SMAPI, .NET, Xvfb, Mesa
   Zink, SwiftShader, the Vulkan loader, and their transitive `.so` dependencies — into the
   distroless runtime stage. Trace the closure with `ldd` over every binary; a missing transitive
   dependency fails at runtime, not build (see `.claude/rules/universal/runtime-post-conditions-are-gates.md`).
4. **Supervision stays s6-overlay.** s6 and execline are shell-free binaries and run fine in
   distroless; phase 2 already converted the run-scripts to execline.

## The ongoing cost to accept

Distroless has no package manager, so the copied library set is maintained by hand — there is no
`apt`/`apk upgrade` to refresh Mesa, .NET, or the X libraries when a CVE lands. That maintenance
moves to rebuilding the builder stage and re-copying. This is the real tax of distroless for a
dynamically-linked workload like this; weigh it against the hardening benefit.

## The ops change to accept

No shell means no `docker exec sh` into the running server. Debugging moves to logs and the HTTP API.
If an interactive CLI is wanted, it runs as a separate shell-having sidecar (or a local tool) that
talks to the server over HTTP and the shared log volume — the server container itself stays
shell-free. Make this an explicit sign-off, not a phase-3 surprise.

## Done when

- The runtime image is distroless/cc (glibc matching phase 1).
- The traced library closure is complete and the image boots, serves, and a client joins over SDR.
- The smoke test from phase 1 still passes against the distroless image.
- No shell or package manager is present in the runtime image.
