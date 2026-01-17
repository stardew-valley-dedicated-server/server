# Installation

There are two ways to install JunimoServer: using a pre-built release or building from source locally.

## Use Release

The recommended approach for most users. This method uses pre-built Docker images for quick setup.

### Requirements

- Docker `>=20`

### Setup

::: info
Make sure Docker is running before proceeding with the installation.
:::

**1. Create Configuration**

Start by downloading the configuration files:
- [docker-compose.yml](https://github.com/stardew-valley-dedicated-server/server/blob/master/docker-compose.yml)
- [.env.example](https://github.com/stardew-valley-dedicated-server/server/blob/master/.env.example)

Rename `.env.example` to `.env` and configure your server settings.

Below is a minimal example configuration:

```sh
# Steam Account Details (required for downloading the game server)
STEAM_USERNAME=""
STEAM_PASSWORD=""

# VNC Server (for web-based administration access)
VNC_PASSWORD=""
```

::: warning
Your Steam credentials are only used to download the Stardew Valley server files. They are never shared or transmitted outside your local environment.
:::

**2. First-Time Setup**

Run the interactive setup to authenticate with Steam and download the game files:

```sh
docker compose run --rm -it steam-auth setup
```

This will prompt you for Steam Guard authentication (email code, mobile app, or QR code) and save a refresh token for future use.

**3. Start the Server**

After setup, start the server as a background process:

```sh
docker compose up -d
```

To see logs:

```sh
docker compose logs -f
```

**4. Stop the Server**

To save and stop the server:

```sh
docker compose down
```

## Build Locally

For developers or users who want to build from source and customize the server.

### Requirements

- Docker `>=20`
- git `>=2`
- make `>=4`
- .NET SDK `6`
- A local installation of Stardew Valley (via Steam)

### Setup

**1. Create a Working Directory**

Clone the repository with its submodules:

```sh
mkdir sdvd-server
cd sdvd-server
git clone --recurse-submodules git@github.com:stardew-valley-dedicated-server/server.git .
```

**2. Configure your IDE**

Update `JunimoServer.csproj` to enable autocompletion and build support in your IDE.

**3. Set the Game Path**

Specify the path to your local Stardew Valley installation:

```xml
<GamePath>C:\path\to\Stardew Valley</GamePath>
```

**4. Create Configuration**

Start by copying the [.env.example](https://github.com/stardew-valley-dedicated-server/server/blob/master/.env.example) file from the repository.

Rename `.env.example` to `.env` and configure your server settings.

Below is a minimal example configuration:

```sh
# Steam Account Details (required for downloading the game server)
STEAM_USERNAME=""
STEAM_PASSWORD=""

# VNC Server (for web-based administration access)
VNC_PASSWORD=""
```

### Usage

To build and start the server:

```sh
make up
```

To see logs:

```sh
docker compose logs -f
```

To save and stop the server:

```sh
docker compose down
```

## Next Steps

Now that you have JunimoServer installed, proceed to [Configuration](/getting-started/configuration) to learn about all available settings and how to customize your server.
