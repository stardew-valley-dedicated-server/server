# Reporting Bugs

Found something broken? Every bug report helps improve the project.

## Bug vs Help Request

**Bugs:** crashes, features not working as documented, data corruption, unexpected errors.

**Not bugs:** setup questions, feature requests, general questions. For those, use [Discord](https://discord.gg/w23GVXdSF7).

## Search First

Check [open issues](https://github.com/stardew-valley-dedicated-server/server/issues) before reporting. If you find a similar issue, comment there instead of creating a duplicate.

## How to Report

1. Go to [GitHub Issues](https://github.com/stardew-valley-dedicated-server/server/issues)
2. Click "New Issue" and select the bug report template
3. Fill out the template with:
   - Clear title
   - Steps to reproduce
   - Expected vs actual behavior
   - Environment and logs — the diagnostics bundle below gathers these for you

## Collect Diagnostics

Rather than copying logs by hand, run the diagnostics bundle. It gathers the server logs,
version, and setup into a single zip:

```sh
docker compose exec -it server diagnostics
```

The zip is saved in a `diagnostics` folder next to your `docker-compose.yml` file. Attach it to
your issue — it covers the environment and logs the template asks for. See the [diagnostics
command](/admins/operations/commands) for what's included.

::: tip
This command is part of the standard server image. The [modern (Alpine)
image](/admins/operations/modern-docker) doesn't include it, so attach your logs manually there.
:::

## Found a Fix?

Comment on the issue or submit a PR. See [Contributing](/community/contributing).

## Good Bug Report Example

```markdown
**Title:** Server crashes when loading modded save with > 10 players

**Description:**
The server crashes immediately when trying to load a save file that
has more than 10 player cabins with certain mods installed.

**Steps to Reproduce:**
1. Install Expanded Cabins mod
2. Create a farm with 12 cabins
3. Restart the server
4. Server crashes during load

**Expected Behavior:**
Server should load the save successfully

**Actual Behavior:**
Server crashes with NullReferenceException

**Environment:**
- OS: Ubuntu 22.04
- Docker: 24.0.5
- JunimoServer: 1.0.0
- Mods: Expanded Cabins v2.1.0

**Logs:**
[Attach the diagnostics zip, or paste relevant log output]

**Additional Context:**
Works fine with 10 or fewer cabins. Issue only occurs with 11+.
```

