# Headless SDV network client (load tester → partial test-client replacement)

## Context

Today every E2E client is a **full Stardew Valley game process** running inside a Docker container: SMAPI + Mono assembly rewrite + Cecil patching + game-engine init + Xvnc/graphics device, driven over an HTTP API exposed by the `tests/test-client/` SMAPI mod (`tests/test-client/HttpServer/`, ~45 endpoints in `ModEntry.cs:253-1140`). That costs ~60s startup and ~500–800 MB per client, and a typical dev host fits only ~8 concurrent (`ClientPool` capacity = `host.ClientCapacity.Capacity`, `ClientPool.cs:93`). The bottleneck for any many-client scenario is startup time × the per-host slot cap.

A **headless client** — a plain .NET process that speaks the SDV multiplayer wire protocol directly, with no game engine and no rendering — connects in <1s at ~2–5 MB, so ~200+ fit on the same host. Two distinct uses fall out of that, and they must not be conflated:

1. **Load tester (primary, the clear unique win).** Connect N clients, idle them, and measure server TPS / memory / GC under player count. We have no tool for this today.
2. **Partial test-client replacement (secondary, opportunistic).** Most E2E tests already assert against the **server** HTTP API (`/cabins`, `/players`, `/farmhands`) per `tests-assert-via-http-api.md`, not against client-perceived state. Those connection/state tests could run against headless clients. Tests that drive **real game logic** (`HoeDirt.plant()`, `Game1.warpFarmer()`, `GameLocation.startSleep()`) or assert **rendered** state (`/screenshot`) cannot — that logic lives in the engine the headless client doesn't run.

