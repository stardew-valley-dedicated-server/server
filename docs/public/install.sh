#!/usr/bin/env bash
# JunimoServer install/update: one script for both.
#
#   curl -fsSL https://docs.junimoserver.com/install.sh | bash
#
# Run it in your server folder to update in place, or in an empty folder to set up a new server
# (it scaffolds ./junimoserver). Set DIR=<path> to choose the new-server directory.
#
# A fresh install generates a secure API key, then (interactively) offers to sign you in to Steam
# and start the server. Update confirms the release channel and asks whether to restart.
# Non-interactive callers skip all prompts: set IMAGE_VERSION=preview|latest to choose the channel,
# or NO_TTY=1 to keep the current channel (recommended on a fresh install) and restart automatically.
set -euo pipefail

REPO="stardew-valley-dedicated-server/server"
MASTER="https://raw.githubusercontent.com/${REPO}/master"

# Channel recommended to new installs. Preview is recommended while a working stable ('latest')
# release is still being finalized; flip to "stable" once latest ships.
RECOMMENDED_CHANNEL="preview"
# Shown in the channel prompt so users don't reflexively pick stable. Clear it once stable is ready.
RECOMMENDED_NOTE="stable isn't ready yet"
NO_TTY="${NO_TTY:-0}"

die() { echo "Error: $*" >&2; exit 1; }

# True when we may prompt: a terminal is available and the caller didn't opt out with NO_TTY.
is_interactive() { [ "$NO_TTY" != "1" ] && [ -r /dev/tty ]; }

# Print a prompt to the terminal, read one line back, and echo it lowercased (empty on read failure).
ask_tty() {
    local prompt="$1" reply=""
    printf '%s' "$prompt" > /dev/tty 2>/dev/null || true
    IFS= read -r reply < /dev/tty || reply=""
    printf '%s' "$reply" | tr '[:upper:]' '[:lower:]'
}

# The IMAGE_VERSION value for the recommended channel (stable -> latest, otherwise preview).
recommended_version() { [ "$RECOMMENDED_CHANNEL" = "stable" ] && echo "latest" || echo "preview"; }

# Echo the uncommented value of a key in an env file; empty if only commented or absent.
get_env_value() {
    local file="$1" key="$2"
    sed -nE "s/^[[:space:]]*${key}=[\"']?([^\"'#[:space:]]*).*/\1/p" "$file" | head -n1
}

# Echo the sorted, unique variable names declared in an env file (commented or not).
env_keys() {
    sed -nE 's/^[[:space:]]*#?[[:space:]]*([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*=.*/\1/p' "$1" | sort -u
}

# Echo the raw.githubusercontent base URL for the commit an already-pulled image was built from
# (docker/Dockerfile bakes it as ENV SDVD_GIT_SHA), or the master branch when it's unknown. This is
# how docker-compose.yml / .env.example are fetched to match the image, without needing releases.
raw_base_for_image() {
    local image="$1" sha
    sha="$(docker image inspect "$image" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null | sed -n 's/^SDVD_GIT_SHA=//p' | head -n1 || true)"
    # Only a plain commit hash resolves on GitHub; local builds stamp "unknown" or "<sha>-dirty".
    case "$sha" in
        "" | *[!0-9a-f]*) echo "$MASTER" ;;
        *)                echo "https://raw.githubusercontent.com/${REPO}/${sha}" ;;
    esac
}

# Generate a random hex token (32 bytes -> 64 hex chars) for secure-by-default secrets.
generate_key() { head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'; }

# Set KEY=VALUE in an env file, replacing the first commented-or-uncommented KEY= line, else appending.
set_env_var() {
    local file="$1" key="$2" value="$3" tmp
    if grep -qE "^[[:space:]]*#?[[:space:]]*${key}=" "$file"; then
        tmp="$(mktemp)"
        awk -v k="$key" -v v="$value" '
            !done && $0 ~ ("^[[:space:]]*#?[[:space:]]*" k "=") { print k "=" v; done=1; next }
            { print }
        ' "$file" > "$tmp" && mv "$tmp" "$file"
    else
        printf '%s=%s\n' "$key" "$value" >> "$file"
    fi
}

# Resolve the IMAGE_VERSION to write. Args: mode (install|update), current value (update only).
# Precedence: explicit IMAGE_VERSION env > interactive prompt > keep current (update) / recommended.
# The prompt goes to the terminal; only the chosen value is written to stdout for the caller.
resolve_channel_version() {
    local mode="$1" current="${2:-}" default prompt reply
    if [ -n "${IMAGE_VERSION:-}" ]; then echo "$IMAGE_VERSION"; return; fi
    if ! is_interactive; then
        if [ "$mode" = "update" ] && [ -n "$current" ]; then echo "$current"; else recommended_version; fi
        return
    fi
    if [ "$mode" = "update" ]; then
        default="${current:-$(recommended_version)}"
        prompt="Release channel? preview or stable [${default}]: "
    elif [ "$RECOMMENDED_CHANNEL" = "preview" ]; then
        default="$(recommended_version)"
        prompt="Release channel? preview (recommended${RECOMMENDED_NOTE:+; $RECOMMENDED_NOTE}) or stable [preview]: "
    else
        default="$(recommended_version)"
        prompt="Release channel? preview or stable (recommended${RECOMMENDED_NOTE:+; $RECOMMENDED_NOTE}) [stable]: "
    fi
    reply="$(ask_tty "$prompt")"
    case "$reply" in
        p|preview) echo "preview" ;;
        s|stable)  echo "latest" ;;
        *)         echo "$default" ;;
    esac
}

