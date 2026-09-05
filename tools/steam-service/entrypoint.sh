#!/bin/sh

# steam-service drops to USER_ID:GROUP_ID (default 1000, non-root) so the game-data volume it
# shares with the server/client containers is owned by the same user across the whole stack —
# production and the E2E harness alike. It starts as root only long enough to chown the volumes
# it writes, then drops via gosu. Set USER_ID=0 to stay root. (The image build's game-download
# stage runs dotnet directly, not through this entrypoint, so it is unaffected.)

set -e

USER_ID="${USER_ID:-1000}"
GROUP_ID="${GROUP_ID:-1000}"
GAME_DIR="${GAME_DIR:-/data/game}"
SESSION_DIR="${SESSION_DIR:-/data/steam-session}"

if [ "$USER_ID" != "0" ]; then
    # Fresh Docker named volumes start root-owned; the dropped user can't write them otherwise.
    mkdir -p "$GAME_DIR" "$SESSION_DIR"
    chown -R "${USER_ID}:${GROUP_ID}" "$GAME_DIR" "$SESSION_DIR"
    # gosu resets HOME to the target uid's passwd home ("/" when the uid has no entry), so HOME
    # must be set after the drop. Session and ticket state are written to SESSION_DIR
    # explicitly; /tmp just gives .NET/Steam a writable home for any first-run files.
    exec gosu "${USER_ID}:${GROUP_ID}" env HOME=/tmp dotnet SteamService.dll "$@"
fi

exec dotnet SteamService.dll "$@"
