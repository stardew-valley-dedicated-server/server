A platform id is a player's Steam64 or GOG Galaxy id, which is a long number distinct from their
in-game name. Where you find it depends on the platform.

**Steam.** The best place is the player's Steam profile. Open it in a browser and look at the URL. If it
reads `steamcommunity.com/profiles/7656...`, the 17-digit number after `/profiles/` is the Steam64 id.
If the profile uses a custom name instead (`steamcommunity.com/id/somename`), paste that URL into a
lookup site such as [steamid.io](https://steamid.io) or [steamid.xyz](https://steamid.xyz) and copy the
**steamID64** it reports.

**GOG.** Log in at [gog.com](https://www.gog.com) in a browser, open
[gog.com/userData.json](https://www.gog.com/userData.json), and copy the **`galaxyUserId`** value. Make
sure you take `galaxyUserId` and not `userId`, which is a separate, shorter number.
