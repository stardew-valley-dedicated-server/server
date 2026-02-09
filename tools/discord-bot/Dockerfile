FROM oven/bun:1.3.9-alpine

WORKDIR /app

# Copy package files and lockfile
COPY package.json bun.lock ./

# Install dependencies
RUN bun install --frozen-lockfile --production

# Copy source
COPY src ./src

# Run as non-root user
USER bun

CMD ["bun", "run", "src/index.ts"]
