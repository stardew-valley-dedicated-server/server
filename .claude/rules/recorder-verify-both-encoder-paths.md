---
paths:
  - "tests/JunimoServer.Tests/Helpers/ContainerRecorder.cs"
  - ".claude/rules/recorder-anchor-first-frame.md"
---

# Verify recorder claims against BOTH the libx264 and NVENC paths

`ContainerRecorder` branches on `_useGpu` in two places: `BuildEncoderArgs` (source-segment encoding) and `BuildExtractCommand`'s `codec` selection (pass-2 per-test re-encode). The two branches have substantially different keyframe behavior, codec args, and downstream seek-snap semantics. Before writing a property claim about recorder behavior in a doc-comment, rule, or commit message, verify the claim holds for BOTH branches — not just the one you happened to read.

**Why:** I wrote "irreducible single keyframe snap" in `recorder-anchor-first-frame.md` Invariant 6 and again in `BuildExtractCommand`'s doc-comment. True for the libx264 source path (`-g 1` makes every frame a keyframe — snap is ≤ one frame). **False for the NVENC source path** (`-g {fps*segmentTime}` puts keyframes only at segment boundaries — pass-2's `-ss` on the merged TS can snap back up to `segmentTime` seconds). No GPU host in production today, so the latent inaccuracy didn't bite — but the doc was lying and a future GPU host would silently regress cross-clip alignment. Caught only during a final adversarial-review pass over my own edits.

**How to apply:** When adding or changing any doc that describes recorder behavior — keyframe interval, snap, GOP, codec args, pixel format, output PTS handling — read both `BuildEncoderArgs` arms AND both `BuildExtractCommand` codec arms. If a property doesn't hold for both, state which path it applies to ("libx264 source: …; NVENC source: …") rather than asserting it universally. Same discipline applies to the `recorder-anchor-first-frame.md` invariants — Invariant 1 already follows this pattern with its explicit "NVENC caveat" subsection; later invariants should match it.
