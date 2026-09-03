FROM oven/bun:1.4.0-alpine

WORKDIR /app

# su-exec drops privileges to the configured USER_ID:GROUP_ID in the entrypoint (see
# entrypoint.sh) so the dashboard-state volume is owned by the operator's host user instead of
# root, consistent with the server and steam-auth containers.
RUN apk add --no-cache su-exec

# Copy package files and lockfile
COPY package.json bun.lock ./

# Install dependencies
RUN bun install --frozen-lockfile --production

# Copy source
COPY src ./src
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# The entrypoint starts as root only long enough to chown /data, then drops to USER_ID:GROUP_ID.
ENTRYPOINT ["/entrypoint.sh"]
