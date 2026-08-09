---
paths:
  - ".claude/plans/**"
---

# Plans cite files and symbols, not line numbers

In a plan document, cite the file and the symbol — `SteamAuthService.cs`, `PruneContentManifest`, `# SERVER_TPS=60`. Leave the `:123` off. Anchors that need a position take a neighbouring symbol or literal instead ("between `# SDVD_MAX_CONCURRENT_EXTRACTIONS=3` and `# SERVER_TPS=60`"), and counts of things that grow get dropped or labelled a snapshot to re-derive.

**Why:** Five plans had drifted silently. `PruneContentManifest`, cited at line 930, had moved to 1105; two timeout sources at 237/273 were at 286/341; `.env.test.example` insertion anchors at 83/86 were at 89/92; and a rules-reorg plan's "39 files → 4 buckets" mapping covered 39 of 48. Updating the numbers was rejected in favour of removing them — a plan sits for months while the code under it moves every week, so an exact position is close to guaranteed wrong by the time anyone executes the plan, and a stale one reads as authoritative.

**How to apply:** This is about documents with a long shelf life, not about in-session work — review findings, verification evidence, and the "cite file:line" compatibility sections in [`plan-discipline.md`](universal/plan-discipline.md) still want exact positions, because they are consumed immediately against the tree they were read from. When a sentence's meaning leans on the position ("delete the source at `:237`"), rewrite it to name the thing ("delete both 300-second cancellation sources").
