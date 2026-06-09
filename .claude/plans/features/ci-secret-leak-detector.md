# CI Secret-Leak Detector (post-run, private alert)

## Goal

After any workflow that uses our secrets finishes, automatically scan that run's
logs for any **secret value appearing unmasked**, and if one is found, alert
privately (Discord webhook) — without re-leaking the value in the process.

This is a **security** guard (detect a real exposure), distinct from the cosmetic
dash-over-masking bug (see `docs/developers/testing/ci-log-masking-runbook.md`).

## Decisions (from clarifying questions)

- **Detect:** real secret leaks — a secret *value* present unmasked in a run log.
- **Trigger:** `workflow_run` (`types: [completed]`) — fires when a secret-using
  workflow finishes. Immediate, per-run coverage.
- **Alert:** Discord webhook via the existing `DISCORD_WEBHOOK_URL` secret, mirroring
  the established `curl -d @- "$DISCORD_WEBHOOK"` pattern in `deploy-server.yml` /
  `build-release.yml` / `deploy-docs.yml`. Private, no new setup.

## The hard constraint (read first — it shapes the whole design)

GitHub already masks registered secret values in logs. So a leak only reaches the
log when a secret's value was **transformed** before printing (decoded, reformatted,
substring-sliced, re-encoded) so the printed bytes no longer match the registered
mask. Examples from this repo's history: `jq -r .sshKey` would emit the *decoded*
PEM (real newlines) which doesn't match the single-line registered secret; a
base64-decoded value printed raw; a JSON value pretty-printed.

This has two consequences the detector must respect:

1. **It cannot rely on "grep the raw secret value"** for transformed leaks — the
   leaked bytes differ from the stored value. Raw-value grep catches only the
   *verbatim* leak (still worth catching: an `echo "$SECRET"` that slipped past
   masking because the step disabled masking, or a value GitHub failed to register).
2. **The detector must never print a match.** Echoing the matched line re-leaks it
   into *this* job's (also public-by-default) log. Report **by secret name + step +
   line number only**, never the value or surrounding text.

### What this detector CAN and CANNOT do (set expectations honestly)

- **CAN:** catch a verbatim secret value in the triggering run's logs (value equals
  what we scan for). CAN catch known transformed forms we explicitly derive (e.g.
  also scan for the base64 and the real-newline variant of a key). CAN flag the
  over-masking-risk signal (a multiline registered secret) as a related hygiene check.
- **CANNOT:** catch an *arbitrary* transformation we didn't anticipate — there's no
  general way to detect "some function of the secret" in text. This is a high-value
  tripwire for the known/verbatim cases, **not** a proof of no-leak. Document this in
  the workflow header so no one mistakes a green check for a guarantee.

## Where does the detector get the values to scan for?

The detector job is a *separate* workflow. To scan for secret X it needs X's value.
Options, with the trade-off:

- **(A) Scan for the values we control, sourced from the same secrets store.** The
  detector job declares the same env-secrets in its own `env:` (e.g.
  `SDVD_DOCKER_HOSTS`, `STEAM_ACCOUNTS`, R2 creds, the inline SSH key derived from
  `SDVD_DOCKER_HOSTS`). It then scans the downloaded log for each value **and known
  transforms** (raw, base64, real-newline PEM body, the bare host IP). This is the
  workable core. The detector holds the same secrets the scanned run does — no new
  exposure surface beyond what already exists.
- **(B) Hash-compare without holding values.** Not viable: GitHub gives no per-run
  list of registered secret *values* or their hashes to compare against. We can only
  scan for values we can name.

→ **Go with (A).** Enumerate the secrets explicitly (a maintained list), scan for
each value + its known transforms, report matches by name only.

## Architecture

New workflow `.github/workflows/secret-leak-scan.yml`:

```
on:
  workflow_run:
    workflows:
      - "E2E Tests"
      - "Deploy Server"
      - "Build Preview"
      - "Build Release"
      - "Deploy Docs"
      # (every workflow that injects a real secret value into a step)
    types: [completed]

permissions:
  actions: read        # to download the triggering run's logs
  # no contents/write needed
```

Single job, `runs-on: ubuntu-latest`, environment `test-vps` (so the same secrets
are in scope). Steps:

