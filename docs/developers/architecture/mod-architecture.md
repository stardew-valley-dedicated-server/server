# Mod Architecture

Services implement `IModService` / `ModService` base class and are auto-discovered from the assembly at startup. `ModEntry.cs` registers them via Microsoft.Extensions.DependencyInjection. Each service's `Entry()` method is called after DI container construction. Services use SMAPI events and Harmony patches to hook into the game loop.

Key services (subset; full list at [`mod/JunimoServer/Services/`](https://github.com/stardew-valley-dedicated-server/server/tree/master/mod/JunimoServer/Services)):

- **AlwaysOn**: Server persistence, autonomous day transitions, festival handling
- **Api**: HTTP REST API + WebSocket for real-time updates
- **CabinManager**: Multiplayer cabin allocation and placement strategies
- **PasswordProtection**: Server password auth with rate limiting
- **HostAutomation**: Autonomous hosting via Activities system
- **GameLoader/GameCreator**: Save file management and farm creation
- **SteamGameServer**: Steam Datagram Relay (SDR) integration
