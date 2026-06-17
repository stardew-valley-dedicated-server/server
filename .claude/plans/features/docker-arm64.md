# arm64 support via box64 (full feature parity)

> **GitHub Issue:** [#63 - linux/arm64/v8 support](https://github.com/stardew-valley-dedicated-server/server/issues/63)

**Constraint:** arm64 must match amd64 feature-for-feature — direct-IP (Lidgren) AND Steam/Galaxy invite codes. No LAN-only tier. Accepted trade-off: emulation overhead (~2-4× slower than native) is fine; a slow working server beats no server.

## Why the game process must be emulated (claims validated 2026-06-13)

- Invite-code multiplayer rides the **GOG Galaxy SDK** (`AuthService.cs`: `GalaxyInstance.Init`, `SignInSteam`, `GalaxySocket` patches). `libGalaxy64.so` is closed-source x86-64; GOG doesn't even offer the Galaxy SDK for Linux publicly ([GOG linux guidelines](https://docs.gog.com/linux-guidelines/) — the game's copy is a partner build) and has no arm64 build on any OS.
- The Steam transport (`SteamGameServerService`, SDR GameServer mode) loads **`steamclient.so`** from the Steamworks SDK Redist (app 1007), downloaded by `steam-service` (`Program.cs`: `SteamworksSdkAppId = 1007`). Valve added `linuxarm64` Steamworks libs in SDK 1.63 (Nov 2025, for Steam Frame; [announcement](https://steamcommunity.com/groups/steamworks/announcements/detail/627817201164877826)), but Stardew 1.6.15 ships an older x86-64-only Steamworks stack — only ConcernedApe could rebuild against 1.63+.
- An arm64 process cannot load x86-64 `.so` files, so a native-arm64 game (ValleyCore-style runtime swap + PE patch) is structurally LAN-only → rejected under the constraint.

**Decision:** run the entire x86-64 game process (game + SMAPI + mods + Galaxy + steamclient) under [box64](https://github.com/ptitSeb/box64) inside an otherwise-native arm64 image. Precedent: [palworld-server-docker](https://github.com/thijsvanloef/palworld-server-docker) (official arm64 incl. Pi 4/5 via box64), Valheim-on-Ampere guides, [steamcmd-arm64](https://github.com/sonroyaalmerol/steamcmd-arm64). qemu-user is not a fallback (.NET is [officially unsupported under QEMU](https://github.com/dotnet/dotnet-docker/blob/main/samples/build-for-a-platform.md)); FEX rejected (no .NET evidence, 4K-only, needs x86-64 rootfs).

## Architecture

| Native arm64 | Emulated x86-64 under box64 |
|---|---|
| Base image, supervisor/services, Xvfb/Xvnc, polybar | `StardewModdingAPI` → game process, incl. its bundled .NET 6 runtime |
| ffmpeg (BtbN publishes `linuxarm64` builds), go2rtc (`arm64` asset), tmux | All game-side native libs: SDL2, OpenAL, `libGalaxy64.so`, `libGalaxyCSharpGlue.so`, `steamclient.so`, SkiaSharp, lz4 |
| `steam-service` sidecar (SteamKit2, pure managed — runs unchanged) | — |

- Game depot stays the **linux x86-64 depot** — `steam-service` keeps downloading exactly what it does today. Hardening: the depot picker (`SteamAuthService.cs` `oslist` match) takes the first `oslist == "linux"` depot with no `osarch` check; pin `osarch` 64/x86-64 so a future Valve-added arm64 depot can't be picked.
- box64 is invoked explicitly via wrapper in `startapp.sh` (`box64 ./StardewModdingAPI ...`). No binfmt_misc — registration is kernel-global and needs host privileges; explicit exec is the established pattern.

## Phase 0 — gating experiment (decides the feature)

Nobody has publicly demonstrated Galaxy invite codes or Steam SDR relaying traffic under box64. Single-player + libs loading are confirmed ([box64#742](https://github.com/ptitSeb/box64/issues/742) Galaxy libs, [#1271](https://github.com/ptitSeb/box64/issues/1271) SMAPI, [#1351](https://github.com/ptitSeb/box64/issues/1351) Pi 5); the networking backends are not. This experiment is the whole bet.

1. **Host:** OCI Ampere A1 or Pi 5 with the 4K kernel (`kernel=kernel8.img` in `/boot/firmware/config.txt`). Verify `getconf PAGESIZE` → 4096. 16K pages are a known hard failure for .NET under box64.
2. **Experimental image:** arm64 Debian base mirroring the runtime stage of `docker/Dockerfile`; install box64 (pi-apps-coders `box64-debs` apt repo, pin version); stage x86-64 `libssl3`/`libcrypto3` into box64's x86-64 lib path (per [box64#3243](https://github.com/ptitSeb/box64/issues/3243)); arch-switch the ffmpeg/tmux/go2rtc download URLs.
3. **Launch:** wrap the game exec in `startapp.sh` with box64; keep FIFO/log plumbing identical. `BOX64_DYNAREC_STRONGMEM=1` is auto-applied for dotnet; have `BOX64_DYNAREC_BIGBLOCK`/`SAFEFLAGS` ready as knobs.
4. **Verify with the E2E suite:** LAN connect test + `WithSteam` invite-code flow (host → invite code via HTTP API → real client joins). The harness automates exactly this.
5. **Record:** pass/fail per transport, CPU/RSS at `SERVER_TPS=5`, startup time, then ≥24 h soak (existing incident reporting + auto-restart cover the known random-SIGSEGV risk, [box64#3026](https://github.com/ptitSeb/box64/issues/3026)).

**Success:** both transports pass E2E and the soak holds. **Failure:** isolate the layer (CLR crash vs Galaxy sign-in vs SDR relay — the harness shows which), file a minimal repro upstream (ptitSeb has engaged on every SDV box64 issue to date), and park the feature until fixed. Do not ship LAN-only.

## Phase 1 — productionize (only after Phase 0 is green)

- `docker/Dockerfile`: `TARGETARCH`-conditional stages — box64 install + exec wrapper + x86-64 ssl staging on arm64 only; arch-switched tool URLs; NVENC stays amd64-only (recorder already falls back to libx264).
- Page-size preflight in `startapp.sh`: fail fast with a message pointing at the Pi 5 4K-kernel workaround.
- CI: buildx `linux/amd64,linux/arm64`; run the LAN E2E subset on `ubuntu-24.04-arm` runners; `WithSteam` tests on arm64 need the same Steam-account broker as amd64.
- Docs (`docs/admins/`): arm64 requirements page — 4K-page kernel mandatory (Pi 5 default `kernel_2712` is 16K; Asahi is 16K-only and unsupported), expected performance, box64 tuning env vars.
- TODO: `docker/modern/` (Alpine/musl) arm64 variant is out of scope — box64-on-musl is untested; glibc image first.

## Open questions

- Per-device box64 builds (`rpi5`, `m1`, generic arm64) vs one generic build — decide from Phase 0 measurements.
- Whether app 1007 now ships a linuxarm64 depot (irrelevant to this plan; only matters if the game itself is ever rebuilt against SDK 1.63+ — would reopen the native path *for Steam*, though Galaxy/invite codes would still block it).
