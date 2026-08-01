A platform id is a player's Steam64 or GOG Galaxy id, which is a long number distinct from their
in-game name. Where you find it depends on the platform.

**Steam.** The best place is the player's Steam profile. Open it in a browser and look at the URL. If it
reads `steamcommunity.com/profiles/7656...`, the 17-digit number after `/profiles/` is the Steam64 id.
If the profile uses a custom name instead (`steamcommunity.com/id/somename`), paste that URL into a
lookup site such as [steamid.io](https://steamid.io) or [steamid.xyz](https://steamid.xyz) and copy the
**steamID64** it reports.

**GOG.** Use the server log: have the player connect to the server once (reaching the farmer-selection
screen is enough), then look for the `Client connected via Galaxy P2P (platform id ...)` line — that
number is their id. The ids shown on the GOG website (such as `galaxyUserId` in your account data) are
encoded differently from the id the game presents on a connection, so they will **not** match.

The same `Client connected via ...` log line also shows a Steam player's Steam64, so the connect log
works as a single lookup path for both platforms.
