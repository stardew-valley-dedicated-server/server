# Usage
## Web VNC
To connect to the web VNC, open your browser and navigate to `http://IP:VNC_PORT`.

The settings panel on the left side can be used to set connection quality, compression level and scaling mode. The scaling mode `Remote Resizing` should not be used, it tends to break the server and requires a restart to be fixed.

Copy and paste only works via the settings panel clipboard area.

## Console Commands
To issue commands to the SMAPI console, connect to the web VNC and use the chat.

> Note: CLI support is not implemented yet but will be added in future versions.

## Save Files
Save files are located in the `data` volume in the `Saves` directory.

Backup functionality is not built in yet, so make sure manually backup the `data` volume in regular intervals.

## Environment Variables
You can find all available environment variables [here](docs/environment-variables.md).

## Mods
To load additional mods like GenericModConfigMenu and TimeSpeed on the server, put these mods a directory and create this additional bind mount in your `docker-compose.yml`.

```yml
services:
    stardew:
        volumes:
            - ./mods:/data/Stardew/Mods/extra
```

## Upgrade the server
To download the latest game or SMAPI files, stop the server, delete the `game` docker volume and start the server again.
