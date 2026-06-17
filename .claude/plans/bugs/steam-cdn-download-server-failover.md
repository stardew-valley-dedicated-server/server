# Harden Steam CDN download against single-server / DNS failures

## Context

A game-download build step crashed with an **unhandled** `HttpRequestException: Name or
service not known (cache1-blv2.valve.org:443)`. The login, license check, and manifest-code
fetch all succeeded — the failure was purely the CDN manifest download.

Root cause, from the log: Steam returned **13 CDN servers** (`Found 13 CDN servers`), but the
code uses only the first (`var server = cdnServers.First();`,
`tools/steam-service/SteamAuthService.cs:1464`). When that one host had a DNS failure, all 4
retries in `RetryTransientAsync` re-invoked the *identical closure* and hammered the **same
dead host** (`cache1-blv2.valve.org`) four times, then threw an unhandled exception that crashed
the process. The other 12 healthy servers were never tried.

Three independent weaknesses:
1. **Single-server pinning** — manifest (line 1464) and every chunk (line 1606) reuse one `server`.
2. **No host failover in retry** — `RetryTransientAsync` retries the same target; backoff against an unresolvable host is wasted.
3. **Unhandled crash** — exhausted retry escapes to `Main` as a raw stack dump instead of a clean non-zero exit.

Intended outcome: a single dead/unreachable CDN host costs **one attempt, then failover to the
next server**, and total CDN unavailability produces a clear `Environment.Exit(1)` with a
readable message instead of a stack trace.

## Decisions (confirmed with user)

- **Rotate across the server list AND classify DNS/connect failures as per-host-fatal** — on a
  `SocketException`-rooted failure, skip immediately to the next server with **no backoff delay**;
  reserve linear backoff only for transient HTTP failures (e.g. 503 during a depot rollout).
- **Clean non-zero exit on full failure** — catch the exhausted-retry exception, log
  `all N CDN servers unreachable`, and `Environment.Exit(1)`.
- **One rotation primitive for both manifest and chunks** — SteamKit2 has no built-in CDN
  failover (the `Server` param is required/singular on both download methods), so per-request
  rotation across the list is the idiomatic pattern. A single `RetryAcrossServersAsync` serves
  both call sites; the chunk loop's old bespoke 3-attempt same-server retry is deleted, leaving
  no duplicated retry logic.

## Changes

### 1. `tools/steam-service/SteamAuthService.cs` — server-rotating retry

Add `using System.Net.Sockets;` (line ~3, for `SocketException`) — not currently imported.
(`SocketException` lives in `System.Net.Sockets`, not `System.Net`.)

**Keep** the existing `RetryTransientAsync<T>(string label, Func<Task<T>> operation)` (lines
1167–1184) unchanged — it's still the right tool for the two content-API calls (see call sites
below). **Add** a sibling server-rotating method for the CDN-host calls:

```csharp
// Runs a CDN-host request across the available servers, failing over on error. A DNS/connect
// failure (SocketException) is host-fatal: skip to the next server immediately with no delay.
// A transient HTTP failure (e.g. 503 during a depot rollout) backs off, then retries the next
// server. Throws an aggregated exception only when every server has been exhausted.
private async Task<T> RetryAcrossServersAsync<T>(
    string label,
    IReadOnlyList<Server> servers,
    Func<Server, Task<T>> operation)
{
    // Walk servers; cap so a huge list doesn't spin forever, floor so the loop always runs once.
    int maxAttempts = Math.Clamp(servers.Count, 1, 8);
    var failures = new List<Exception>();
    for (int attempt = 0; attempt < maxAttempts; attempt++)
    {
        var server = servers[attempt % servers.Count];
        try
        {
            return await operation(server);
        }
        catch (Exception ex)
        {
            // Catch on EVERY attempt (incl. the last) so the real failure is collected, not
            // swallowed. Throw the aggregate only after the loop exhausts all servers.
            failures.Add(ex);
            bool hostDead = IsHostFatal(ex);
            Logger.Log(
                $"{_logPrefix} {label} failed on {server.Host} "
                + $"(attempt {attempt + 1}/{maxAttempts}{(hostDead ? ", host unreachable, skipping" : "")}): {ex.Message}"
            );
            if (!hostDead && attempt < maxAttempts - 1)
            {
                await Task.Delay(1000 * (attempt + 1)); // backoff only for transient HTTP errors, never after the last try
            }
        }
    }
    throw new AggregateException(
        $"{label} failed across all {servers.Count} CDN servers", failures);
}

// A DNS-resolution or TCP-connect failure means *this host* is unusable; SteamKit surfaces it
// as HttpRequestException wrapping a SocketException. Walk the inner chain to detect it.
private static bool IsHostFatal(Exception ex)
{
    for (var e = ex; e != null; e = e.InnerException)
    {
        if (e is SocketException)
        {
            return true;
        }
    }
    return false;
}
```

