# Load configuration
-include .env

# Export make variables as actual environment variables,
# so that we can pass them as docker secrets during build
# export STEAM_USER := $(shell echo $(STEAM_USER) | sed -e 's/^"//' -e 's/"$$//' -e "s/^'//" -e "s/'$$//")
# export STEAM_PASS := $(shell echo $(STEAM_PASS) | sed -e 's/^"//' -e 's/"$$//' -e "s/^'//" -e "s/'$$//")

# Function to strip single/double quotes using Make functions
define strip_quotes
$(subst ",,$(subst ',,$($(1))))
endef

# Export cleaned variables individually (portable)
export STEAM_USER := $(call strip_quotes,STEAM_USER)
export STEAM_PASS := $(call strip_quotes,STEAM_PASS)

# Disable printing directory traversal on task start/end
MAKEFLAGS += --no-print-directory

# Constants
IMAGE_NAME="sdvd/server"
IMAGE_VERSION ?= local
DOCKERFILE=docker/Dockerfile

# Build and run everything
run: build
	@echo Starting server...
	@docker compose -f docker-compose.yml up -d
	@echo.
	@echo Server is now running. Use 'make logs' to view output.

restart:
	@echo Starting server...
	@docker compose -f docker-compose.yml restart
	@echo.
	@echo Server restarted. Use 'make logs' to view output.

# Build docker image (downloads game during build for mod compilation)
build:
	@echo Building image $(IMAGE_NAME):$(IMAGE_VERSION)...
	@docker buildx build \
		--platform=linux/amd64 \
		-t $(IMAGE_NAME):$(IMAGE_VERSION) \
		-t $(IMAGE_NAME):latest \
		--secret id=steam_user,env=STEAM_USER \
		--secret id=steam_pass,env=STEAM_PASS \
		-f $(DOCKERFILE) \
		--load \
		--progress=auto \
		.
	@echo Build complete.

# View server logs
logs:
	@docker compose -f docker-compose.yml logs -f
	@printf '\033[0m'

# Attach to interactive server CLI (with split-pane view for logs and commands)
cli:
	@echo Attaching to server console...
	@docker compose exec server attach-cli

# Attach to Ink-based CLI (experimental terminal-style interface)
cli-ink:
	@echo Starting Ink CLI...
	@docker compose exec server bash -c "cd /data/Tools/cli && npm start"

# Stop the server
stop:
	@echo Stopping server...
	@docker compose -f docker-compose.yml down --remove-orphans

# Clean up everything
clean:
	@echo Cleaning up...
	@docker compose -f docker-compose.yml down -v
	@docker rmi $(IMAGE_NAME):$(IMAGE_VERSION) $(IMAGE_NAME):latest 2>/dev/null || true

# Show help
help:
	@echo Stardew Valley Dedicated Server
	@echo.
	@echo Targets:
	@echo   make run      - Build and start server
	@echo   make build    - Build docker image (tag: local)
	@echo   make logs     - View server logs
	@echo   make cli      - Attach to interactive server console (tmux-based)
	@echo   make ink-cli  - Attach to Ink-based CLI (experimental)
	@echo   make stop     - Stop the server
	@echo   make clean    - Remove containers, volumes and images
	@echo.
	@echo Note: Use GitHub Actions for building and pushing release images

.DEFAULT_GOAL := run
