---
paths:
  - "docker/modern/**"
---

# Modern Docker image — musl gotchas

The Alpine/musl-based server image needs specific compatibility shims that don't apply to glibc Debian images. Stack/architecture reference: [`docs/admins/operations/modern-docker.md`](../../docs/admins/operations/modern-docker.md).

## musl-specific invariants

- **Steam SDK shims**: needs `pthread_shim.so`, `steamclient_stub.so`, and AuthService try/catch.
- **musl detection**: `[ -f /lib/ld-musl-x86_64.so.1 ]`. Do NOT use `ldd --version | grep musl` — it doesn't work on Alpine.
- **MonoGame Threading namespace**: `Microsoft.Xna.Framework.Threading` (NOT `MonoGame.Framework.Threading`).

## SMAPI `RunSynchronously` deadlock on musl

`task.RunSynchronously()` queues to ThreadPool instead of running inline on musl. When the task needs `BlockOnUIThread()` (texture loading), classic deadlock.

**Fix**: Harmony-patch `SModHooks.StartTask` to use `task.Start()` for content-loading tasks. Find `SModHooks` via assembly scan (string-based `AccessTools.Method` fails). **Do NOT patch `BlockOnUIThread`** — the GL context is thread-local, so the patch can't work.

**Why:** Each rule is anchored to a real bug encountered while bringing up the modern image. The detection gotcha was a multi-hour wrong turn before the file-existence check; the `RunSynchronously` deadlock surfaces as a hung server with no useful log line, and the `BlockOnUIThread` patch attempt was a dead end.

**How to apply:** When touching `docker/modern/`, treat each invariant as load-bearing. If something else looks broken on the modern image, check whether it's another musl-specific divergence before reaching for a glibc-style fix.
