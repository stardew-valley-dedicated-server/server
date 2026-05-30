# Plan: Close two gaps in `dependabot-dockerfile.yml`

## Context

`dependabot.yml`'s `docker` ecosystem only parses `FROM image:tag` directives. Versions pinned via `ARG`+`curl` in our Dockerfiles are not tracked by it, which is why `dependabot-dockerfile.yml` exists as a custom auto-bumping workflow for SMAPI / tmux / Bun. Audit of `docker/Dockerfile` and `docker/Dockerfile.test-client` against that workflow surfaced two gaps:

1. **SMAPI bump only patches `docker/Dockerfile`**, not `docker/Dockerfile.test-client`. Both files declare `ARG SMAPI_VERSION=4.5.1` (server `:2`, test-client `:11`) and both run the SMAPI installer. After the next SMAPI release the test-client image will silently drift behind the server image — breaking the test harness, which assumes the two ends run identical SMAPI.

2. **ffmpeg/BtbN is not tracked at all.** Both Dockerfiles install ffmpeg 8.1.1 from a date-stamped BtbN release (server `:175`, test-client `:140`). The Dockerfile comment (`Dockerfile.test-client:139`) explicitly states the two pins must match. There is no automation to keep them in sync or to discover upstream releases.

**Invariant (clarified by the user):** `docker/Dockerfile` is the single source of truth for shared pin versions. `docker/Dockerfile.test-client` follows the server. There is no scenario where the two should diverge intentionally.

Under that model the simplest correct workflow is: **read** the current version from the server Dockerfile only, **write** the new version to both files unconditionally — using a wildcard `sed` that matches whatever the test-client currently has, so the test-client converges on the server's value regardless of any pre-existing drift. No drift-detection guard is needed in the bump workflow because the wildcard write _corrects_ drift as a side effect of every bump.

## Scope guardrails

