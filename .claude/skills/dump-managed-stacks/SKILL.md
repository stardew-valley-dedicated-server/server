---
name: dump-managed-stacks
description: Capture managed .NET thread stacks from a hung game process inside a Docker container, via a dotnet SDK sidecar joined to the container's PID namespace. Use when a containerized .NET process wedges (game log goes quiet, container still up) and logs don't say what it's blocked on.
argument-hint: [container name, optionally a remote docker host]
tools: Bash
---

# Dump managed stacks from a hung containerized .NET process

The game images ship no dotnet CLI and no debugger, but every .NET process exposes a
diagnostics socket in its own `/tmp`. A throwaway SDK sidecar that joins the target
container's PID namespace can reach that socket through `/proc/<pid>/root/` and pull full
managed stack traces with `dotnet-stack` — no image changes, no restart, works on a
live-hung process.

## When to use

- A server/client container is `Up (healthy)` but the game log stopped advancing (HTTP API
  may still answer — that's a different thread).
- Kernel-level triage already says "parked" but not where: use the cheap pre-checks below
  first, then this skill for the managed view.

## Cheap pre-checks (no sidecar needed)

```bash
docker exec <name> ps -eLo tid,pcpu,stat,wchan:30,comm   # thread states + kernel wait channel
cat /proc/<pid>/task/<tid>/syscall                        # sample twice, a few seconds apart
```

Same futex address twice with a NULL timeout arg = parked indefinitely (an infinite
`pthread_cond_wait`), not slow. `wait_for_partner` on an early-created thread is the CLR's
standard debug-pipe listener — benign, present in every .NET process.

## Procedure

```bash
docker run --rm --pid=container:<NAME> --cap-add SYS_PTRACE \
  mcr.microsoft.com/dotnet/sdk:9.0 bash -c '
  export PATH=$PATH:/root/.dotnet/tools
  dotnet tool install -g dotnet-stack >/dev/null 2>&1
  PID=$(for p in /proc/[0-9]*; do
    [ "$(basename $(readlink $p/exe 2>/dev/null) 2>/dev/null)" = StardewModdingAPI ] && basename $p
  done | head -1)
  ln -sf /proc/$PID/root/tmp/dotnet-diagnostic-* /tmp/
  dotnet-stack report -p $PID'
```

Read the output as pairs: find the thread that *holds* progress (e.g. blocked in a `Wait`)
and the thread it's waiting *for* — a deadlock shows up as two stacks pointing at each other.

## Pitfalls (each one cost a round trip)

- **Find the PID via `/proc/<pid>/exe` basename, not cmdline grep.** The game runs under a
  `script`/pipe wrapper whose *arguments* contain the binary name — cmdline matching returns
  the wrapper and `dotnet-stack` then reports "Unable to connect to Process".
- **Symlink the diagnostics socket into the sidecar's `/tmp`.** The socket lives in the
  *target's* mount namespace; `dotnet-stack` looks in its own `/tmp` for
  `dotnet-diagnostic-<pid>-*-socket`. `/proc/<pid>/root/tmp/` crosses the namespace and
  `connect()` follows the symlink.
- **Cross-arch works.** An arm64 SDK sidecar reads an emulated amd64 target fine — the
  EventPipe protocol is architecture-agnostic; don't waste time matching the sidecar arch.
- **Remote hosts over non-login SSH may fail to pull the SDK image** ("error getting
  credentials" — the CLI's credential helper isn't on PATH). Pull with an anonymous config:
  `mkdir -p /tmp/anon-docker-cfg && echo '{}' > /tmp/anon-docker-cfg/config.json &&
  DOCKER_CONFIG=/tmp/anon-docker-cfg docker pull mcr.microsoft.com/dotnet/sdk:9.0`.

## How this proved itself

Every Debian 13 server on the emulated test host hung at "Synchronizing 'NewDay' task..." —
container healthy, HTTP API serving, game log silent. Kernel triage narrowed it to "main
thread parked on one futex forever, only the network thread alive" but could not say what
code was waiting on what. One `dotnet-stack report` against the live hung container gave the
whole deadlock in a single shot: the main thread inside `SModHooks.StartTask →
Task.InternalRunSynchronously → SpinThenBlockingWait` (SMAPI's synchronized NewDay task was
*queued*, not inlined — the smoking gun that `TryEnsureSufficientExecutionStack` had failed),
and a thread-pool thread executing `Game1._newDayAfterFade → HoeDirt.loadSprite →
BlockOnUIThread`, waiting for the main thread to pump UI actions. Main waits for task, task
waits for main. That pair of stacks turned a "servers randomly hang on the new base image"
mystery into a precise chain (executable-stack tunable → broken stack-bounds detection →
inline refusal → UI-thread deadlock) that log reading could never have produced — the logs'
last line was an unrelated GC message.
