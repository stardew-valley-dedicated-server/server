---
paths:
  - "docker/**"
  - "tools/steam-service/**"
---

# glibc >= 2.41 refuses to dlopen executable-stack libs — clear the ELF flag, never the tunable

A `DllNotFoundException` for a native lib on a glibc 2.41+ image (Debian 13/trixie) whose file
exists and whose deps all resolve is dlopen refusing the lib's executable-stack marker
(`PT_GNU_STACK` flags RWE). Fix it by clearing the flag on the `.so` at the artifact's single
writer (`ExecstackPatcher` in steam-service); never via `GLIBC_TUNABLES=glibc.rtld.execstack=2`
and never via loader-path env vars.

**Why:** The Debian 13 bump killed Galaxy init with `DllNotFoundException` for
`GalaxyCSharpGlue` while every standard probe pointed away from the cause: `ldd` resolved all
deps (it doesn't dlopen) and `LD_PRELOAD` loaded the lib fine (startup-time execstack is still
legal — only dlopen refuses), so a session was spent on loader-path theories before an A/B
dlopen probe on the two glibc versions exposed the real layer. The tunable then made it worse:
value 2 forces the stack executable at startup, and under emulated amd64 (the arm64 test host)
glibc misreports the main thread's stack as 4 KB with the stack pointer outside it. .NET 6's
`Task.RunSynchronously` gates inlining on `TryEnsureSufficientExecutionStack`, so it silently
queued SMAPI's synchronized NewDay task to the thread pool, whose `BlockOnUIThread` then
deadlocked against the blocked main thread — every server hung at "Synchronizing 'NewDay'
task..." (same family as the musl deadlock in `modern-docker.md`). Native amd64 was unaffected,
which is what made the tunable look safe locally.

**How to apply:** Probe the actual mechanism: a one-line dlopen (perl `DynaLoader`, python
`ctypes`) inside the target image — `ldd` and `LD_PRELOAD` both test the wrong layer. Check the
marker with `readelf -lW <lib> | grep GNU_STACK` (flags `RWE` = requests execstack); exactly the
two GOG Galaxy libs carry it. Any new lib that needs the same treatment goes into
`ExecstackPatcher.GalaxyLibs` — the flag-clear runs at download completion and steam-service
startup, so it covers fresh and existing volumes. When manually verifying Galaxy loading,
`STEAM_AUTH_URL` must be set (a dead URL is fine) — without it the mod skips Galaxy init
entirely and the boot proves nothing; "Steam-auth service not ready" means the lib loaded and
only auth failed, `GalaxyInstancePINVOKE` means the dlopen itself failed.