> **Control-flow invariant (adversarial-review fix):** the catch has **no `when` guard** — it runs
> on every iteration including the last, appends to `failures`, and the loop falls through to the
> `throw new AggregateException`. (An earlier draft guarded the catch with `when (attempt <
> maxAttempts - 1)`; that made the last attempt's exception propagate raw and rendered the
> post-loop throw unreachable — a swallow-the-real-reason bug. Do **not** reintroduce the guard.)
> With 1 server, `maxAttempts == 1`: one attempt, then a single-element `AggregateException` — the
> real reason is still carried in `InnerExceptions[0]`.

**Update the call sites.** Build an indexable, de-duplicated server list once, right after the
fetch:

```csharp
// GetServersForSteamPipe's doc type is "enumerable list"; the concrete result is already
// indexable (existing code uses .Count/.First), but ToList guarantees IReadOnlyList for the
// rotation. DistinctBy(Host) drops duplicate hosts so rotation can't re-hit one dead server.
var cdnServerList = cdnServers.DistinctBy(s => s.Host).ToList();
Logger.Log($"{_logPrefix} Found {cdnServerList.Count} CDN servers");
```

Then:
- **CDN server list fetch** (lines 1452–1455): `GetServersForSteamPipe()` is a content-API call
  over the CM connection — it *returns* the list and does **not** target a CDN host (verified:
  SteamKit2 XML signature takes no `Server` param). Leave it on `RetryTransientAsync`. Do **not**
  route it through `RetryAcrossServersAsync` (no server list exists yet).
- **Manifest request code** (lines 1468–1471): `GetManifestRequestCode` is also a content-API
  call (no `Server` param; the returned code is manifest-specific and valid on **any** CDN server,
  verified in SteamKit2 XML at lines 3715/2255). Keep on `RetryTransientAsync`. *This is the
  load-bearing correctness fact for rotation:* because the request code is not server-bound, the
  manifest download can fail over to a different server with the same `manifestCode`.
- **Manifest download** (lines 1482–1492): route through `RetryAcrossServersAsync`, passing
  `cdnServerList` and `server => cdnClient.DownloadManifestAsync(depotId, manifestId, manifestCode, server, depotKeyResult.DepotKey)`.
- **Remove the `var server = cdnServers.First();`** pin (line 1464).

### 2. `tools/steam-service/SteamAuthService.cs` — chunk loop uses the SAME primitive

**Delete the chunk loop's bespoke inner retry** (the `for (int retry = 0; retry < maxRetries; retry++)`
block, lines 1599–1619) and route each chunk through the **same** `RetryAcrossServersAsync`
primitive used for the manifest. SteamKit2 has no built-in CDN failover — the `Server` param is
required and singular on both download methods (verified: XML lines 2225/2244, both throw
`ArgumentNullException` on a null server), so per-request rotation across the list is the idiomatic
pattern, and there's no reason to maintain two different rotation mechanisms.

Replacement for the per-chunk download (inside the `foreach (var chunk in chunksToDownload)`):

