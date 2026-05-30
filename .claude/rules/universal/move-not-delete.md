# Never delete generated/untracked files without moving them first

Never delete generated or untracked files. Always move them to the new location instead.

**Why:** Untracked files cannot be recovered from git. Generated files like mock-data.json require a full E2E test run (expensive) to regenerate. Deleting instead of moving caused data loss and wasted time.

**How to apply:** When relocating any file, use `mv` (or git mv for tracked files), never `rm` + recreate. Before deleting ANY file, verify it's tracked by git and can be recovered. If untracked, ask first.
