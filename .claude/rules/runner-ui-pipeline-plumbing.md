---
paths:
  - "tests/JunimoServer.Tests/Helpers/SetupEventBus.cs"
  - "tests/JunimoServer.Tests/Helpers/ContainerStatsCollector.cs"
  - "tests/JunimoServer.TestRunner/**"
  - "tests/test-ui/src/types/events.ts"
  - "tests/test-ui/src/types/state.ts"
  - "tests/test-ui/src/composables/useTestStore.ts"
---

# Adding a field to a runner→UI event requires end-to-end plumbing

Shared event records (e.g. `InstanceStatsData`) flow through a hand-written pipeline: producer → `SetupEventBus` JSONL → `SetupPipeServer` parse → `ITestRenderer.OnX` signature → `RendererBase` → `WebRenderer` → `TestRunState.ApplyX` → live WebSocket `evt` object → `BuildSnapshot` projection → `test-ui` types → store → component. Every hop is a hand-written forwarder. Adding a field at the producer does NOT automatically plumb it through.

**Why:** 8 fields (`gcRate`, `pendingActions`, `gameThreadWaitMs`, `netRx/TxBytesPerSec`, `blkRead/WriteBytesPerSec`, `memoryLimitMb`) were added to `InstanceStatsData` but silently dropped at `SetupPipeServer.cs` parse block, the `OnInstanceStats` signature chain, `TestRunState.ApplyInstanceStats`, the outgoing WS `evt` anonymous object, AND `BuildSnapshot`'s `StatsHistory` projection. Test-ui types already declared the fields, so the consumer read `event.fieldName ?? 0` and produced flat-zero graphs. Compile clean, runtime silent, ~30 min to diagnose.

**How to apply:** When adding a field to any record or event that crosses the TestRunner IPC boundary, grep the field name through every hop listed above and confirm each hand-written forwarder either passes it through or explicitly drops it with a comment. Same rule when adding to `TestRunState`'s `BuildSnapshot` (used for late-connecting WebSocket clients) — it is a separate projection from the live `evt` object and needs its own update. The only layer with compile-time coverage is the C# signature chain; all JSON serialization and TypeScript consumption is string-keyed.
