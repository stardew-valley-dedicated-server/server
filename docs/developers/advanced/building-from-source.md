# Building from Source

## Requirements

- Docker `>=20`
- git `>=2`
- make `>=4`
- .NET SDK `6`
- A local installation of Stardew Valley (via Steam)

## Setup

### 1. Clone the Repository

Clone the repository with its submodules:

```sh
mkdir sdvd-server
cd sdvd-server
git clone --recurse-submodules git@github.com:stardew-valley-dedicated-server/server.git .
```

### 2. Configure your IDE

Update `JunimoServer.csproj` to enable autocompletion and build support in your IDE.

### 3. Set the Game Path

Specify the path to your local Stardew Valley installation:

```xml
<GamePath>C:\path\to\Stardew Valley</GamePath>
```

### 4. Create Configuration

Copy the [.env.example](https://github.com/stardew-valley-dedicated-server/server/blob/master/.env.example) file from the repository.

Rename `.env.example` to `.env` and configure your server settings:

```sh
# Steam Account Details (required for downloading the game server)
STEAM_USERNAME=""
STEAM_PASSWORD=""

# VNC Server (for web-based administration access)
VNC_PASSWORD=""
```

## Usage

### Build and Start

To build and start the server:

```sh
make up
```

### View Logs

To see logs:

```sh
docker compose logs -f
```

### Stop the Server

To save and stop the server:

```sh
docker compose down
```

## Make Commands

The Makefile provides several useful commands:

| Command | Description |
|---------|-------------|
| `make up` | Build and start all containers |
| `make down` | Stop all containers |
| `make setup` | Install development dependencies |
| `make logs` | Follow container logs |
| `make clean` | Remove built images |

## Project Structure

```
server/
├── mod/                    # JunimoServer SMAPI mod
│   └── JunimoServer/       # Main mod source
├── docker/                 # Docker configuration
├── steam-service/          # Steam authentication service
├── discord-bot/            # Discord bot integration
├── docs/                   # Documentation (VitePress)
└── tools/                  # Utility scripts
```