**Goal**: a `JunimoHeadlessClient` project that completes the Lidgren handshake, is admitted as a farmhand, participates in the day-transition barrier (so it doesn't freeze the server), and idles draining server traffic — wrapped in a small load-test harness that reports server-side metrics.

**Non-goals**: rendering/screenshots; running game simulation locally (planting, warping, sleeping as engine logic); replacing the test-client wholesale; Steam/Galaxy transport (LAN/Lidgren only). Realistic *mutating* load (scripted movement/inventory deltas) is a documented future extension, not in this plan.

## Key findings from the decompiled protocol (`decompiled/sdv-1.6.15-24356/`)

These are verified against source, not inferred — they determine the whole shape of the work.

### What we reuse vs. reimplement

The reusable pieces are the **wire-format helpers**, which are static and `Game1`-free:
- `OutgoingMessage.Write(BinaryWriter)` — frame is exactly `byte messageType` + `long farmerID` + `WriteSkippable(payload)` (`OutgoingMessage.cs:42-145`).
- `IncomingMessage.Read(BinaryReader)` — the inverse.
- `LidgrenMessageUtils.WriteMessage` / `ReadStreamToMessage` — wrap a message into/out of a Lidgren buffer, with gzip applied **above 1024 bytes** via `Program.netCompression.CompressAbove(data, 1024)` (`LidgrenMessageUtils.cs:12-38`).
- `NetFarmerRoot` / `NetRoot<T>.WriteFull`/`ReadFull` / `NetWorldState` / `FarmerTeam` — the `Farmer`/world serialization. `NetFarmerRoot` uses `SaveSerializer.GetSerializer(typeof(Farmer))` (`NetFarmerRoot.cs:10`).

What we **cannot** reuse: `LidgrenClient` and its base `Client` are deeply `Game1`-coupled. `connectImpl`, `receiveAvailableFarmhands` (`Client.cs:144-179`), `sendPlayerIntroduction` (`Client.cs:186-197`), and `setUpGame` (`Client.cs:199+`) all read/write `Game1.player`, `Game1.multiplayer`, `Game1.activeClickableMenu`, `Game1.netWorldState`, `Game1.log`, `Game1.content`. So the **connection state machine is reimplemented** against the raw Lidgren `NetClient`; only the framing/serialization helpers are linked from the game assemblies.

The one open risk (the spike below): whether `SaveSerializer.GetSerializer(typeof(Farmer))` and the `NetRoot` serialization run cleanly **without a live `Game1`**. `NetFarmerRoot.Clone()` references `Game1.serverHost` (`NetFarmerRoot.cs:22`) — but `WriteFull`/`ReadFull` on the base `NetRoot<T>` (`NetRoot.cs:48-72`) do not, so the serialization path we need may be `Game1`-free even though sibling methods aren't. This must be proven, not assumed.

### The exact handshake a headless client must perform

Transport: **Lidgren UDP, port 24642** (`LidgrenClient.cs:59`), `NetPeerConfiguration("StardewValley")`, MTU 1200, `ReliableOrdered` delivery. No auth — `LidgrenClient.getUserID()` returns `""` (`LidgrenClient.cs:29-32`), and the server's farmhand check passes on `userID == "" || farmer.userID == userID` (this is also why our LAN path is immune to the abandoned-claim bug per `abandoned-claim-is-steam-only.md`).

1. **Discovery.** `client.DiscoverKnownPeer(host, 24642)`. Server replies with a `DiscoveryResponse` carrying, in order: `protocolVersion` (string), `serverName` (string) (`LidgrenClient.cs:116-127`). We must `validateProtocol` — the string is `Game1.version[+versionLabel]` (`Multiplayer.cs` `protocolVersion`). Since we link the game assemblies, `Multiplayer.protocolVersion` is available to match it.
2. **Connect.** `client.Connect(senderEndpoint.Address, senderEndpoint.Port)` (`LidgrenClient.cs:157-160`).
3. **Receive available farmhands (msg type 9).** Payload order: `int32 year`, `int32 season`, `int32 dayOfMonth`, `byte count`, then `count` × `NetFarmerRoot.ReadFull` (`Client.cs:144-162`, server side `GameServer.cs:608-663`). Pick one farmhand.
4. **Send player introduction (msg type 2).** `writeObjectFullBytes(NetFarmerRoot, null)` of the chosen farmhand (`Client.cs:186-196`). This is the farmer we send back to claim the slot.
5. **Receive server introduction (msg type 1).** Payload order: full `NetFarmerRoot` (host farmer), full `FarmerTeam`, full `NetWorldState` (`Client.cs:268-285`, server side `sendServerIntroduction` `GameServer.cs:396-406`). On the real client this triggers `setUpGame()` and sets `readyToPlay = true`. Headless: we deserialize enough to know our `UniqueMultiplayerID` and the world clock, then **discard the rest** (we don't maintain a world model).

If the server is mid-transition (`isGameAvailable()` false — festival, wedding, new-day-sync in progress, demolish lock; `GameServer.cs:459-470`), step 3 returns available-farmhands but approval waits. The harness must connect when the server is available (fresh save, daytime), which the broker already arranges for normal tests.

### The day-transition barrier — the must-implement blocker

An idle client that only drains traffic will **freeze the entire server** at the first night. The server's new-day flow blocks on `Game1.newDaySync.hasFinished()`, and `isGameAvailable()` is false until it finishes (`GameServer.cs:461` `flag3`). The barrier readiness check requires **every** connected farmer to have checked in (`NetSynchronizer.barrierReady`, `NetSynchronizer.cs:33-44`):

```
barrier ready  ⇔  ∀ key ∈ Game1.otherFarmers.Keys : barrierPlayers(name).Contains(key)
```

So each headless client must answer the new-day protocol. The messages involved (`Multiplayer.cs` constants): `newDaySync` = **14**, `startNewDaySync` = **30**, `readySync` = **31**. The barrier itself sends inner-type `1` + barrier-name string (`NetSynchronizer.cs:55-73, 138-157`) wrapped in the type-14 envelope. The headless client's job is: on receiving the server's new-day kickoff, echo the barrier check-ins for each named barrier so `barrierReady` becomes true. **The precise set of barrier names and the readySync sequence must be traced from a real run** (capture a real two-client day transition's message log) before implementing — this is the single highest-risk detail and the most likely source of a subtle "server hangs at 2am under load" bug. Treat it as a sub-spike.

### What an idle client receives and must drain

Per tick the server pushes deltas the client must read off the socket (or Lidgren's buffer backs up): `worldDelta` (12), `serverToClientsMessage` (18), and `farmerDelta`/`locationDelta`/`teamDelta` (0/6/13) (`Multiplayer.cs` dispatch). The headless client reads-and-discards these — it does **not** decode `NetField` deltas. Draining is mandatory; interpretation is not.

## Infrastructure integration (from `tests/`)

- **SDV assemblies** come from `Directory.Build.props` `GamePath` (extracted from `.env`/`GAME_PATH`), the same mechanism the mod and test-client use. The new project references the game DLLs (for `StardewValley.Network.*`, `Netcode.*`, `Lidgren.Network`) the same way — no SMAPI reference needed (it's not a mod).
- **Networking.** The server container exposes Lidgren on `24642/udp` and is reachable on the test Docker network via alias `server-{runId}` (`ServerContainer.cs:62,255`; clients connect via `ConnectViaLanAsync("server-{runId}", 24642)`). The game port is **network-internal, not tunneled** — so headless clients must run **inside a container on the same Docker network**, OR (simpler for local load tests) the server's `24642/udp` mapped host port can be used directly when running the harness on the Docker host. The plan uses the container-on-network model to match how real clients reach the server.
- **Where it lives.** New project `tests/JunimoHeadlessClient/JunimoHeadlessClient.csproj` (console app, net6.0 to match the game's net6.0 assemblies — `test-client` is net6.0). Sibling to `tests/test-client/`. The load-test harness that orchestrates N of them lives in `tests/JunimoServer.Tests/` (net10.0) and drives them.
- **Server-side metrics already exist**: `/stats` reports TPS, `AvgTickMs` (rolling 60-tick), memory, GC counts, game-thread wait (`ApiService.cs:337-368`). The load test reads these — no new server instrumentation needed for v1.

## Approach — phased, spike-gated

### Phase 0 — Feasibility spike (gating; ~0.5 day)

**Do not skip or build past this.** Two questions, both answered by running code, per `runtime-post-conditions-are-gates.md`:

1. **Does `Farmer`/`NetRoot` serialization work headless?** A throwaway console app that references the game DLLs, constructs a `NetFarmerRoot` from a known farmhand, calls `WriteFull` into a `MemoryStream`, and `ReadFull`s it back — with no `Game1` initialized. If it throws on a `Game1`-static access, scope the workaround (e.g. minimal `Game1` field priming) before committing to the architecture.
2. **Does the discovery+connect handshake complete?** Same app: raw Lidgren `NetClient` against a locally-running server container's mapped `24642/udp`, complete steps 1–2, log the `DiscoveryResponse` protocol string and confirm `validateProtocol` passes.

Post-condition: both observed working (or the workaround scoped). If `Game1`-priming turns out to be extensive, re-evaluate the whole effort — that's the 4-vs-7 hinge.

### Phase 1 — Handshake to "connected farmhand" (~1.5 day)

Implement the connection state machine (reimplemented, not `LidgrenClient`): discovery → connect → receive type 9 → send type 2 → receive type 1 → mark connected. Reuse `OutgoingMessage`/`IncomingMessage`/`LidgrenMessageUtils`/`NetFarmerRoot` for framing. Decode only `UniqueMultiplayerID` + world clock from the server intro; discard the rest. Add a drain loop reading and discarding all incoming deltas.

Post-condition (runtime gate): a single headless client connects to a broker-provisioned server and appears in the server's `/players` (or `/farmhands` as customized) snapshot. Verified by HTTP read against the server API, per `tests-assert-via-http-api.md`.

### Phase 2 — Day-transition barrier participation (~1 day)

Sub-spike first: capture a real two-real-client day transition and log the type-14/30/31 message sequence and barrier names. Then implement the headless barrier check-in so `barrierReady` resolves with N headless clients connected.

Post-condition (runtime gate): with 3 headless clients connected, advance the server a day (via the server's existing `/test` time controls) and confirm the day actually advances — server `/stats` `tickCount` keeps climbing, `isFrozen=false`, no hang. **This is the make-or-break correctness test**; a passing Phase 1 with a broken Phase 2 produces a server that wedges under load.

### Phase 3 — Load-test harness + metrics (~1 day)

A harness in `tests/JunimoServer.Tests/` that: provisions one server, spawns N headless clients (containerized on the test network, parallel — no 60s game-engine boot), holds them M minutes, samples server `/stats` (TPS, AvgTickMs, memory, GC, game-thread wait) on an interval, and asserts a TPS floor (e.g. ≥90% of configured `SERVER_TPS`, which is `5` headless per `server-tps-headless.md`). Emit samples to `infrastructure.jsonl` via `InfrastructureEventLog.Emit` for the existing UI/trace pipeline. Add a `make` target (`load-test` with an `N=` / `MINUTES=` parameter).

Post-condition (runtime gate): a real run with N=20 reports a TPS curve and stable memory, completing in minutes (vs. hours with real clients). Document the measured per-client cost to confirm the ~60–120× startup and ~100× density claims hold in practice rather than on paper.

### Phase 4 (optional, separate task) — test-client migration

Only after Phases 1–3 are solid. Port the connection/state E2E tests (the ~70% that assert via server API) to optionally run against headless clients behind the same `GetClientAsync()`-style seam, keeping real clients for `/screenshot` and engine-driven-action tests. Scope and effort to be re-estimated then; **not committed here** — flagged as the follow-on so the plan reader knows the replacement story is deliberate, not forgotten (`holistic-or-explicit-todo.md`).

## Risks & open questions

- **Headless serialization (Phase 0 Q1)** — the architecture hinges on `NetRoot` serialization being `Game1`-free. Gated by the spike.
- **Barrier protocol fidelity (Phase 2)** — the highest-risk *correctness* detail; mis-implementing it wedges the server under load in a way that looks like a server bug, not a client bug. Trace from a real run; do not implement from the decompile alone.
- **net6.0 vs net10.0 boundary** — the headless client is net6.0 (game assemblies); the harness is net10.0 (test assembly). They communicate over the network + process boundary, not in-process, so the TFM split is fine. Confirm during Phase 3.
- **Effort estimate: ~4 days** for Phases 0–3 (the useful load tester). Phase 4 is additional and re-estimated later.

## Why not just reuse `LidgrenClient`

Considered and rejected: `LidgrenClient`/`Client` require a live `Game1` (menu state, `Game1.player`, `Game1.multiplayer`, content manager). Driving a real `Game1` headless *is* what the current test-client does — the whole point of this work is to avoid that cost. Reusing only the static wire-format helpers (`simplest-solution.md`: use the existing pattern where it fits, don't reimplement serialization) while owning a minimal connection state machine is the smallest thing that delivers the load tester.
