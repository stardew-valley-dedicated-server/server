# Wedding test: containers + recordings run ~1 min past the actual test, frozen-tick tail

**Status: 3 of 4 fix steps landed (1, 2, 4). Step 3 — stop recorders at the test boundary —
is the sole remaining work.**

User report: weddings finish ~0:57 (video clock), but containers and videos ran until ~1:57;
from ~0:57 the in-video TICK counter was frozen (nothing happening), and recording-finalization
only *started* at ~1:52 — so the frozen minute was not explained by finalization.

Evidence run: `TestResults/runs/2026-06-24T10-48-19Z_e5fdae0/`.

## Root cause

`recording_stopped` (frame capture ends) was coupled to **container disposal**, not to the test
body finishing:

- **Server:** `ServerContainer.DisposeAsync` calls `_recorder.StopAsync`. Disposal ran at
  lease-release in `TestLifecycle` cleanup — ~25s after the world went idle.
- **Clients:** `GameClientContainer.DisposeAsync` calls `_recorder.StopAsync`, but client
  *containers* are disposed by `ClientPool.DisposeAsync` at **end of class teardown**, not when
  the client is `client_returned` to the pool. Under `SharedClass` the client recorder ran from
  test-body-end until pool teardown — ~57s in the evidence run, longer with more methods in the
  class.

The per-test clip (what the timeline/UI shows) was already extracted during the artifacts phase,
before the recorder stopped — so the late `StopAsync` fed only the **full** recording's frozen
tail. Server disposal also blocked the *next* test's start (33s synchronous `leaseReleaseMs`),
compounding the same coupling bug from the other direction.

## Fix (4 steps)

1. **Fast `StopAsync`** — landed. Collapsed the N `docker exec` pid-check polling loop into one
   in-container wait exec (`ContainerRecorder.cs:1865-1894`, `kill -0` polling at 0.2s
   granularity). ~10s SIGINT finalize → ffmpeg's real finalize time (~1-2s) + 1 exec.
2. **Relax `ExtractClipFromLiveAsync`'s state guard** — landed (`ContainerRecorder.cs:746-749`).
   Accepts `Stopped` in addition to `Recording`: extraction reads finalized on-disk segments
   (`ReadSegmentListAsync` + `SelectCoveringSegments`), and a graceful `StopAsync` finalizes the
   active segment before returning, so a clip whose window touched the former active segment
   still finds it. Still rejects a dead/never-started container.
3. **Stop recorders at the test boundary** — **not landed**. In `TestLifecycle.FinalizeAsync`,
   after the per-test clip extraction, `await` a stop on every registered recorder (server +
   clients) via a `StopAllRecordersAsync()`-shaped call. This is what actually freezes the video
   at the test boundary instead of at disposal.

   **Constraint (verified, still holds):** recorders are per-CONTAINER-lifetime, not per-test —
   each is created once in the container's own `StartAsync` and containers are pooled/reused
   across `SharedClass`/`SharedAssembly` methods. A boundary stop must only fire when the
   container is genuinely retiring (last test on that instance), not on every test — stopping a
   pooled recorder mid-reuse would kill recording for every later test on that container. Steps
   1+2 make a boundary stop cheap and safe to award once that "is this the last use?" check
   exists; that check is the open part of this step.
4. **Background the demand-exhausted + PerTest server disposal** — landed
   (`TestResourceBroker.cs`, `BackgroundDisposeServer` helper at `:2345`, called from the PerTest
   release branch at `:2208` and the demand-exhausted branch at `:2244`). Synchronous bookkeeping
   (`ReleaseSlotEarly`, `server_disposed` emit) happens on the test thread so the next test's
   acquisition isn't blocked; the heavy container teardown (`StopAsync` → extract → retrieve) now
   runs in the background, off the critical path.

## Runtime verification (run 2026-06-24T14-37-16Z) — steps 1/2/4 only

Measured vs the pre-change run, relative to test-body-done:

| Metric | Before | After |
|---|---|---|
| `leaseReleaseMs` (test-thread block) | 33,360 ms | **7 ms** |
| cleanup phase total | 35.10 s | **1.47 s** |
| `test_completed` after body-done | ~44 s | **~10 s** |
| recorders stopped after body-done | 25 s (server) / 57 s (clients) | **~18 s, all three uniformly** |
| last `container_stopped` after body-done | ~75 s | **~35 s** |
| `recording_per_test_clip` / `recording_clip_failed` | 3 / 0 | **3 / 0 (no regression)** |

Fast `StopAsync` confirmed via=sigint (no TERM/KILL escalation) on all three recorders. The
residual ~18s post-weddings capture is legitimate activity (ceremony-2 teleport-home beat, final
assertions, disconnect, navigate-to-title), not frozen idle — the remaining ~1 min frozen-tick
tail this plan set out to fix is **already gone** for the common case. Step 3 would tighten the
~18s further by cutting capture at the exact boundary instead of at background-disposal-driven
`StopAsync`, but is not required to clear the original complaint.

## Known residual risk (untouched by steps 1/2/4, applies if step 3 lands)

**Clip-extract vs container-teardown race.** The deferred per-test clip extract and a
backgrounded server dispose's full convert/retrieve both acquire the host `ExtractLimiter`, but
container stop itself is not limiter-gated — it could in principle tear the container down
mid-clip-extract. Pre-existing (predates steps 1/2/4), not widened by them. Runtime evidence
above shows 0 `recording_clip_failed` across the verification run. If step 3 lands and a future
run shows a lost per-test clip, gate `DisposeContainerSafely` behind the `ExtractLimiter`.

## Out of scope

- The convert+retrieve+container-stop work itself (bounded and necessary) — only the
  frame-capture tail and its coupling to disposal was the bug.
- General end-of-class teardown latency unrelated to recording.
- Client recorder tail from run-lifetime pooling (early-stopping a pooled client recorder would
  break reuse for later methods in the same class) — inherent, not a regression.
