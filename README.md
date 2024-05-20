# Stardew Dedicated Server

This repository contains everything needed to build a docker container running headless Stardew Valley.

## Usage

Game config is pulled from the backend to the daemon before the game starts. The game mod retrieves its config from the daemon. There are default options provided in the case the backend service is not available. Use docker-compose to run the container.

## Compilation


### Requirements
All
- make
- docker

Daemon
- go 1.21

Mod
- .NET 6 SDK
- Installed SMAPI & Stardew

### Command
run ```make build```

## Context
The headless server contains three main components: 
- Docker
- Daemon
- JunimoServer SMAPI Mod

### Docker
Docker files are used to build a container that headlessly runs Stardew.

### Daemon
The daemon is used to manage the system the game is running on. It's responsible for creating backups, getting configs from the backend, and eventually managing the game process completely.


### JunimoServer
JunimoServer is a SMAPI mod used to automate the host and add in features condusive to server play.


### TODO
* [Anticheat](https://github.com/funny-snek/anticheat-and-servercode)
* [Display Cabin Owner](https://www.nexusmods.com/stardewvalley/mods/3036)
* [UnlimitedPlayers](https://github.com/Armitxes/StardewValley_UnlimitedPlayers)
  
### Credits
This project would not have been possible without the amazing work of many people before :heart:

* [Junimohost](https://github.com/JunimoHost/junimohost-stardew-server) from ([mrthinger](https://github.com/mrthinger) and [Regnivon](https://github.com/regnivon)) as the base of this dedicated server
* [AlwaysOn Mod](https://github.com/funny-snek/Always-On-Server-for-Multiplayer) (from [funny-snek](https://github.com/funny-snek)) for writing the original keep-alive logic for the server player
* [NetworkOptimizer](https://github.com/Ilyaki/NetworkOptimizer) (from [Ilyaki](https://github.com/Ilyaki))