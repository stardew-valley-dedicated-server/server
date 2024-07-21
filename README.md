# Stardew Valley Dedicated Server

![Static Badge](https://img.shields.io/badge/Stardew%20Valley-1.6.8-34D058) 
![Static Badge](https://img.shields.io/badge/SMAPI-4.0.8-34D058)

Join us in [Discord](https://discord.gg/w23GVXdSF7)

## Table of Contents

<!-- REGENRATE TOC: npx markdown-toc -i README.md -->

<!-- toc -->

- [What is this project?](#what-is-this-project)
- [Features](#features)
- [Usage](#usage)
  * [Quick start](#quick-start)
  * [Web VNC](#web-vnc)
  * [Console](#console)
  * [Saves](#saves)
  * [Upgrading](#upgrading)
  * [Environment Variables](#environment-variables)
  * [Mods](#mods)
- [Planned Features](#planned-features)
- [Contributing](#contributing)
  * [Requirements](#requirements)
  * [Local development](#local-development)
- [Release Management](#release-management)
  * [Push release](#push-release)
- [Credits & Contributors](#credits--contributors)
- [FAQ](#faq)
  * [Why is the Steam Client not used?](#why-is-the-steam-client-not-used)
  * [Are there other Stardew Valley servers?](#are-there-other-stardew-valley-servers)

<!-- tocstop -->

## What is this project?
A Linux Docker image to run a headless dedicated multiplayer server for [Stardew Valley](https://www.stardewvalley.net/). 

This project is not affiliated with or endorsed by ConcernedApe LLC. All product names, logos, and brands are property of their respective owners.


## Features
* Dedicated hosting: Linux Docker image to run the game autonomously
* Administration: Web VNC for easy and centralized administration
* CropSaver: Your crops are safe from decay while you are not connected
* And much more!


## Usage
### Quick start

Copy the `docker-compose.yml` and `.env.example` file, rename it to `.env` and set these config values:
```sh
# Steam
STEAM_USER=""
STEAM_PASS=""

# VNC
VNC_PASSWORD=""
```

Run the server:
```sh
docker compose up -d
```

To stop the server:
```sh
docker compose down
```

### Web VNC
To connect to the web VNC, open your browser and navigate to `http://IP:VNC_PORT`. 

The settings panel on the left side can be used to set connection quality, compression level and scaling mode. The scaling mode `Remote Resizing` should not be used, it tends to break the server and requires a restart to be fixed.

Copy and paste only works via the settings panel clipboard area.

### Console
To issue commands to the SMAPI console, connect to the web VNC and use the chat. 

CLI support is not implemented yet.

### Saves
Save files are located in the `data` volume in the `Saves` directory.

Backup functionality is not built in yet, so make sure manually backup the `data` volume in regular intervals.

### Upgrading
To download the latest game or SMAPI files, stop the server, delete the `game` docker volume and start the server again.

### Environment Variables
These are the environment variables which can be specified at container run time.

|Variable Name|Description|Default|Available in|
|---|---|---|---|
|GAME_PORT|Game Port|24643|1.0.0|
|DISABLE_RENDERING|Disables rendering in VNC|true|1.0.0|
|STEAM_USER|Required to download the game on initial startup or for updates, but not to run the server|-|1.0.0|
|STEAM_PASS|See STEAM_USER|-|1.0.0|
|VNC_PORT|Web VNC port|8090|1.0.0|
|VNC_PASSWORD|Web VNC password|-|1.0.0|
|IMAGE_VERSION|Docker image version|-|1.0.0|
|SMAPI_VERSION|SMAPI version to load on startup|-|1.0.0|
|CI|Currently toggles between debug and release build, subject to change|false|1.0.0|
|CI_GAME_PATH|Must be set when `CI=true`|D:\Games\Steam\steamapps\common\Stardew Valley|1.0.0|

### Mods
To load additional mods like GenericModConfigMenu and TimeSpeed on the server, put these mods a directory and create this additional bind mount in your `docker-compose.yml`.

```yml
services:
    stardew:
        volumes:
            - ./mods:/data/Stardew/Mods/extra
```



## Planned Features
This is a list of planned or wanted features with rough priorization, so if you want to help out this is a good starting point:
* **[HIGH] Hide player IPs**: Players IPs should be hidden from server join message, /list command and others (?)
* **[HIGH] Server authentication**: Implement server password, whitelist, banlist
* **[HIGH] Admin indicator**: Admins need a visible chat indicator to prevent name impersonation
* **[HIGH] Unit Tests**: Would be great to have least parts of the code base covered by automated (unit)-tests
* **[HIGH] Player list command**: Implement new /online command, showing player names with ping etc.
* **[MID] Backup integration**: Backups should be integrated into the server in a provider agnostic way
* **[MID] noVNC encryption**: Currently using insecure connections, but should use secure connections
* **[MID] noVNC password**: Prevent startup without VNC password, to prevent exposing by accident
* **[LOW] Runtime update checks**: Game and mod should check for updates while running
* **[LOW] Server tweaks**: Hide server from map, /list, chat, item pickup, visible in VNC etc.
* **[LOW] SMAPI CLI**: Allow to run commands in CLI, not only VNC
* **[LOW] Mod compiling**: Mod should be compiled inside a docker image instead of on host
* **[LOW] Display cabin owner**: Display cabin owner for everyone to see (for reference [see mod](https://www.nexusmods.com/stardewvalley/mods/3036))
* **[LOW] Anticheat**: Haven't looked into it, but we want anticheat (for reference [see mod](https://github.com/funny-snek/anticheat-and-servercode)?)
* **[LOW] Discord integration**: WebHooks, Bot, something else?
* **[LOW] Server Plugins**: Plugin/mod system to extend the server (which we then also use internally)
* **[LOW] Custom launcher**: Custom app as a server browser with listing, favorites, per-server mod sync and more
* **[LOW] Performance Benchmarks**: Run benchmarks to get performance requirements



## Contributing
> [!WARNING]
> This section needs work.

### Commits
We use conventional commits, refer to [Conventional Commit Cheat Sheet](https://gist.github.com/qoomon/5dfcdf8eec66a051ecd85625518cfd13).

Tools and CI pipelines enforce conventional commits are not yet in place.
  
### Development
#### Requirements
* Docker `>=20`
* git `>=2`
* make `>=4` ([Download](https://gnuwin32.sourceforge.net/packages/make.htm))
* .NET SDK `6` ([Download](https://dotnet.microsoft.com/en-us/download/dotnet/6.0))
* Local Stardew Valley installation (use Steam)

#### Setup
Clone the repository:
```sh
mkdir sdvd-server
cd sdvd-server
git clone --recurse-submodules git@github.com:stardew-valley-dedicated-server/server.git .
```

Update `JunimoServer.csproj` to enable IDE autocomplete and build support:
```xml
<GamePath>C:\path\to\Stardew Valley</GamePath>
```

#### Usage
Build and start the server:
```sh
make dev
```

See the logs:
```sh
docker compose logs -f
```

Stop the server:
```sh
docker compose down
```

#### Decompile Stardew Valley
To decompile Stardew Valley run this script:
```sh
bash ./tools/decompile.sh
```



### Release Management
> [!WARNING]
> This section needs work.

#### Push release
To push a release to the container repository, set the `IMAGE_VERSION` in your `.env` file and run these commands:

```sh
make push
```



## Architecture
> [!WARNING]
> This section needs work.

|Repo|Description|
|---|---|
|[Web UI](https://github.com/stardew-valley-dedicated-server/web)|Web based admin interface based on Nuxt3 (**not fully released yet**)|
|[AsyncAPI TS](https://github.com/stardew-valley-dedicated-server/asyncapi-generator-template-ts)|AsyncAPI template to generate a strongly typed TS websocket client|
|[AsyncAPI C#](https://github.com/stardew-valley-dedicated-server/asyncapi-generator-template-cs)|AsyncAPI template to generate a strongly typed C# websocket client|

## Credits & Contributors
This project would not have been possible without the amazing work of many other people :heart:
* [Junimohost](https://github.com/JunimoHost/junimohost-stardew-server) (from [mrthinger](https://github.com/mrthinger) and [Regnivon](https://github.com/regnivon)) as the base of this dedicated server
* [AlwaysOn Mod](https://github.com/funny-snek/Always-On-Server-for-Multiplayer) (from [funny-snek](https://github.com/funny-snek)) for writing the original keep-alive logic for the server player
* [NetworkOptimizer](https://github.com/Ilyaki/NetworkOptimizer) (from [Ilyaki](https://github.com/Ilyaki))
  
If you think your name belongs here, please feel free to create a PR!



## FAQ
### Why is the Steam Client not used?
Running the client alongside the game would allow us to enable achievements and invite codes. However, we do not include it for several reasons:
* UI interaction hard to automate (focus needs mouse over steam window, hidden windows with duplicated titles etc.)
* Has known bugs not exclusive to this project (close button not working, non-deterministic behaviour in general)
* Doubles or triples the image build time, image size and startup time 
* Direct IP connection should be used instead of invite codes

### Are there other Stardew Valley servers?
There are other projects which might fit your needs:
* [norimicry/stardew-multiplayer-docker](https://github.com/norimicry/stardew-multiplayer-docker)



## Public Test Server
We have a public test server at `46.38.238.188:24642`, feel free to join and tinker around. 

Simply play the game or try to break the server in obscure ways, it will be reset regularly so nothing can really break :) 