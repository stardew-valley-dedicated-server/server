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
   - Environment (OS, Docker version, JunimoServer version)
   - Logs and error messages

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
[Attach relevant log output here]

**Additional Context:**
Works fine with 10 or fewer cabins. Issue only occurs with 11+.
```

