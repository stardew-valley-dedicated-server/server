---
paths:
  - "renovate.json"
---

# Renovate `allowedVersions` silently no-ops under the default `nuget` scheme — pair it with `versioning: semver` and verify via local dry-run

A Renovate `packageRule` that constrains a NuGet dependency with `allowedVersions` can silently fail to filter anything — no error, no warning, the bumps keep appearing. Under the default `nuget` versioning scheme, the range comparison falls through for packages whose published version list interleaves prereleases (the debug log shows `Falling back to npm semver syntax for allowedVersions`). Add `"versioning": "semver"` to the rule so the constraint is evaluated correctly. Also: a bare `"allowedVersions": "2.4.1"` reads as a `>=` floor, not a pin — use `"=2.4.1"` for an exact pin.

**Why:** A SteamKit2 pin (`allowedVersions: "2.4.1"`, scoped to the mod csproj) was merged and looked correct, but Renovate kept proposing the forbidden `2.5.0`/`3.4.0` bumps across two PR-recreation cycles. Root-causing meant running Renovate's own engine: `allowedVersions` worked for cleanly-versioned packages (Testcontainers) but silently no-op'd for SteamKit2, whose nuget version list contains `2.5.0-beta.*` and `3.0.0-alpha.*` between current and target. Switching the rule to `versioning: semver` (and `=2.4.1` instead of the bare floor) fixed it. The whole multi-cycle debugging arc was the cost; `enabled: false` works as a blunt fallback but is a total freeze, not a cap.

**How to apply:** When adding or editing any `allowedVersions` (or other version-constraint) rule in `renovate.json` for a `nuget` dependency, add `"versioning": "semver"`, and use explicit comparators (`=X.Y.Z` to pin, `<X.0.0` to cap — never a bare version, which is a `>=` floor). Then verify the rule actually suppresses its target before trusting it: `renovate --platform=local --dry-run=lookup` (with `LOG_LEVEL=debug`) and check the "flattened updates found" list / per-dep `updates` array for the package. Do not assume a merged config works — Renovate applies the new rule only on a full re-scan (a PR rebase replays the old commit and does not re-evaluate), so a wrong constraint can look like it merged fine while doing nothing. Same shape as `runtime-post-conditions-are-gates.md` (run the thing, don't trust the claim) but specific to Renovate's per-package versioning-scheme trap.
