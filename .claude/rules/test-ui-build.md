---
paths:
  - "tests/test-ui/**"
---

# Use make build-test-ui for UI builds

When checking the test-ui build, use `make build-test-ui` not `npx vite build`. The Makefile target runs `bun install && bun run build`, which expands to `vue-tsc --noEmit && vite build` — that catches TypeScript errors that plain vite build misses (vite strips types without checking them).

**Why:** Missed a `.value` in a template expression that vue-tsc caught but plain `vite build` didn't.

**How to apply:** Any time verifying test-ui compiles, run `make build-test-ui` from the repo root. Don't reach for `make test-web` for build-only checks — it builds the UI then runs the full E2E suite (10+ min).
