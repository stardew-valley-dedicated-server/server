# CI_GAME_PATH=/home/runner/actions-runner/_work/junimohost-stardew-server/junimohost-stardew-server/Stardew Valley
CI_GAME_PATH=D:/Games/Steam/steamapps/common/Stardew Valley

# Load configuration
-include .env

# Define constants
SRC_PATH=./mod/JunimoServer
BUILD_PATH=./.output/build
DEST_PATH=./.output/mods/JunimoServer

# TODO: Random project meta data, should it be defined more centrally? (.env vs .props files problem)
IMAGE_REGISTRY=sdvd
IMAGE_NAME=server

# Build and start docker containers
run: build-mod build-image
	docker compose up --force-recreate -d

# Build mod and docker image
build-image:
	docker build --platform=amd64 -t $(IMAGE_REGISTRY)/$(IMAGE_NAME):$(IMAGE_VERSION) -t $(IMAGE_REGISTRY)/$(IMAGE_NAME):latest -f docker/Dockerfile .

# Build mod
build-mod: $(shell find $(SRC_PATH)/**/*.cs -type f) $(SRC_PATH)/JunimoServer.csproj $(SRC_PATH)/manifest.json
ifeq ($(CI), true)
	dotnet build $(SRC_PATH)/JunimoServer.csproj -o $(BUILD_PATH) --configuration Release '/p:EnableModZip=false;EnableModDeploy=false;GamePath=$(CI_GAME_PATH)'
else
	dotnet build $(SRC_PATH)/JunimoServer.csproj -o $(BUILD_PATH) --configuration Debug
endif
	rm -rf $(DEST_PATH)
	mkdir -p $(DEST_PATH)
	cp $(BUILD_PATH)/JunimoServer.dll \
	$(BUILD_PATH)/Google.Protobuf.dll \
	$(BUILD_PATH)/Grpc.Core.Api.dll \
	$(BUILD_PATH)/Grpc.Net.Client.dll \
	$(BUILD_PATH)/Grpc.Net.Common.dll \
	$(BUILD_PATH)/Microsoft.Extensions.DependencyInjection.dll \
	$(BUILD_PATH)/Microsoft.Extensions.Logging.Abstractions.dll \
	$(BUILD_PATH)/Microsoft.IO.RecyclableMemoryStream.dll \
	$(BUILD_PATH)/System.Reactive.dll \
	$(BUILD_PATH)/Websocket.Client.dll \
	$(SRC_PATH)/manifest.json \
	$(DEST_PATH)
	rm -rf $(BUILD_PATH)

# Tag and push docker image
push: build-mod build-image
	docker push $(IMAGE_REGISTRY)/$(IMAGE_NAME):$(IMAGE_VERSION)
	docker push $(IMAGE_REGISTRY)/$(IMAGE_NAME):latest
