# Planned Features
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
