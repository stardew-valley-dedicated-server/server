.PHONY: docs

# Load configuration from .env (production settings, ports, etc.)
-include .env

# Function to strip single/double quotes using Make functions
define strip_quotes
$(subst ",,$(subst ',,$($(1))))
endef

# Disable printing directory traversal on task start/end
MAKEFLAGS += --no-print-directory

# Constants
IMAGE_NAME=sdvd/server
TEST_CLIENT_IMAGE_NAME=sdvd/test-client
IMAGE_VERSION ?= local
DOCKERFILE_PATH=docker/Dockerfile
TEST_CLIENT_DOCKERFILE_PATH=docker/Dockerfile.test-client

# Build configuration (Debug for local, Release for CI/production)
BUILD_CONFIGURATION ?= Debug

# Docker build progress output (plain, tty, auto, quiet)
DOCKER_PROGRESS ?= plain

# Export IMAGE_VERSION for usage in docker compose commands
export IMAGE_VERSION

# Steam build credentials for Docker image builds (game download).
# By default these come from .env (loaded above). When the test harness
# calls `make build`, it overrides these via command-line variables
# (e.g. `make build STEAM_USERNAME=x`), which take precedence over
# -include .env values in GNU Make.
export STEAM_USERNAME := $(call strip_quotes,STEAM_USERNAME)
export STEAM_PASSWORD := $(call strip_quotes,STEAM_PASSWORD)
export STEAM_REFRESH_TOKEN := $(call strip_quotes,STEAM_REFRESH_TOKEN)

# Cross-platform ISO 8601 UTC timestamp (safe for use with filenames etc.)
ifeq ($(OS),Windows_NT)
    TIMESTAMP := $(shell powershell -NoProfile -Command "(Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH-mm-ss')")Z
else
    TIMESTAMP := $(shell date -u '+%Y-%m-%dT%H-%M-%S')Z
endif

# Install development dependencies
install:
	@echo Installing development dependencies...
	@npm ci
	@dotnet tool restore
	@echo Setup complete. Git hooks are now active.

# Build all production docker images (server + steam-service)
build: build-server build-steam-service

# Build server docker image (downloads game during build for mod compilation)
build-server:
	@echo Building image `$(IMAGE_NAME):$(IMAGE_VERSION)` with BUILD_CONFIGURATION=$(BUILD_CONFIGURATION)...
	@docker buildx build \
		--platform=linux/amd64 \
		--build-arg BUILD_CONFIGURATION=$(BUILD_CONFIGURATION) \
		-t $(IMAGE_NAME):$(IMAGE_VERSION) \
		$(if $(filter-out local,$(IMAGE_VERSION)),-t $(IMAGE_NAME):latest) \
		--secret id=steam_username,env=STEAM_USERNAME \
		--secret id=steam_password,env=STEAM_PASSWORD \
		--secret id=steam_refresh_token,env=STEAM_REFRESH_TOKEN \
		-f $(DOCKERFILE_PATH) \
		--load \
		--progress=$(DOCKER_PROGRESS) \
		.
	@echo Server build complete.

# Build steam-service docker image
build-steam-service:
	@echo Building steam-service image...
	@docker compose build steam-auth
	@echo Steam-service build complete.

# Build test client docker image (for containerized E2E tests)
build-test-client:
	@echo Building test client image `$(TEST_CLIENT_IMAGE_NAME):$(IMAGE_VERSION)`...
	@docker buildx build \
		--platform=linux/amd64 \
		-t $(TEST_CLIENT_IMAGE_NAME):$(IMAGE_VERSION) \
		--secret id=steam_username,env=STEAM_USERNAME \
		--secret id=steam_password,env=STEAM_PASSWORD \
		--secret id=steam_refresh_token,env=STEAM_REFRESH_TOKEN \
		-f $(TEST_CLIENT_DOCKERFILE_PATH) \
		--load \
		--progress=$(DOCKER_PROGRESS) \
		.
	@echo Test client build complete.

# Build and run everything
up: build
	@echo Starting server `$(IMAGE_NAME):$(IMAGE_VERSION)`...
	@docker compose up -d --build
	@echo Server is now running. Use `make cli` or `make logs` to view output.

# Authenticate Steam accounts and download game files (interactive).
# Passes .env.test (if present) so steam-service sees both production
# credentials (STEAM_USERNAME/PASSWORD from .env via compose) and test
# accounts (STEAM_ACCOUNTS JSON from .env.test). Accounts with saved
# sessions are skipped; only new accounts prompt for Steam Guard.
setup: build-steam-service
	@docker compose run --rm -it \
		$(if $(wildcard .env.test),--env-from-file .env.test) \
		steam-auth setup
	@echo Setup complete. Saved sessions are stored in the steam-session volume.

restart:
	@echo Restarting server `$(IMAGE_NAME):$(IMAGE_VERSION)`...
	@docker compose restart
	@echo Server restarted. Use `make cli` or `make logs` to view output.

# Stop the server
down:
	@echo Stopping server...
	@docker compose down --remove-orphans

# Attach to interactive split-pane server CLI
cli:
	@docker compose exec server attach-cli

# View server logs (escape sequence to reset colors)
logs:
	@docker compose logs -f
	-@bun -e "process.stdout.write('\x1b[0m')"

dumplogs:
	@echo "Writing logs to logs_$(TIMESTAMP).txt"
	@docker compose logs > "logs_$(TIMESTAMP).txt"

# Start docs dev server (extracts OpenAPI spec from Docker image first)
docs:
	@echo Extracting OpenAPI spec from $(IMAGE_NAME):$(IMAGE_VERSION) image...
	@bun -e "require('fs').mkdirSync('docs/assets', { recursive: true })"
	-@bun -e "try{require('child_process').execSync('docker rm -f openapi-extract',{stdio:'ignore'})}catch(e){}"
	@docker create --name openapi-extract $(IMAGE_NAME):$(IMAGE_VERSION)
	@docker cp openapi-extract:/data/openapi.json docs/assets/openapi.json
	@docker rm openapi-extract
	@echo OpenAPI spec ready.
	@bun --cwd=./docs run dev

# Clean up everything, including all volumes
clean:
	@echo Cleaning up...
	@IMAGE_VERSION=$(IMAGE_VERSION) docker compose down -v
	-@docker rmi $(IMAGE_NAME):$(IMAGE_VERSION) $(IMAGE_NAME):latest

# Test project paths
TEST_PROJECT := ./tests/JunimoServer.Tests
RUNNER_PROJECT := ./tests/JunimoServer.TestRunner

# Test filtering. Use FILTER to run specific tests (case-insensitive substring of the
# class FullName or {Class}.{Method} display name). Separate alternatives with '|':
#   make test FILTER=PasswordProtection
#   make test FILTER="Login_WithCorrectPassword"
#   make test FILTER="CabinStrategyNoneTests|CabinPositionPersistenceTests"
#   make test (runs all tests)
FILTER ?=

# Verbose output. Use VERBOSE=1 for detailed setup steps and diagnostic logs:
#   make test VERBOSE=1
VERBOSE ?=

export COLUMNS=160

# Run tests with custom runner (CI mode - streaming output)
test:
	@dotnet run --project $(RUNNER_PROJECT) -- $(if $(VERBOSE),--verbose) $(if $(FILTER),--filter "$(FILTER)")

# Run tests with verbose output (detailed setup steps, diagnostics inline)
test-verbose:
	@dotnet run --project $(RUNNER_PROJECT) -- --verbose $(if $(FILTER),--filter "$(FILTER)")

# Run tests with structured JSONL output (for LLM/AI agents)
# Forces SDVD_TEST_TRACING=full so the AI-debug context captures every
# cross-process correlation (respBytes, X-Request-Id on reads, wait_started,
# per-test http_wait mirror). The default `make test` runs at SDVD_TEST_TRACING=none.
test-llm: export SDVD_TEST_TRACING=full
test-llm:
	@dotnet run --project $(RUNNER_PROJECT) -- --llm $(if $(FILTER),--filter "$(FILTER)")

# Build the test UI frontend (required for --web mode)
build-test-ui:
ifeq ($(OS),Windows_NT)
	@where bun >NUL 2>&1 || (echo "Error: bun is required for test-ui. Install: https://bun.sh" && exit 1)
else
	@command -v bun >/dev/null || (echo "Error: bun is required for test-ui. Install: https://bun.sh" && exit 1)
endif
	@cd tests/test-ui && bun install && bun run build

# Run tests with web UI (opens browser with live results)
test-web: build-test-ui
	@dotnet run --project $(RUNNER_PROJECT) -- --web $(if $(VERBOSE),--verbose) $(if $(FILTER),--filter "$(FILTER)")

# Run tests with web UI and generate static report
test-web-report: build-test-ui
	@dotnet run --project $(RUNNER_PROJECT) -- --web --report $(if $(VERBOSE),--verbose) $(if $(FILTER),--filter "$(FILTER)")

# --- Test Observability Targets ---
# All run via tools/test-observability.ts so they work from any shell (PowerShell,
# cmd, bash) with no jq/python/awk/column dependency. Latest-run resolution and
# all queries live in the script; these targets just pass args through.

# Show test run summary (failures, timing, classification)
test-summary:
	@bun tools/test-observability.ts summary

# Show per-test event log (Usage: make test-events TEST=ClassName.MethodName[(arg=...)])
# Filters infrastructure.jsonl by test displayName; matches both fact and theory tests.
test-events:
	@bun tools/test-observability.ts events TEST="$(TEST)"

# Show infrastructure lifecycle log (server/client creation, capacity, sessions)
test-infra-log:
	@bun tools/test-observability.ts infra-log

# Show full lifecycle log for a container (Usage: make test-container-log CONTAINER=server-0)
test-container-log:
	@bun tools/test-observability.ts container-log CONTAINER="$(CONTAINER)"

# Show run metadata (git, env, runtime info)
test-metadata:
	@bun tools/test-observability.ts metadata

# Show flakiness data across recent runs
test-flaky:
	@bun tools/test-observability.ts flaky

# Rank tests by runtime across recent runs to find suite-shortening targets.
# Reads the per-test timing already in flakiness.jsonl (same file as test-flaky).
# Default sort is total observed slot-time (sum of sampled durations) — the best
# proxy for "improving this shortens the whole suite", since tests run in parallel
# and a frequently-run or theory-multiplied test consumes more wall-clock than a
# slow-but-rare singleton.
# Usage: make test-slowest [N=15] [SORT=total|p50|p90|max] [FIELD=durationMs|testBodyMs] [LASTRUNS=20] [MINRUNS=3]
N ?= 15
SORT ?= total
FIELD ?= durationMs
LASTRUNS ?= 20
MINRUNS ?= 3
test-slowest:
	@bun tools/test-observability.ts slowest N=$(N) SORT=$(SORT) FIELD=$(FIELD) LASTRUNS=$(LASTRUNS) MINRUNS=$(MINRUNS)

# Dump the last few failure_context events from the latest run for quick triage.
test-diagnose:
	@bun tools/test-observability.ts diagnose

# Verify analyzer style rules and formatting without writing (builds the solution).
# Biome covers the JS/TS projects scoped in biome.jsonc.
lint-check:
	@dotnet format style JunimoServer.slnx --severity error --verify-no-changes
	@dotnet csharpier check .
	@npx @biomejs/biome check .

# Auto-fix analyzer style violations (braces, namespaces, usings), then re-format.
lint-fix:
	@dotnet format style JunimoServer.slnx --severity error
	@dotnet csharpier format .
	@npx @biomejs/biome check --write --unsafe .

# Show help
help:
	@echo Stardew Valley Dedicated Server
	@echo ""
	@echo Targets:
	@echo "  make install  - Install development dependencies (commitlint, git hooks)"
	@echo "  make setup    - Run first-time Steam authentication and game download"
	@echo "  make up       - Build and start server"
	@echo "  make build    - Build all production images (server + steam-service)"
	@echo "  make logs     - View server logs"
	@echo "  make dumplogs - Dump server logs to file on host"
	@echo "  make cli      - Attach to interactive server console (tmux-based)"
	@echo "  make down     - Stop the server"
	@echo "  make restart  - Restart the server (preserves volumes)"
	@echo "  make docs     - Start docs dev server (requires built image)"
	@echo "  make clean    - Remove ALL containers, volumes and images"
	@echo ""
	@echo Testing:
	@echo "  make test         - Run E2E tests (CI mode, streaming output)"
	@echo "  make test-verbose - Run tests with detailed setup/diagnostic output"
	@echo "  make test-llm     - Run tests with structured JSONL output (for AI agents)"
	@echo "  make test-web     - Run tests with web UI (opens browser with live results)"
	@echo "  make test-web-report - Run tests with web UI + static report generation"
	@echo "  FILTER=X          - Filter tests, e.g. FILTER=PasswordProtection"
	@echo "  VERBOSE=1         - Show detailed setup steps, e.g. make test VERBOSE=1"
	@echo ""
	@echo "  make build-test-client - Build test client container image (for E2E tests)"
	@echo ""
	@echo Formatting:
	@echo "  make lint-check - Verify C# + JS/TS style rules + formatting (slow, builds)"
	@echo "  make lint-fix   - Auto-fix C# + JS/TS style violations + re-format (slow, builds)"
	@echo ""
	@echo Test Observability:
	@echo "  make test-summary  - Show test run summary (failures, timing)"
	@echo "  make test-events   - Show per-test events (TEST=Class.Method)"
	@echo "  make test-infra-log - Show infrastructure lifecycle log"
	@echo "  make test-metadata - Show run metadata (git, env, runtime)"
	@echo "  make test-flaky    - Show flakiness data across recent runs"
	@echo "  make test-slowest  - Rank tests by runtime (SORT=total|p50|p90|max) to find suite-shortening targets"
	@echo "  make test-diagnose - Show last failure_context events for triage"
	@echo "  make test-container-log - Show full container log (Usage: CONTAINER=server-0)"
	@echo ""
	@echo Note: Use GitHub Actions for building and pushing release images

.DEFAULT_GOAL := help