# Ask whether to restart now to apply a staged update. Non-interactive runs return success so that
# unattended updates still apply; interactively the default is yes (Enter).
should_restart() {
    is_interactive || return 0
    case "$(ask_tty 'Restart now to apply the update? [Y/n]: ')" in
        n|no) return 1 ;;
        *)    return 0 ;;
    esac
}

command -v docker >/dev/null 2>&1 || die "Docker is not installed or not on PATH."
docker compose version >/dev/null 2>&1 || die "The Docker Compose plugin is required (docker compose v2)."
command -v curl >/dev/null 2>&1 || die "curl is required."

# Update in place when the current directory already holds a server, else scaffold a new one.
if [ -f docker-compose.yml ]; then
    target="$(pwd)"
else
    target="${DIR:-junimoserver}"
    mkdir -p "$target"
fi

# Run the rest in a subshell so the script never changes the caller's directory, even on abort.
(
cd "$target"

if [ -f docker-compose.yml ]; then
    # ── Update ────────────────────────────────────────────────────────────────────────────────
    echo "Updating server in $(pwd)"
    echo ""

    # Confirm the channel before pulling: IMAGE_VERSION decides which image Compose fetches.
    current_version=""
    if [ -f .env ]; then current_version="$(get_env_value .env IMAGE_VERSION)"; fi
    [ -n "$current_version" ] || current_version="latest"
    channel_version="$(resolve_channel_version update "$current_version")"
    if [ "$channel_version" != "$current_version" ]; then
        echo "Switching channel: ${current_version} -> ${channel_version}"
        echo "Back up your saves first; save formats can differ between builds."
        [ -f .env ] && set_env_var .env IMAGE_VERSION "$channel_version"
    fi
    echo "Channel: ${channel_version}"
    echo ""

    echo "Pulling images..."
    docker compose pull
    echo ""

    # Fetch the docker-compose.yml matching the image we just pulled (honors .env and any override).
    server_image="$(docker compose config --images 2>/dev/null | grep -E '^sdvd/server:' | head -n1 || true)"
    [ -n "$server_image" ] || server_image="sdvd/server:latest"
    raw_base="$(raw_base_for_image "$server_image")"

    echo "Fetching docker-compose.yml..."
    tmp_compose="$(mktemp)"
    curl -fsSL -o "$tmp_compose" "${raw_base}/docker-compose.yml" || die "Could not download docker-compose.yml from ${raw_base}/docker-compose.yml"
    mv "$tmp_compose" docker-compose.yml
    echo "Updated docker-compose.yml to match the new image."

    # New options ship in .env.example but your .env is never touched, so surface any you're missing.
    # Keys are matched commented or not, so an option you deliberately left commented isn't re-flagged.
    tmp_example="$(mktemp)"
    if [ -f .env ] && curl -fsSL -o "$tmp_example" "${raw_base}/.env.example" 2>/dev/null; then
        new_keys="$(comm -23 <(env_keys "$tmp_example") <(env_keys .env))"
        if [ -n "$new_keys" ]; then
            echo ""
            echo "New .env options are available this release (your .env is unchanged):"
            echo "$new_keys" | sed 's/^/  - /'
            echo "Add any you want to your .env. Docs: https://docs.junimoserver.com/admins/configuration/environment"
        fi
    fi
    rm -f "$tmp_example"
    echo ""

    if should_restart; then
        echo "Restarting..."
        docker compose up -d --remove-orphans
        echo ""
        echo "Update complete."
    else
        echo "Update staged. Apply it when you're ready with:"
        echo "  docker compose up -d"
    fi
    echo ""
else
    # ── Fresh install ─────────────────────────────────────────────────────────────────────────
    echo "Installing into $(pwd)"
    echo ""

    # Pick the channel first, then pull its server image so we can fetch the matching config.
    if [ -f .env ]; then
        channel_version="$(get_env_value .env IMAGE_VERSION)"
        [ -n "$channel_version" ] || channel_version="latest"
    else
        channel_version="$(resolve_channel_version install "")"
    fi
    server_image="sdvd/server:${channel_version}"

    echo "Pulling ${server_image}..."
    # An image that is already present locally (a `make build` tag, or an offline host) is fine.
    if ! docker pull "$server_image"; then
        docker image inspect "$server_image" >/dev/null 2>&1 || die "Could not pull ${server_image}. Check the channel/version and your network."
        echo "Using the local ${server_image} image."
    fi
    raw_base="$(raw_base_for_image "$server_image")"
    echo ""

    curl -fsSL -o docker-compose.yml "${raw_base}/docker-compose.yml" || die "Could not download docker-compose.yml from ${raw_base}/docker-compose.yml"
    if [ -f .env ]; then
        echo "Wrote docker-compose.yml. Kept existing .env (IMAGE_VERSION=${channel_version}, not overwritten)."
    else
        curl -fsSL -o .env "${raw_base}/.env.example" || die "Could not download .env.example from ${raw_base}/.env.example"
        set_env_var .env IMAGE_VERSION "$channel_version"
        # Secure by default: a strong random API key so the HTTP API is never left open.
        set_env_var .env API_KEY "$(generate_key)"
        # TODO: remove once a published image no longer refuses to start without VNC_PASSWORD
        # (the image-side gate now disables the VNC ports instead). Until then a fresh .env
        # without it crash-loops the server.
        set_env_var .env VNC_PASSWORD "$(generate_key | head -c 16)"
        echo "Wrote docker-compose.yml and .env (IMAGE_VERSION=${channel_version}, API_KEY and VNC_PASSWORD generated)."
    fi

    # Offer to finish now: sign in to Steam, start, open the console. Non-interactive prints steps.
    echo ""
    setup_now="n"
    is_interactive && setup_now="$(ask_tty 'Sign in to Steam and start the server now? [Y/n]: ')"
    case "$setup_now" in
        n|no)
            cat <<EOF

