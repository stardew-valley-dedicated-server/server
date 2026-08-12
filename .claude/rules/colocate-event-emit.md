---
paths:
  - "tests/JunimoServer.Tests/Containers/**"
  - "tests/JunimoServer.Tests/Infrastructure/**"
---

# Co-locate state-transition event emits with the state change they announce

When a component produces a state change (file on disk, resource released, container torn down) that the UI or another subsystem needs to know about, emit the event from inside that component, right where the state change becomes observable — not from an outer coordinator that runs after its dispose.

**Why:** Server `full_recording.mp4` files existed on disk but never appeared in the UI. `SetupEventBus.EmitInstanceRecording` was called from `ManagedServer.DisposeAsync` after an unwrapped `await Server.DisposeAsync()`. Any throw from the container teardown chain (`RemoveDockerVolume`, exit-code read, `TestRunRegistry.Unregister`) silently skipped the emit. `ClientPool` wrapped its inner dispose and got away with it by luck, not design. The fix moved the emit into each container, scoped to the point where `FullRecordingPath` was actually written, wrapped in its own try/catch. The public `FullRecordingPath` property was then deletable because nothing outside the container read it.

**How to apply:** Before emitting an event from outside the producing component, ask: "can something between the producer and my emit throw and swallow this?" If yes, move the emit inside the producer, wrapped in try/catch. Treat a public property whose only readers are sibling-class emit sites as a smell — the emit belongs with the property. Applies especially in `ServerContainer`, `GameClientContainer`, and any `*.DisposeAsync` that fires UI events.
