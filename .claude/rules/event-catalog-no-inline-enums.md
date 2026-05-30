---
paths:
  - "tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs"
  - "tests/JunimoServer.Tests/Helpers/SetupEventBus.cs"
  - "docs/developers/events-schema.md"
---

# Don't enumerate inline string values in the event-catalog doc — point at the emitter

The event catalog in `InfrastructureEventLog.cs` (and the public `docs/developers/events-schema.md`) documents what fields each event carries. When a field's value is an enum, reference the enum via `<see cref="..."/>`. When a field's value is an inline string literal at the emit site (e.g. `reason = "ffmpeg_missing"`, `stage = "wrong_state"`, `via = "sigint"`), do NOT enumerate the variants verbatim in the catalog — point at the emitting class instead ("see <see cref="ContainerRecorder"/> for the definitive list"). Inline value lists go silently stale the moment someone adds a reason at the emit site without updating the catalog.

**Why:** Caught during a recorder refactor. The catalog had ~50 lines of verbatim `"foo"|"bar"|"baz"` unions for `recording_clip_failed.stage`, `recording_start_failed.reason`, `recording_per_test_clip_skipped.reason`, etc. Several were already out of sync with the emit sites — fields had been added/renamed without doc updates. User explicitly called this out: "we don't want to manually list all possible values in them. We want to reference Classes/Enums/Whatever, but not like 20-30 values verbatim". When the inline-string set is small and unlikely to change (`"sigint"|"sigterm"|"kill9"` for the `via` field), enumerating is fine; when it's large or growing, it drifts.

**How to apply:** When adding/updating an event in the catalog, prefer `<see cref="..."/>` to a real enum (e.g. `<see cref="RecordingSkipReason"/>`); when no enum exists and the value set is >3-4 variants, write `<c>reason</c> (see <see cref="EmittingClass"/> for variants)` rather than reproducing the list. Also: if you find yourself wanting to enumerate, treat that as a signal — the inline strings should probably become a typed enum. Add a one-line note in the catalog flagging it for future cleanup so the next reader knows the absence of enumeration is intentional.