1. **Download the triggering run's logs.**
   `gh api repos/${{ github.repository }}/actions/runs/${{ github.event.workflow_run.id }}/logs > logs.zip`
   (auth via the job's `GITHUB_TOKEN`; `actions: read` permission). Unzip to a dir.

2. **Build the scan-target list (in-memory, never written to disk/log).** For each
   named secret, emit the raw value and its known transforms:
   - raw value
   - base64 of the value
   - for the SSH key inside `SDVD_DOCKER_HOSTS`: the real-newline PEM body and the
     bare endpoint IP (the two historically-leaky transforms)
   Build this as a NUL-separated list in a shell var, not a file.

3. **Scan.** For each target, `grep -rFl` (fixed-string, list filenames only —
   **never** `grep` without `-l`, which would print the matching line). Collect
   `(secretName, file, count)` tuples. Use `grep -c` per file for counts. At no point
   echo the target or the matched line.
   - Skip empty/very-short targets (len < 8) to avoid false positives on common
     fragments — and log that they were skipped (no silent caps, per
     `holistic-or-explicit-todo.md`).

4. **Multiline-hygiene sub-check (cheap, related).** For each named secret value,
   `printf '%s' "$VALUE" | tr -cd '\n' | wc -c`; if > 0, add a WARNING line: "secret
   <NAME> is multiline → over-masking risk (see ci-log-masking-runbook.md)". This
   folds the over-mask root cause into the same guard.

5. **Report.**
   - **No findings:** exit 0 silently (or a single "scan clean" line). No Discord spam
     on the happy path.
   - **Findings:** POST to Discord via `DISCORD_WEBHOOK_URL`, body listing
     `secretName → step/file (N occurrences)` and the multiline warnings —
     **names and locations only, zero values**. Include a link to the triggering run
     (`github.event.workflow_run.html_url`).
   - Also `::error::` annotate so the scan job itself is red (visible in the Actions
     tab) — but the *content* of the alert stays in Discord, not the public log.

6. **Self-protection.** The scan job prints neither the targets nor matched lines.
   Its own log must stay clean even if it finds a leak. Guard every `grep`/`echo`
   accordingly. `set -euo pipefail` with explicit guards so a missing log/zip is a
   clean skip, not a crash.

## The maintained secret list (single source of truth)

A documented list in the workflow of which secrets carry real values worth scanning
for, kept in sync when secrets are added. Per `verify-documented-config-is-consumed.md`,
each name in the list must be a real secret in the `test-vps`/repo store. Start with:
`SDVD_DOCKER_HOSTS` (+ derived inline key, endpoint IP), `STEAM_ACCOUNTS`,
`R2_ACCESS_KEY_ID`, `R2_ACCOUNT_ID`, `R2_SECRET_ACCESS_KEY`, `STEAM_PASSWORD`,
`STEAM_USERNAME`, `DOCKERHUB_TOKEN`, `DISCORD_WEBHOOK_URL`.

## Failure isolation

The scan must never affect the triggering run (it's a separate workflow — it
structurally can't). The scan job failing (network, missing log) must alert but not
loop: `continue-on-error` on the download step, clean skip if no logs.

## Files

- `.github/workflows/secret-leak-scan.yml` — the detector (new).
- `docs/developers/contributing/ci-cd.md` — short section: what the scan does, its
  CAN/CANNOT limits, and how to extend the secret list.
- (No change to the scanned workflows — `workflow_run` is decoupled.)

## Verification gates (runtime, per runtime-post-conditions-are-gates.md)

- **Positive control:** temporarily add a step to a test branch that deliberately
  prints a known non-sensitive sentinel registered as a dummy secret; confirm the scan
  detects it, Discord alert fires with the *name only* (no value), scan job goes red.
  Remove the sentinel after.
- **No-leak control:** a normal clean run produces no Discord message and a green scan.
- **No-re-leak audit:** read the scan job's OWN log on a positive-control run — confirm
  it contains **neither** the sentinel value **nor** any matched line text. This is the
  load-bearing gate; if the detector leaks while detecting, it's worse than nothing.
- **Multiline sub-check:** point it at a deliberately multiline dummy secret; confirm
  the hygiene warning fires.

## Open questions / risks

- **Coverage honesty:** must be documented as a tripwire for known/verbatim leaks, not
  a guarantee (the arbitrary-transform gap). A green scan ≠ "no secret leaked."
- **Cross-repo secret scope:** `workflow_run` runs in the context of the default
  branch's workflow file with repo/its-environment secrets — confirm the detector's
  `environment:` gives it the `test-vps` env secrets (it should, same as e2e). Verify
  before relying on it.
- **Headless auth caveat:** the `gh api .../logs` download needs `actions: read`;
  confirm the job token has it under the `permissions:` block.
```
