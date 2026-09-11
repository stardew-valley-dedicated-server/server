#!/usr/bin/env bash
# Posts a release/preview message to a Discord webhook by running the discord-notify action's own
# script locally, so a test post can't drift from what CI sends. Mirrors the notification step in
# .github/workflows/build-preview.yml / build-release.yml — keep the embed texts below in sync.
#
#   DISCORD_WEBHOOK='https://discord.com/api/webhooks/...' tools/discord-notify-test.sh preview 1.5.0-preview.136
#   tools/discord-notify-test.sh preview 1.5.0-preview.136            # no webhook: prints the payload (dry run)
#
# Arguments: <preview|release> <version> [changelog-markdown]. The changelog markdown is the
# `## Changes` block that build-changelog.js emits. Pass it as the third argument, pipe it on
# stdin (e.g. `build-changelog | tools/discord-notify-test.sh preview 1.5.0`), or omit it for a
# short sample.
set -euo pipefail

channel="${1:?usage: $0 <preview|release> <version> [changelog-markdown]}"
version="${2:?usage: $0 <preview|release> <version> [changelog-markdown]}"
if [ -n "${3:-}" ]; then
    changelog="$3"
elif [ ! -t 0 ]; then
    changelog="$(cat)"   # piped in, e.g. build-changelog | tools/discord-notify-test.sh preview VERSION
else
    changelog="## Changes
### Features
- one-command install and update for operators ([#642](https://github.com/stardew-valley-dedicated-server/server/pull/642))
### Bug Fixes
- keep Discord release posts within the embed limit ([#644](https://github.com/stardew-valley-dedicated-server/server/pull/644))"
fi

case "$channel" in
    preview) username="Preview Bot"; color="14524706"; word="Build" ;;   # amber, docs #dda122
    release) username="Release Bot"; color="7864174"; word="Release" ;;  # logo green, #77FF6E
    *) echo "channel must be preview or release" >&2; exit 1 ;;
esac

hub="https://hub.docker.com/r/sdvd/server/tags?name=${version}"
compare="https://github.com/stardew-valley-dedicated-server/server/compare/a...b"

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
action_script="$(node -e '
    const yaml = require("js-yaml"), fs = require("fs");
    process.stdout.write(yaml.load(fs.readFileSync(process.argv[1], "utf8")).runs.steps[0].run);
' "$repo_root/.github/actions/discord-notify/action.yml")"

WEBHOOK="${DISCORD_WEBHOOK:-}" \
DRY_RUN="$([ -n "${DISCORD_WEBHOOK:-}" ] && echo false || echo true)" \
CONTENT="" TITLE="" URL="" \
COLOR="$color" \
DESCRIPTION="# ${word} [${version}](${hub}) is available!
${changelog}" \
EMBED2_TITLE="" \
EMBED2_DESCRIPTION='## Install or update
Run from your server folder to update, or from an empty folder for a new server.
### Linux/macOS
`curl -fsSL https://docs.junimoserver.com/install.sh | bash`
### Windows (PowerShell)
`powershell -c "irm https://docs.junimoserver.com/install.ps1 | iex"`' \
USERNAME="$username" AVATAR_URL="" FIELDS="[]" \
BUTTONS="[{\"label\":\"Upgrade guide\",\"url\":\"https://docs.junimoserver.com/admins/operations/upgrading\"},{\"label\":\"See all changes\",\"url\":\"${compare}\"}]" \
bash -c "$action_script"
