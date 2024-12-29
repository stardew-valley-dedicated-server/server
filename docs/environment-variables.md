# Environment Variables
These are the environment variables which can be specified at container run time.

|Variable Name|Description|Default|Available in|
|---|---|---|---|
|GAME_PORT|Game Port|24643|1.0.0|
|DISABLE_RENDERING|Disables rendering in VNC|true|1.0.0|
|STEAM_USER|Required to download the game on initial startup or for updates, but not to run the server|-|1.0.0|
|STEAM_PASS|See STEAM_USER|-|1.0.0|
|VNC_PORT|Web VNC port|8090|1.0.0|
|VNC_PASSWORD|Web VNC password|-|1.0.0|
|CI|Currently toggles between debug and release build, subject to change|false|1.0.0|
|CI_GAME_PATH|Must be set when `CI=true`|D:\Games\Steam\steamapps\common\Stardew Valley|1.0.0|