JunimoServer is installed in $(pwd). To finish:

  1. cd ${target}
  2. Sign in to Steam (enter your account; downloads the game):
       docker compose run --rm -it steam-auth setup
  3. Start the server:  docker compose up -d
  4. Open the console:  docker compose exec server attach-cli   (type 'info' for your invite code)

Update later by re-running this in the same folder:
  curl -fsSL https://docs.junimoserver.com/install.sh | bash

EOF
            ;;
        *)
            echo ""
            echo "Sign in to your Steam account when prompted (Steam Guard may ask for a code)."
            docker compose run --rm -it steam-auth setup < /dev/tty
            echo ""
            echo "Starting the server..."
            docker compose up -d
            echo ""
            echo "Waiting for the server to be ready. First boot downloads and loads the game,"
            echo "so this can take a few minutes (watch it with: docker compose logs -f steam-auth)."

            # Wait until the server is ready (its game log appears) or crashes, whichever comes first.
            # Attaching into a crashed/crash-looping server hangs forever, so surface the logs instead.
            # A crash shows as State.Status != running or a climbing RestartCount (not a clean exit).
            cid="$(docker compose ps -aq server 2>/dev/null | head -n1)"
            restarts0="$(docker inspect -f '{{.RestartCount}}' "$cid" 2>/dev/null || echo 0)"
            waited=0
            while [ "$waited" -lt 600 ]; do
                status="$(docker inspect -f '{{.State.Status}}' "$cid" 2>/dev/null || echo missing)"
                restarts="$(docker inspect -f '{{.RestartCount}}' "$cid" 2>/dev/null || echo 0)"
                if [ "$status" != "running" ] || [ "${restarts:-0}" -gt "${restarts0:-0}" ]; then
                    echo ""
                    echo "The server stopped right after starting. Logs of the failed run:"
                    echo ""
                    # Stop the restart loop first, then show only the failed run: Docker has usually
                    # restarted the container by now, so a plain tail would end in the next boot's
                    # first lines with the crash buried above them.
                    docker compose stop server >/dev/null 2>&1 || true
                    if [ "${restarts:-0}" -gt "${restarts0:-0}" ]; then
                        docker logs --tail 200 --until "$(docker inspect -f '{{.State.StartedAt}}' "$cid")" "$cid" 2>&1
                    else
                        docker logs --tail 200 "$cid" 2>&1
                    fi
                    echo ""
                    die "Server failed to start. Fix the issue above, then: docker compose up -d && docker compose exec server attach-cli"
                fi
                docker compose exec -T server sh -c 'test -f /tmp/server-output.log' 2>/dev/null && break
                sleep 3; waited=$((waited + 3))
            done

            echo ""
            echo "Opening the CLI..."
            docker compose exec server attach-cli < /dev/tty
            ;;
    esac
fi
)
