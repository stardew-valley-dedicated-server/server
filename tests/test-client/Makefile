# JunimoTestClient Makefile
# Requires: dotnet, and Stardew Valley with SMAPI installed

# Paths (adjust if needed)
GAME_DIR ?= D:/Games/Steam/steamapps/common/Stardew Valley
SMAPI_EXE = $(GAME_DIR)/StardewModdingAPI.exe
MOD_DIR = $(GAME_DIR)/Mods/JunimoTestClient

# Test API settings
export JUNIMO_TEST_PORT ?= 5123

.PHONY: build deploy clean run run-bg stop status test

# Build the mod
build:
	dotnet build

# Build and deploy (deploy is automatic via EnableModDeploy)
deploy: build
	@echo "Mod deployed to $(MOD_DIR)"

# Clean build artifacts
clean:
	dotnet clean
	rm -rf bin obj

# Run the game with SMAPI (foreground)
run: deploy
	@echo "Starting Stardew Valley with SMAPI..."
	@echo "Test API will be available at http://localhost:$(JUNIMO_TEST_PORT)/"
	cd "$(GAME_DIR)" && "./StardewModdingAPI.exe" --client

# Run the game with SMAPI (background)
run-bg: deploy
	@echo "Starting Stardew Valley with SMAPI in background..."
	@echo "Test API will be available at http://localhost:$(JUNIMO_TEST_PORT)/"
	cd "$(GAME_DIR)" && "./StardewModdingAPI.exe" --client &
	@echo "Game started. Use 'make stop' to terminate."

# Stop the game (Windows)
stop:
	@echo "Stopping Stardew Valley..."
	-taskkill //F //IM "StardewModdingAPI.exe" 2>/dev/null || true
	-taskkill //F //IM "Stardew Valley.exe" 2>/dev/null || true

# Check if game is running and API is responding
status:
	@echo "Checking game status..."
	@curl -s http://localhost:$(JUNIMO_TEST_PORT)/ping && echo " - API responding" || echo "API not responding"
	@curl -s http://localhost:$(JUNIMO_TEST_PORT)/health || true

# Quick API test
test:
	@echo "=== Ping ==="
	@curl -s http://localhost:$(JUNIMO_TEST_PORT)/ping | jq . 2>/dev/null || curl -s http://localhost:$(JUNIMO_TEST_PORT)/ping
	@echo ""
	@echo "=== Status ==="
	@curl -s http://localhost:$(JUNIMO_TEST_PORT)/status | jq . 2>/dev/null || curl -s http://localhost:$(JUNIMO_TEST_PORT)/status
	@echo ""
	@echo "=== Health ==="
	@curl -s http://localhost:$(JUNIMO_TEST_PORT)/health | jq . 2>/dev/null || curl -s http://localhost:$(JUNIMO_TEST_PORT)/health
	@echo ""
	@echo "=== Stats ==="
	@curl -s http://localhost:$(JUNIMO_TEST_PORT)/stats | jq . 2>/dev/null || curl -s http://localhost:$(JUNIMO_TEST_PORT)/stats

# Show available endpoints
endpoints:
	@echo "Available endpoints:"
	@echo "  GET  /ping              - Health check"
	@echo "  GET  /status            - Overall game status"
	@echo "  GET  /menu              - Current menu info"
	@echo "  GET  /menu/buttons      - Clickable buttons"
	@echo "  GET  /menu/slots        - Menu slots"
	@echo "  GET  /connection        - Connection status"
	@echo "  GET  /farmer            - Farmer info"
	@echo "  POST /navigate          - Navigate to menu"
	@echo "  POST /coop/tab          - Switch coop tab"
	@echo "  POST /coop/join-invite  - Join via invite code"
	@echo "  POST /coop/join-lan     - Join via LAN"
	@echo "  GET  /farmhands         - Available farmhand slots"
	@echo "  POST /farmhands/select  - Select farmhand"
	@echo "  POST /chat/send         - Send chat message"
	@echo "  POST /chat/info         - Send local info"
	@echo "  GET  /chat/history      - Chat history"
	@echo "  GET  /wait/menu         - Wait for menu type"
	@echo "  GET  /wait/connected    - Wait until connected"
	@echo "  GET  /wait/world-ready  - Wait until world ready"
	@echo "  GET  /wait/farmhands    - Wait for farmhand menu"
	@echo "  GET  /wait/title        - Wait for title screen"
	@echo "  GET  /health            - Health watchdog status"
	@echo "  GET  /stats             - Performance stats"
	@echo "  POST /stats/reset       - Reset max tick tracking"
	@echo "  GET  /errors            - Get captured errors"
	@echo "  DELETE /errors          - Clear errors"
	@echo "  POST /screenshot        - Capture screenshot (base64)"
	@echo "  POST /screenshot/file   - Capture screenshot to file"
	@echo "  POST /exit              - Exit to title"

help:
	@echo "JunimoTestClient Makefile"
	@echo ""
	@echo "Usage:"
	@echo "  make build     - Build the mod"
	@echo "  make deploy    - Build and deploy to game"
	@echo "  make clean     - Clean build artifacts"
	@echo "  make run       - Start game with SMAPI (foreground)"
	@echo "  make run-bg    - Start game with SMAPI (background)"
	@echo "  make stop      - Stop the game"
	@echo "  make status    - Check if API is responding"
	@echo "  make test      - Quick API test"
	@echo "  make endpoints - List available API endpoints"
	@echo ""
	@echo "Environment variables:"
	@echo "  GAME_DIR           - Stardew Valley install path"
	@echo "  JUNIMO_TEST_PORT   - API port (default: 5123)"