```csharp
var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
try
{
    // Same primitive as the manifest: rotate servers, host-fatal skips with no delay.
    int written = await RetryAcrossServersAsync(
        $"Chunk {file.FileName}",
        cdnServerList,
        server => cdnClient.DownloadDepotChunkAsync(depotId, chunk, server, buffer, depotKeyResult.DepotKey)
    );

    if (written != (int)chunk.UncompressedLength)
    {
        throw new Exception(
            $"Chunk size mismatch for {file.FileName}: expected {chunk.UncompressedLength}, got {written}");
    }

    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
    await fs.WriteAsync(buffer, 0, written);
    fileBytes += written;
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

**Why this is correct:**
- **Buffer lifecycle:** the buffer is rented **outside** `RetryAcrossServersAsync` and captured in
  the lambda; `DownloadDepotChunkAsync` overwrites it from offset 0 each attempt (SteamKit
  semantics), so a partial write from a failed server is harmless. The `ArrayPool` return stays in
  the `finally` exactly as today.
- **Validation preserved:** the `written != UncompressedLength` check and the offset `Seek`/`Write`
  stay at the call site (unchanged behavior); only the retry *mechanism* moves into the primitive.
- **Cleanup preserved:** when all servers are exhausted, `RetryAcrossServersAsync` throws an
  `AggregateException` that propagates out of the chunk loop, leaving `downloadSuccess == false`,
  so the existing partial-file-delete `finally` (lines 1654–1668) still runs unchanged.
- **One mechanism:** the old `maxRetries = 3` same-server retry is gone — there's now a single
  rotation primitive for both manifest and chunks, no duplicated retry logic.

### 3. `tools/steam-service/Program.cs` — clean exit on download failure

The cleanest place to catch is **inside `DownloadAllAsync`** (line 395) rather than at each
caller — there are **three** call sites (verified): the `setup` single-account path (line 215),
the `setup` multi-account path (line 259), and the `download` path (line 313). Wrapping the one
helper covers all three without duplicating the try/catch:

```csharp
async Task DownloadAllAsync(SteamAuthService svc)
{
    try
    {
        await svc.DownloadGameAsync(StardewValleyAppId);
        if (!args.Contains("--skip-sdk"))
        {
            var steamSdkDir = Path.Combine(gameDir, ".steam-sdk");
            await svc.DownloadGameAsync(SteamworksSdkAppId, steamSdkDir);
        }
    }
    catch (Exception ex)
    {
        Logger.Log($"[SteamService] Game download failed: {ex.Message}");
        Environment.Exit(1);
    }
}
```

This converts the raw unhandled-exception stack dump into a clear, single-line failure plus a
non-zero exit that the build/CI sees cleanly. `Environment.Exit(1)` skips the end-of-`Main`
`svc.Disconnect()` loop (line ~386), which is **consistent with the existing codebase** — seven
other sites already `Environment.Exit(1)` from inside the switch (config errors), and the OS
reclaims the Steam socket on process teardown. This is a build-time-only tool, so that is fine.
For an `AggregateException` from `RetryAcrossServersAsync`, `ex.Message` is the wrapper text; the
per-server failure detail is already logged by `RetryAcrossServersAsync` itself, so the one-line
caller message is enough.

## Out of scope

- Changing the bounded-retry behavior of the two content-API calls (CDN-server-list fetch,
  manifest-request-code) — those don't target a specific CDN host, so server rotation doesn't apply;
  their existing 4-attempt backoff is correct.
- Any change to `serve` / `ticket` / `login` / `export-token` command paths.

## Verification

This is a `net10.0` console tool under `tools/steam-service/`, build-time only (runs in the
Docker image build / `setup` flow). There is no E2E test layer for it.

1. **Build clean:** `dotnet build tools/steam-service/SteamService.csproj` — confirm the
   `using System.Net.Sockets;` addition and new methods compile (watch for `SocketException` /
   `AggregateException` / `DistinctBy` resolution; `DistinctBy` is .NET 6+, fine on net10.0).
2. **Static trace of the failure path:** confirm `cdnServers.First()` is gone, the chunk loop's
   old `for (retry...maxRetries)` block is deleted, both the manifest fetch and each chunk go
   through the single `RetryAcrossServersAsync` (selecting `servers[attempt % servers.Count]`),
   and that catch has **no `when` guard** (so the final attempt is collected and the post-loop
   `throw` is reachable).
3. **Failover unit-walk (manual, no live Steam needed):** the host-fatal classifier is pure —
   construct `new HttpRequestException("x", new SocketException())` and confirm `IsHostFatal`
   returns true, and a plain `new Exception("503")` returns false. Implementer can sanity-check
   this in a scratch `Main` or by inspection.
4. **Live confirmation (optional, needs Steam creds):** run the `download` command; on a healthy
   run the log should still read `Found N CDN servers` → `Downloading manifest...` → success. The
   resilience path only triggers when a server is actually dead, which can't be forced
   deterministically — so the primary gate is (1)–(3) plus the log-message readability of the new
   per-server failure lines.

## Risk notes

- **Control flow (the one that nearly shipped wrong):** `RetryAcrossServersAsync`'s catch has no
  `when` guard so the final attempt is collected and the aggregate throws. A green build with the
  guarded version would *look* fine but propagate the raw last-attempt exception and never reach
  the aggregate — verify the guard is absent in code review, not just that it compiles.
- `IsHostFatal` walks `InnerException`; the observed crash was `HttpRequestException` →
  `SocketException`, so the walk catches it. The loop handles deeper nesting defensively at no cost.
- **Not verifiable read-only:** that SteamKit2's CDN `Client` actually honors a different `Server`
  per call (vs. caching a connection to the first host) — the XML docs are silent. The signatures
  take `Server` per call and the existing chunk loop already passes `server` per chunk, so the API
  is per-call by contract; if a live run ever shows rotation not switching hosts, that's the thing
  to inspect. Flagged rather than silently assumed.
