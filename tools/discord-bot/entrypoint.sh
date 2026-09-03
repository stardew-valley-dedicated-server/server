#!/bin/sh

# Starts as root to take ownership of the dashboard-state volume, then drops to the configured
# USER_ID:GROUP_ID (default 1000). Fresh Docker named volumes start root-owned, so the chown is
# what lets the non-root user write /data. Matches the server and steam-auth containers so the
# whole stack runs as the operator's configured host user.

set -e

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"

mkdir -p /data
chown -R "${USER_ID}:${GROUP_ID}" /data

# su-exec resets HOME to the target uid's passwd home ("/" when the uid has no entry), so HOME
# is set after the drop. The bot writes its state to /data explicitly; /tmp just gives Bun a
# writable home for any runtime files.
exec su-exec "${USER_ID}:${GROUP_ID}" env HOME=/tmp bun run src/index.ts
