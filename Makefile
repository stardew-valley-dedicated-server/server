
# Set .env defaults
IMAGE_VERSION=v0.20.7
CI_GAMEPATH=/home/runner/actions-runner/_work/junimohost-stardew-server/junimohost-stardew-server/Stardew Valley

# Load .env file
-include .env

build: 
	docker/mods/JunimoServer $(shell find docker -type f)
	docker build --platform=amd64 -t stardew-dedicated-server:$(IMAGE_VERSION) -f docker/Dockerfile .

dev: 
	make docker/mods/JunimoServer -B
	docker compose up --build

clean:
	rm -rf ./docker/mods/JunimoServer
	rm -rf ./mod/build
	rm -rf ./mod/JunimoServer/bin
	rm -rf ./mod/JunimoServer/obj

docker/mods/JunimoServer: $(shell find mod/JunimoServer/**/*.cs -type f) ./mod/JunimoServer/JunimoServer.csproj
ifeq ($(CI), true)
	cd mod && dotnet build -o ./build --configuration Release "/p:EnableModZip=false;EnableModDeploy=false;GamePath=$(CI_GAMEPATH)"
else
	cd mod && dotnet build -o ./build --configuration Debug
endif
	rm -rf ./docker/mods/JunimoServer
	mkdir -p ./docker/mods/JunimoServer
	cp ./mod/build/JunimoServer.dll \
	./mod/build/Microsoft.Extensions.Logging.Abstractions.dll \
	./mod/build/Microsoft.IO.RecyclableMemoryStream.dll \
	./mod/build/Google.Protobuf.dll \
	./mod/build/Grpc.Core.Api.dll \
	./mod/build/Grpc.Net.Client.dll \
	./mod/build/Grpc.Net.Common.dll \
	./mod/build/System.Reactive.dll \
	./mod/build/Websocket.Client.dll \
	./mod/JunimoServer/manifest.json \
	./docker/mods/JunimoServer

game-daemon: $(shell find daemon -type f)
	GOOS=linux GOARCH=amd64 go build -o game-daemon ./cmd/daemon/daemon.go

push: build
	docker push gcr.io/junimo-host/stardew-dedicated-server:$(IMAGE_VERSION)

daemon_windows:
	cd daemon && set GOOS=linux && go build -o game-daemon ./cmd/daemon/daemon.go