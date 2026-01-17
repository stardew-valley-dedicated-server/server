# Load configuration
-include .env

# Function to strip single/double quotes using Make functions
define strip_quotes
$(subst ",,$(subst ',,$($(1))))
endef

# Disable printing directory traversal on task start/end
MAKEFLAGS += --no-print-directory

# Constants
IMAGE_NAME=sdvd/server
IMAGE_VERSION ?= local
DOCKERFILE_PATH=docker/Dockerfile

# Build configuration (Debug for local, Release for CI/production)
BUILD_CONFIGURATION ?= Debug

# Export IMAGE_VERSION for usage in docker compose commands
export IMAGE_VERSION

# Export make variables as actual environment variables,
# so that we can pass them as docker secrets during build
export STEAM_USERNAME := $(call strip_quotes,STEAM_USERNAME)
export STEAM_PASSWORD := $(call strip_quotes,STEAM_PASSWORD)
export STEAM_REFRESH_TOKEN := $(call strip_quotes,STEAM_REFRESH_TOKEN)

# Install development dependencies
install:
	@echo Installing development dependencies...
	@npm ci
	@echo Setup complete. Git hooks are now active.

# Build docker image (downloads game during build for mod compilation)
build:
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
		--progress=plain \
		.
	@echo Build complete.

# Build and run everything
up: build
	@echo Starting server `$(IMAGE_NAME):$(IMAGE_VERSION)`...
	@docker compose up -d --build
	@echo Server is now running. Use `make cli` or `make logs` to view output.

setup:
	@docker compose run --rm -it steam-auth setup
	@echo Server is now set up. Use `make cli` or `make logs` to view output.

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
	@echo Attaching to server console...
	@echo Waiting for server to be ready...
	@docker compose exec server sh -c 'while [ ! -f /tmp/server-output.log ]; do sleep 1; done'
	@docker compose exec server attach-cli

# View server logs (escape sequence to reset colors)
logs:
	@docker compose logs -f
	@printf '\033[0m'

# Clean up everything
clean:
	@echo Cleaning up...
	@IMAGE_VERSION=$(IMAGE_VERSION) docker compose down -v
	@docker rmi $(IMAGE_NAME):$(IMAGE_VERSION) $(IMAGE_NAME):latest 2>/dev/null || true

# Show help
help:
	@echo Stardew Valley Dedicated Server
	@echo.
	@echo Targets:
	@echo   make install  - Install development dependencies (commitlint, git hooks)
	@echo   make setup    - Run first-time Steam authentication and game download
	@echo   make up       - Build and start server
	@echo   make build    - Build docker image (tag: local)
	@echo   make logs     - View server logs
	@echo   make cli      - Attach to interactive server console (tmux-based)
	@echo   make down     - Stop the server
	@echo   make clean    - Remove containers, volumes and images
	@echo.
	@echo Note: Use GitHub Actions for building and pushing release images

.DEFAULT_GOAL := run
