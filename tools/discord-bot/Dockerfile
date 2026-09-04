FROM oven/bun:1.4.0-alpine

WORKDIR /app

# Copy package files and lockfile
COPY package.json bun.lock ./

# Install dependencies
RUN bun install --frozen-lockfile --production

# Copy source
COPY src ./src

# Dashboard ownership state lives in /data (named volume). A fresh volume
# inherits the mountpoint's ownership from the image, so chown here is what
# lets the non-root user write to it.
RUN mkdir -p /data && chown bun:bun /data

# Run as non-root user
USER bun

CMD ["bun", "run", "src/index.ts"]
