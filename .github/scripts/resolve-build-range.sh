#!/usr/bin/env bash
# Resolves a build's commit range: the head commit down to the nearest matching tag strictly
# below it. The single definition of "what shipped in this build", shared by the
# stamp-issue-versions and build-changelog actions so issue stamping and the changelog can
# never disagree on the range.
#
# Inputs (env):
#   HEAD_REF  - commit-ish of the build's head (SHA or tag name)
#   TAG_MATCH - space-separated `git describe --match` globs selecting base-tag candidates
# Outputs (appended to $GITHUB_OUTPUT):
#   head-oid, base-oid, base-tag
#
# Requires full history and tags in the current checkout (fetch-depth: 0, fetch-tags: true).
set -euo pipefail
set -f # tag globs must reach git verbatim, not expand against the working tree

HEAD_OID=$(git rev-parse "$HEAD_REF^{commit}")
MATCH_ARGS=()
for glob in $TAG_MATCH; do MATCH_ARGS+=(--match "$glob"); done
# The base must sit strictly below HEAD. A candidate tag pointing at HEAD itself is this
# build's own tag or a previous build of the same commit — taking it as base would empty
# the range. Both consumers tolerate re-walking an already-processed range (stamping is
# first-value-wins and marker-deduped; the changelog post just repeats).
EXCLUDE_ARGS=()
for tag in $(git tag -l --points-at "$HEAD_OID" $TAG_MATCH); do
  EXCLUDE_ARGS+=(--exclude "$tag")
done
BASE=$(git describe --tags --abbrev=0 "${MATCH_ARGS[@]}" ${EXCLUDE_ARGS[@]+"${EXCLUDE_ARGS[@]}"} "$HEAD_OID" 2>/dev/null) || {
  echo "::error::no base tag matching '$TAG_MATCH' strictly below $HEAD_REF"
  exit 1
}
BASE_OID=$(git rev-parse "$BASE^{commit}")
echo "HEAD=$HEAD_OID BASE=$BASE ($BASE_OID)"
{
  echo "head-oid=$HEAD_OID"
  echo "base-oid=$BASE_OID"
  echo "base-tag=$BASE"
} >> "$GITHUB_OUTPUT"