- Do **not** touch any Dockerfile other than `docker/Dockerfile` and `docker/Dockerfile.test-client` (matches the user's "skip modern" instruction; tools/discord-bot keeps its own Bun handling).
- Do **not** consolidate the existing per-tool block pattern into a generic helper. Mirror the SMAPI/tmux/Bun shape exactly — one block per tracked dep — per `simplest-solution.md`.
- tmux and Bun blocks stay as-is structurally (single-file each — tmux is server-only, Bun lives only in `tools/discord-bot/Dockerfile`). Each gets a 3-line empty-`LATEST` guard added (small adjacent fix).

## File to modify

- `.github/workflows/dependabot-dockerfile.yml` — single-file change.

## Changes

### Change A — Extend the SMAPI block to cover both Dockerfiles (server is canonical)

Add an empty-`LATEST` guard to the existing `Check for SMAPI updates` step (currently lines ~21-41) — read continues to come from `docker/Dockerfile` only:

```yaml
- name: Check for SMAPI updates
  id: smapi
  env:
      GH_TOKEN: ${{ github.token }}
  run: |
      set -euo pipefail
      CURRENT_VERSION=$(grep -oP 'ARG SMAPI_VERSION=\K[0-9.]+' docker/Dockerfile)
      echo "current_version=$CURRENT_VERSION" >> $GITHUB_OUTPUT

      LATEST_VERSION=$(gh api repos/Pathoschild/SMAPI/releases --jq '[.[] | select(.prerelease == false)][0].tag_name')
      if [ -z "$LATEST_VERSION" ] || [ "$LATEST_VERSION" = "null" ]; then
        echo "::error::Could not resolve latest SMAPI version from GitHub API"
        exit 1
      fi
      echo "latest_version=$LATEST_VERSION" >> $GITHUB_OUTPUT

      if [ "$CURRENT_VERSION" != "$LATEST_VERSION" ]; then
        echo "update_available=true" >> $GITHUB_OUTPUT
        echo "SMAPI update available: $CURRENT_VERSION -> $LATEST_VERSION"
      else
        echo "update_available=false" >> $GITHUB_OUTPUT
        echo "SMAPI is up to date: $CURRENT_VERSION"
      fi
```

(`set -euo pipefail` is added so an unexpected grep/jq pipeline failure aborts the step instead of silently producing empty values.)

Then extend `Update SMAPI in Dockerfile` (current line ~87) to write both files. The server uses an exact-match `sed` (`current → latest`) so a no-op there is suspicious; the test-client uses a **wildcard** `sed` so it converges on the new value no matter what was there before:

```yaml
- name: Update SMAPI in Dockerfiles
  if: steps.smapi.outputs.update_available == 'true'
  run: |
      set -euo pipefail
      sed -i "s/ARG SMAPI_VERSION=${{ steps.smapi.outputs.current_version }}/ARG SMAPI_VERSION=${{ steps.smapi.outputs.latest_version }}/" docker/Dockerfile
      sed -i "s/^ARG SMAPI_VERSION=.*/ARG SMAPI_VERSION=${{ steps.smapi.outputs.latest_version }}/" docker/Dockerfile.test-client
```

The PR-creation step is unchanged — the branch `deps/smapi-<version>` and the PR body remain valid; the diff just covers two files instead of one (or one file plus a drift correction, in the unusual case test-client had drifted).

### Change B — Add an ffmpeg block

Append a new block after the Bun block, structurally identical to the existing three. The ffmpeg pin has three parts that must change together: the **release tag** (`autobuild-YYYY-MM-DD-HH-MM`), the **asset stem** (`ffmpeg-n<UPSTREAM>-linux64-gpl-<GPL>` — used as both the tarball name and the extracted directory), and the **upstream version in the leading comment** (`Install ffmpeg <UPSTREAM>`).

The release tag is the unit of "version" for branch/PR naming — it's guaranteed unique per BtbN release, whereas the upstream `n8.1.1` could repeat across multiple BtbN rebuilds.

Read from server only; write to both files. Server uses exact-match `sed`; test-client uses wildcard `sed` so it converges on the server's new values regardless of starting state.

```yaml
- name: Check for ffmpeg (BtbN) updates
  id: ffmpeg
  env:
      GH_TOKEN: ${{ github.token }}
  run: |
      set -euo pipefail
      CURRENT_TAG=$(grep -oP 'FFmpeg-Builds/releases/download/\Kautobuild-[A-Za-z0-9-]+' docker/Dockerfile | head -1)
      CURRENT_STEM=$(grep -oP 'ffmpeg-n[0-9.]+-linux64-gpl-[0-9.]+' docker/Dockerfile | head -1)
      CURRENT_UPSTREAM=$(echo "$CURRENT_STEM" | grep -oP 'n\K[0-9.]+(?=-linux64)')
      echo "current_tag=$CURRENT_TAG" >> $GITHUB_OUTPUT
      echo "current_stem=$CURRENT_STEM" >> $GITHUB_OUTPUT
      echo "current_upstream=$CURRENT_UPSTREAM" >> $GITHUB_OUTPUT

      # Pick the most recent autobuild-* release (the moving "latest" tag is excluded by the prefix filter).
      LATEST_JSON=$(gh api repos/BtbN/FFmpeg-Builds/releases --jq '[.[] | select(.tag_name | startswith("autobuild-"))][0]')
      LATEST_TAG=$(echo "$LATEST_JSON" | jq -r '.tag_name')
      # Resolve the matching asset(s) once; assert exactly one. >1 means BtbN's naming convention changed.
      MATCHING_ASSETS_JSON=$(echo "$LATEST_JSON" | jq -r '[.assets[].name | select(test("^ffmpeg-n[0-9.]+-linux64-gpl-[0-9.]+\\.tar\\.xz$"))]')
      MATCHING_ASSET_COUNT=$(echo "$MATCHING_ASSETS_JSON" | jq -r 'length')
      if [ "$MATCHING_ASSET_COUNT" != "1" ]; then
        echo "::error::Expected exactly 1 BtbN asset matching ffmpeg-nX-linux64-gpl-Y.tar.xz, found $MATCHING_ASSET_COUNT. BtbN naming convention may have changed; investigate manually."
        exit 1
      fi
      LATEST_STEM=$(echo "$MATCHING_ASSETS_JSON" | jq -r '.[0]' | sed 's/\.tar\.xz$//')
      if [ -z "$LATEST_TAG" ] || [ "$LATEST_TAG" = "null" ] || [ -z "$LATEST_STEM" ] || [ "$LATEST_STEM" = "null" ]; then
        echo "::error::Could not resolve latest BtbN ffmpeg release/asset"
        exit 1
      fi
      LATEST_UPSTREAM=$(echo "$LATEST_STEM" | grep -oP 'n\K[0-9.]+(?=-linux64)')
      echo "latest_tag=$LATEST_TAG" >> $GITHUB_OUTPUT
      echo "latest_stem=$LATEST_STEM" >> $GITHUB_OUTPUT
      echo "latest_upstream=$LATEST_UPSTREAM" >> $GITHUB_OUTPUT

      if [ "$CURRENT_TAG" != "$LATEST_TAG" ]; then
        echo "update_available=true" >> $GITHUB_OUTPUT
        echo "ffmpeg update available: $CURRENT_TAG ($CURRENT_STEM) -> $LATEST_TAG ($LATEST_STEM)"
      else
        echo "update_available=false" >> $GITHUB_OUTPUT
        echo "ffmpeg is up to date: $CURRENT_TAG"
      fi

- name: Update ffmpeg in Dockerfiles
  if: steps.ffmpeg.outputs.update_available == 'true'
  run: |
      set -euo pipefail
      # Server: exact-match (canonical source — a no-op here would be suspicious).
      sed -i "s|${{ steps.ffmpeg.outputs.current_tag }}|${{ steps.ffmpeg.outputs.latest_tag }}|g" docker/Dockerfile
      sed -i "s|${{ steps.ffmpeg.outputs.current_stem }}|${{ steps.ffmpeg.outputs.latest_stem }}|g" docker/Dockerfile
      sed -i "s|Install ffmpeg ${{ steps.ffmpeg.outputs.current_upstream }}|Install ffmpeg ${{ steps.ffmpeg.outputs.latest_upstream }}|" docker/Dockerfile
      # Test-client: wildcard match (mirrors server regardless of starting state).
      sed -i "s|autobuild-[A-Za-z0-9-]\+|${{ steps.ffmpeg.outputs.latest_tag }}|g" docker/Dockerfile.test-client
      sed -i "s|ffmpeg-n[0-9.]\+-linux64-gpl-[0-9.]\+|${{ steps.ffmpeg.outputs.latest_stem }}|g" docker/Dockerfile.test-client
      sed -i "s|Install ffmpeg [0-9.]\+|Install ffmpeg ${{ steps.ffmpeg.outputs.latest_upstream }}|" docker/Dockerfile.test-client

- name: Create ffmpeg Pull Request
  if: steps.ffmpeg.outputs.update_available == 'true'
  uses: peter-evans/create-pull-request@c0f553fe549906ede9cf27b5156039d195d2ece0 # v8.1.0
  with:
      token: ${{ secrets.GITHUB_TOKEN }}
      commit-message: "chore(deps): bump ffmpeg from ${{ steps.ffmpeg.outputs.current_tag }} to ${{ steps.ffmpeg.outputs.latest_tag }}"
      title: "chore(deps): bump ffmpeg from ${{ steps.ffmpeg.outputs.current_tag }} to ${{ steps.ffmpeg.outputs.latest_tag }}"
      body: |
          Bumps [ffmpeg (BtbN builds)](https://github.com/BtbN/FFmpeg-Builds) from `${{ steps.ffmpeg.outputs.current_tag }}` (`${{ steps.ffmpeg.outputs.current_stem }}`) to `${{ steps.ffmpeg.outputs.latest_tag }}` (`${{ steps.ffmpeg.outputs.latest_stem }}`).

          Release notes: https://github.com/BtbN/FFmpeg-Builds/releases/tag/${{ steps.ffmpeg.outputs.latest_tag }}

          ---
          This PR was automatically created by the [Dependabot: Dockerfile](${{ github.server_url }}/${{ github.repository }}/actions/workflows/dependabot-dockerfile.yml) workflow.
      branch: deps/ffmpeg-${{ steps.ffmpeg.outputs.latest_tag }}
      labels: dependencies
      delete-branch: true

- name: Reset working directory
  if: steps.ffmpeg.outputs.update_available == 'true'
  run: git checkout -- .
```

### Change C — Add empty-`LATEST` guards to existing tmux and Bun blocks

Small adjacent fix. Existing tmux/Bun checks `gh api ...` then immediately compare; an API failure leaves `LATEST_VERSION=""` and the comparison spuriously says "update available" with empty target. Add the same guard already present in the SMAPI revision:

```bash
if [ -z "$LATEST_VERSION" ] || [ "$LATEST_VERSION" = "null" ]; then
  echo "::error::Could not resolve latest <tmux|Bun> version from GitHub API"
  exit 1
fi
```

Three lines per block, inserted directly after each `LATEST_VERSION=$(gh api ...)` line. No structural change, no behavior change on the happy path.

## Why these specific design choices

- **Server is canonical; test-client mirrors via wildcard sed.** The user's stated invariant is "server is the source of truth, test-client follows." Reading from the server only and writing to the test-client with a regex that matches whatever's there means a single bump always converges both files on the same value — no drift detection needed because drift is corrected by construction. Simpler than reading both, asserting equal, then writing both.
- **Server bump still uses an exact-match `sed`.** Wildcard for the canonical file would mask bugs (e.g. workflow runs against a Dockerfile with an unexpected shape). Exact-match makes a no-op there a loud signal that something upstream has changed.
- **Single regex over the whole stem `ffmpeg-n…-linux64-gpl-…`.** BtbN's two version components (`n<UPSTREAM>` and `gpl-<MAJOR.MINOR>`) drift independently across releases (verified by Explore agent). Treating the stem as one opaque token avoids splitting and re-assembling — three sed substitutions per file (tag, stem, comment) instead of five.
- **Branch name keys off `latest_tag`, not `latest_upstream`.** BtbN can ship multiple releases for the same upstream version; the date-stamped tag is the only guaranteed-unique identifier per release.
- **Comment substitution is included.** `# Install ffmpeg 8.1.1.` becoming a lie after the next bump is the kind of silent rot the comment is supposed to prevent. One extra `sed` line keeps the comment honest.
- **Empty-`LATEST` guards added to all blocks.** Existing tmux/Bun blocks were missing these; small, adjacent fix per `simplest-solution.md`.

## Verification (post-conditions, runtime gates)

These are runtime checks per `runtime-post-conditions-are-gates.md` — run them, don't infer.

**Heads-up:** the current ffmpeg pin is `autobuild-2026-05-05-13-19`; today's BtbN latest is `autobuild-2026-05-09-13-18`. The first dispatch after merge **will** open an ffmpeg PR. That's the intended catch-up; it doesn't indicate a bug. The SMAPI block's first run is currently a no-op (versions match upstream).

1. **Pre-merge manual dispatch on the feature branch** (versions read from the branch's Dockerfiles):
    - Push the workflow change to a branch.
    - `gh workflow run dependabot-dockerfile.yml --ref <branch>` (the `--ref` flag is required to dispatch from a non-default branch).
    - `gh run watch <run-id>`.
    - **Expected:** SMAPI step logs `SMAPI is up to date`, `update_available=false`, no SMAPI PR (assuming SMAPI hasn't released between merge and dispatch — if it has, an SMAPI PR also opens, which is correct). ffmpeg step logs `update_available=true` and opens a PR on `deps/ffmpeg-autobuild-2026-05-09-13-18` (or whatever the latest BtbN tag is at dispatch time). No `::error::` lines appear in any step.

2. **Drift-correction smoke test — prove the wildcard sed actually heals divergence** (locally, on a Linux/WSL/Git-Bash shell — `grep -oP` and `sed -i` semantics differ on macOS/PowerShell):
    - On a throwaway branch, temporarily edit `docker/Dockerfile.test-client:11` to `ARG SMAPI_VERSION=4.0.0` (a fake older value).
    - Run the wildcard sed directly, simulating what the workflow would do for a bump to e.g. `4.5.2`:
        ```bash
        sed -i "s/^ARG SMAPI_VERSION=.*/ARG SMAPI_VERSION=4.5.2/" docker/Dockerfile.test-client
        grep '^ARG SMAPI_VERSION=' docker/Dockerfile.test-client
        ```
    - **Expected:** `ARG SMAPI_VERSION=4.5.2`. Revert (`git checkout -- docker/Dockerfile.test-client`).
    - Repeat for ffmpeg by editing one file's `autobuild-…` tag and stem to fake older values, then running the three wildcard `sed` lines from Change B against it.

3. **End-to-end bump verification** (after the workflow PRs land):
    - Confirm SMAPI PR diff touches both `docker/Dockerfile` and `docker/Dockerfile.test-client`.
    - Confirm ffmpeg PR diff touches in each file: the release tag (1 hit), the stem (3 hits — URL filename + two `install` paths), and the leading comment (1 hit).
    - Check out the PR branch and run `make build` and `make build-test-client`. Both must succeed (proves the new download URLs and tarball stems are real, not just regex-plausible).
    - Per `verify-edit-landed-in-artifact.md`, after the build, spot-check the produced image with a one-shot container run (cross-platform; no host-filesystem cp):
      `docker run --rm <tag> ffmpeg -version | head -1` — confirm the printed version matches `latest_upstream`.

4. **Schema sanity for `gh api`** (one-shot, before merge):
    - `gh api repos/BtbN/FFmpeg-Builds/releases --jq '[.[] | select(.tag_name | startswith("autobuild-"))][0].tag_name'` returns a non-empty `autobuild-YYYY-MM-DD-HH-MM` string.
    - `gh api repos/BtbN/FFmpeg-Builds/releases --jq '[.[] | select(.tag_name | startswith("autobuild-"))][0] | [.assets[].name | select(test("^ffmpeg-n[0-9.]+-linux64-gpl-[0-9.]+\\.tar\\.xz$"))] | length'` returns exactly `1`.

## What is NOT in this plan

- `polybar-themes` git clone pin (mentioned in prior audit). Out of scope — different mechanism (no version axis to track via API), separate PR.
- Generalizing the per-tool block into a reusable composite action. Premature; four blocks of ~30 lines each is fine per `simplest-solution.md`.
- Drift guard / drift detection of any kind. The wildcard write design eliminates the need: every bump corrects drift as a side effect. Empty-`LATEST` guard _is_ added to all blocks (Change C).
- Switching ffmpeg to BtbN's moving `latest` tag. Explicitly rejected — the Dockerfile comment pins away from it deliberately to prevent rebuild non-determinism.
