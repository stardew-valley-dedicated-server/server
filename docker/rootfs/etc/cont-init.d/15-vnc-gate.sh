#!/bin/sh

# Runs as root after the base image's 10-nginx.sh and 10-vnc-password.sh. Keeps the VNC web UI
# (nginx, port 5800) and the raw VNC port (Xvnc, port 5900) unreachable from outside the container
# unless a VNC password is configured or ALLOW_INSECURE_SETUP=true opts out.
#
# The base image's WEB_LISTENING_PORT / VNC_LISTENING_PORT knobs can't express this: they are baked
# into the image as ENV, and its init applies /etc/cont-env.d files only to variables that are not
# already set, so nothing inside the image can make them depend on VNC_PASSWORD. Instead the listen
# directives nginx already generated are re-bound to loopback (nginx stays up, so its readiness
# check and the services that depend on it are unaffected), and a marker tells services.d/xvnc/run
# to drop Xvnc's TCP port. If a directive survives in a form this script doesn't know, the container
# refuses to start rather than boot with the web UI open.

set -e

VNC_DISABLED_MARKER=/var/run/sdvd-vnc-disabled
rm -f "${VNC_DISABLED_MARKER}"

# Same password resolution as the base image's services.d/xvnc/params.
has_password=false
if [ -f /config/.vncpass ] && [ -n "$(cat /config/.vncpass)" ]; then
    has_password=true
elif [ -f /tmp/.vncpass ] && [ -n "$(cat /tmp/.vncpass)" ]; then
    has_password=true
fi

if [ "${has_password}" = true ]; then
    exit 0
fi
if [ "${ALLOW_INSECURE_SETUP:-}" = "true" ]; then
    echo "VNC has no password but stays reachable because ALLOW_INSECURE_SETUP=true."
    exit 0
fi

# 10-nginx.sh always writes listen.conf; if it is missing, the base image changed where nginx's
# listen directives live and this gate no longer covers them.
if [ ! -f /var/tmp/nginx/listen.conf ]; then
    echo "ERROR: /var/tmp/nginx/listen.conf not found; refusing to start with the VNC web UI possibly reachable without a password." >&2
    exit 1
fi

# Address forms nginx accepts: `IPv4:port`, `[IPv6]:port`, bare `port`. All become loopback.
for conf in /var/tmp/nginx/listen.conf /var/tmp/nginx/stream_listen.conf; do
    [ -f "${conf}" ] || continue
    sed -i \
        -e 's/^listen 0\.0\.0\.0:/listen 127.0.0.1:/' \
        -e 's/^listen \[::\]:/listen [::1]:/' \
        -e 's/^listen \([0-9][0-9]*\)\([ ;]\)/listen 127.0.0.1:\1\2/' \
        "${conf}"
    if grep '^listen ' "${conf}" | grep -v -e '^listen 127\.0\.0\.1:' -e '^listen \[::1\]:' -e '^listen unix:'; then
        echo "ERROR: ${conf} still has a listen directive that is not loopback (above); refusing to start with the VNC web UI reachable without a password." >&2
        exit 1
    fi
done
touch "${VNC_DISABLED_MARKER}"
echo "VNC_PASSWORD is not set: the VNC web UI and VNC port are disabled. Set VNC_PASSWORD in .env to enable them."
