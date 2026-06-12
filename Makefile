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

# Test filtering. Use FILTER to run specific tests:
#   make test FILTER=PasswordProtection
#   make test FILTER="Login_WithCorrectPassword"
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
# Resolve latest run: prefer latest.txt, fall back to most recent run dir with summary.json
LATEST_RUN = $(shell cat TestResults/latest.txt 2>/dev/null)
ifeq ($(LATEST_RUN),)
  LATEST_RUN = $(shell ls -1d TestResults/runs/*/ 2>/dev/null | sort -r | while read d; do test -f "$$d/summary.json" && echo "$$d" && break; done)
endif

# Show test run summary (failures, timing, classification)
test-summary:
	@if [ -z "$(LATEST_RUN)" ]; then echo "No test runs found. Run tests first."; exit 1; fi
	@if [ ! -f "$(LATEST_RUN)/summary.json" ]; then echo "No summary.json in $(LATEST_RUN) (run may have been aborted)."; exit 1; fi
	@cat "$(LATEST_RUN)/summary.json" | python3 -m json.tool 2>/dev/null || cat "$(LATEST_RUN)/summary.json"

# Show per-test event log (Usage: make test-events TEST=ClassName.MethodName[(arg=...)])
# Filters infrastructure.jsonl by test displayName; matches both fact and theory tests.
test-events:
	@test -n "$(TEST)" || { echo "Usage: make test-events TEST=ClassName.MethodName[(arg=value)]"; exit 1; }
	@if [ -z "$(LATEST_RUN)" ]; then echo "No test runs found."; exit 1; fi
	@if [ ! -f "$(LATEST_RUN)/diagnostics/infrastructure.jsonl" ]; then echo "No infrastructure.jsonl in $(LATEST_RUN)."; exit 1; fi
	@jq -c 'select(.test.displayName // "" | contains("$(TEST)"))' \
	    "$(LATEST_RUN)/diagnostics/infrastructure.jsonl"

# Show infrastructure lifecycle log (server/client creation, capacity, sessions)
test-infra-log:
	@if [ -z "$(LATEST_RUN)" ]; then echo "No test runs found."; exit 1; fi
	@cat "$(LATEST_RUN)/diagnostics/infrastructure.jsonl" 2>/dev/null || echo "No infrastructure log in $(LATEST_RUN)."

# Show full lifecycle log for a container (Usage: make test-container-log CONTAINER=server-0)
test-container-log:
	@test -n "$(CONTAINER)" || { echo "Usage: make test-container-log CONTAINER=server-0|client-0|steam-auth-shared|steam-auth-per-N"; exit 1; }
	@if [ -z "$(LATEST_RUN)" ]; then echo "No test runs found."; exit 1; fi
	@if [ -f "$(LATEST_RUN)/containers/$(CONTAINER)/container.log" ]; then \
	  cat "$(LATEST_RUN)/containers/$(CONTAINER)/container.log"; \
	elif [ -f "$(LATEST_RUN)/containers/$(CONTAINER)/container.log.gz" ]; then \
	  gunzip -c "$(LATEST_RUN)/containers/$(CONTAINER)/container.log.gz"; \
	else \
	  echo "No container.log for $(CONTAINER) in $(LATEST_RUN)."; \
	  ls "$(LATEST_RUN)/containers/" 2>/dev/null | head -20; \
	  exit 1; \
	fi

# Show run metadata (git, env, runtime info)
test-metadata:
	@if [ -z "$(LATEST_RUN)" ]; then echo "No test runs found."; exit 1; fi
	@cat "$(LATEST_RUN)/run-metadata.json" 2>/dev/null | python3 -m json.tool 2>/dev/null || cat "$(LATEST_RUN)/run-metadata.json" 2>/dev/null || echo "No metadata in $(LATEST_RUN)."

# Show flakiness data across recent runs
test-flaky:
	@test -f TestResults/flakiness.jsonl && tail -2000 TestResults/flakiness.jsonl || echo "No flakiness data. Run tests multiple times first."

# Dump the last failure_context event from the latest run for quick triage.
# Usage: make test-diagnose [TEST=ClassName.MethodName]
test-diagnose:
	@if [ -z "$(LATEST_RUN)" ]; then echo "No test runs found."; exit 1; fi
	@if [ ! -f "$(LATEST_RUN)/diagnostics/infrastructure.jsonl" ]; then echo "No infrastructure log in $(LATEST_RUN)."; exit 1; fi
	@grep -F '"event":"failure_context"' "$(LATEST_RUN)/diagnostics/infrastructure.jsonl" | tail -5 | python3 -m json.tool --no-ensure-ascii 2>/dev/null || grep -F '"event":"failure_context"' "$(LATEST_RUN)/diagnostics/infrastructure.jsonl" | tail -5

# Auto-format all C# files using CSharpier (rewrites in place)
format:
	@dotnet csharpier format .

# Verify all C# files are formatted (CI). Exits non-zero on diff.
format-check:
	@dotnet csharpier check .

# Auto-fix analyzer style violations (braces, namespaces, usings), then re-format.
# Slower than `make format` — dotnet format builds the solution to run the analyzers.
lint:
	@dotnet format style JunimoServer.slnx --severity error
	@dotnet csharpier format .

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
	@echo "  make format       - Format all C# files (CSharpier)"
	@echo "  make format-check - Check formatting without writing (CI)"
	@echo "  make lint         - Auto-fix analyzer style violations + format (slow, builds)"
	@echo ""
	@echo Test Observability:
	@echo "  make test-summary  - Show test run summary (failures, timing)"
	@echo "  make test-events   - Show per-test events (TEST=Class.Method)"
	@echo "  make test-infra-log - Show infrastructure lifecycle log"
	@echo "  make test-metadata - Show run metadata (git, env, runtime)"
	@echo "  make test-flaky    - Show flakiness data across recent runs"
	@echo "  make test-diagnose - Show last failure_context events for triage"
	@echo "  make test-container-log - Show full container log (Usage: CONTAINER=server-0)"
	@echo ""
	@echo Note: Use GitHub Actions for building and pushing release images

.DEFAULT_GOAL := help
