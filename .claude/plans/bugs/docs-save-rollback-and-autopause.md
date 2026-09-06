# Docs: previous-day save rollback is undocumented and the auto-pause description is stale

**Status:** ready-to-implement
**Priority:** 3 (high)
**GitHub Issue(s):** none (Discord report)
**Area:** docs
**Related:** [`autopause-skips-festival-days.md`](./autopause-skips-festival-days.md) (the code fix; until it merges, the docs must describe the festival exception as current behavior); [`../features/restore-day-transition-backup.md`](../features/restore-day-transition-backup.md) (different feature: an in-mod hook around SMAPI's daily zip, not the game's `_old` pair)
**Observed:** production report — an operator lost a festival day and asked whether the game's `_old` save copy should exist and how to go back one day. Nothing in `docs/` answers either question. Separately, `docs/features/server-mechanics.md` quotes a 6:10 AM pause start that the code changed to 6:00 AM, and describes the 1:00 AM boundary as an "unpause" that a paused server can never reach.
**Next step:** apply the four doc edits below in one PR; file the follow-up command as its own feature plan when picked up

## Symptom

- No page mentions the game's own previous-save copy (the `_old` files inside every save folder), whether the server keeps it, or how to roll back one day with it. The backup page covers only SMAPI's daily zips and whole-volume tarballs, so the fastest recovery for "we lost a day" is invisible to operators.
- `docs/features/server-mechanics.md` "Pause Behavior" is wrong on two points and silent on a third: it says the pause starts at 6:10 AM (the code pauses from 6:00), it says the game "unpauses" after 1:00 AM (a paused clock never reaches 1:00 AM; the rule is that an empty server is *not paused* once the clock is already past 1:00 AM, so a day players carried into the night still closes at the 2:00 AM pass-out), and it omits that festival days are never auto-paused at all.
- `docs/community/faq.md` "Does time pass when I'm offline?" states the pause with no festival caveat.
- The `saves` command docs and the backup page never say how the server decides which save to load, so an operator with several folders under `Saves/` cannot tell which one the rollback should target.

## Root cause

The `_old` behavior is vanilla (`SaveGame.getSaveEnumerator`, `BackupNameSuffix = "_old"`): every save writes to a temp file, renames the current data file and `SaveGameInfo` to `<name>_old` / `SaveGameInfo_old`, then moves the new files into place. The game only reads the pair as a crash-recovery fallback (`SaveGame.TryReadSaveFileWithFallback`) and never surfaces it as "load yesterday". The server does not patch the save routine, and the mod's on-demand save (`SaveNow.TrySave`) drives the same enumerator, so the rotation happens on the server too. Nothing in the mod deletes `_old` files; the save-import and test clone paths merely skip copying them into a new folder. None of this was ever written down for operators.

The pause docs drifted when `HandleAutoPause` moved the floor from 6:10 to 6:00 (the "pause the empty server at 6:00 instead of 6:10" change) and were imprecise about the 1:00 AM ceiling from the start. The festival exclusion has been in the code since the initial commit and was never documented.

The active save is an explicit pointer, not a folder scan: `GameLoaderService` reads `SaveNameToLoad` from SMAPI global data under the key `JunimoHost.GameLoader`, which SMAPI stores as `.smapi/mod-data/junimohost.server/junimohost.gameloader.json` at the root of the saves volume (`/config/xdg/config/StardewValley/`, one level *above* `Saves/`; the key is lower-cased in the filename).

## Fix

All snippets below were executed against the local image during the investigation: the swap ran end to end on a dummy save folder, and the missing-file, empty-file, and leftover-`_swap` paths each aborted before any rename. The `docker compose run` invocation with the multi-line single-quoted script was verified from PowerShell. Keep them verbatim unless re-tested.

### 1. `docs/features/backup.md`

- **Overview table:** add a row `Previous save (game) | Every save | \`_old\` files inside the save folder | Game`.
- **New H2 "Previous-Save Copy (`_old` files)"** after "SMAPI Automatic Backups":
  - What the game keeps and how it rotates (one paragraph, no code).
  - That it is one *save* behind, not strictly one *day*: cabin migration and the `farmhand` command trigger an extra save without a day change, and after one of those `_old` is today's earlier state. Tell the reader to check the printed dates before swapping.
  - That the server never deletes the pair, and that it is not part of SMAPI's zips.
  - Check command:
    ```sh
    docker compose exec server sh -c 'ls -la /config/xdg/config/StardewValley/Saves/*/*_old'
    ```
- **New H3 "Finding your active save name"** (the rollback needs it):
  ```sh
  docker compose exec server sed -n 's/.*"SaveNameToLoad": *"\([^"]*\)".*/\1/p' /config/xdg/config/StardewValley/.smapi/mod-data/junimohost.server/junimohost.gameloader.json
  ```
  One sentence that this pointer, not the folder listing, decides what the server loads, so a folder rename without updating it breaks startup. Link to the `saves` command for changing it.
- **New H3 "Rolling back one day"**: the stop / run / start block below, followed by the notes.
  ```sh
  docker compose stop server

  docker compose run --rm --no-deps --entrypoint sh server -c '
    set -eu
    SAVE=Junimo_474497955193478706
    cd /config/xdg/config/StardewValley/Saves/$SAVE
    for f in $SAVE SaveGameInfo ${SAVE}_old SaveGameInfo_old; do
      [ -s "$f" ] || { echo "missing or empty: $f"; exit 1; }
    done
    [ ! -e ${SAVE}_swap ] && [ ! -e SaveGameInfo_swap ] || { echo "leftover _swap files, clean up first"; exit 1; }
    BACKUP=/config/xdg/config/StardewValley/Backups/${SAVE}_$(date +%Y%m%d-%H%M%S)
    mkdir -p "$BACKUP" && cp -a . "$BACKUP" && echo "backup: $BACKUP"
    echo "current: $(grep -oE "<(dayOfMonth|currentSeason|year)>[^<]*" $SAVE | tr "\n" " ")"
    echo "old:     $(grep -oE "<(dayOfMonth|currentSeason|year)>[^<]*" ${SAVE}_old | tr "\n" " ")"
    mv $SAVE ${SAVE}_swap && mv SaveGameInfo SaveGameInfo_swap
    mv ${SAVE}_old $SAVE && mv SaveGameInfo_old SaveGameInfo
    mv ${SAVE}_swap ${SAVE}_old && mv SaveGameInfo_swap SaveGameInfo_old
    echo "swapped. main save now: $(grep -oE "<(dayOfMonth|currentSeason|year)>[^<]*" $SAVE | tr "\n" " ")"
  '

  docker compose start server
  ```
  Notes to include: replace the `SAVE=` line; run from the compose directory in PowerShell, Git Bash, or a Unix shell (not cmd.exe); it prints both dates before swapping and the main save's date after; running it twice swaps back; every rename stays inside the save folder so an abort at any step leaves all files present under some name; the copy in `Backups/` sits outside `Saves/` so the game never lists it as a second save, and the boot-time ownership sweep fixes its owner. Do not explain the `--rm` / `--no-deps` flags or the CRLF edge case; a pasted snippet carries plain line feeds, and a CRLF copy fails on the first line before touching anything.
- **New H3 "Cleaning up rollback backups"**:
  ```sh
  docker compose exec server ls -la /config/xdg/config/StardewValley/Backups
  docker compose exec server rm -rf /config/xdg/config/StardewValley/Backups
  ```
  plus the single-backup variant with `/<BACKUP_NAME>`, and a restore line (`cp -a .../Backups/<BACKUP_NAME>/. .../Saves/<SAVE>/` with the server stopped).
- **Recovery Scenarios:** add "Lost a day" pointing at the rollback section, before "Corrupted Save".

### 2. `docs/admins/troubleshooting.md`

Under the save-issues section, next to "Save corruption", add "A day was lost / need to go back one day" with two sentences and a link to the new backup section. No duplicated commands.

### 3. `docs/features/server-mechanics.md`

Replace the three "Pause Behavior" bullets with:

- **Players online:** game runs normally.
- **No players:** the clock is held wherever it stopped, any time between 6:00 AM and 1:00 AM.
- **Past 1:00 AM with no players:** the game is not paused, so a day that players carried into the night still ends at the 2:00 AM pass-out instead of hanging.
- **Festival days:** the empty-server pause does not apply, so a festival day nobody is online for runs through to the 2:00 AM pass-out and the next day begins.

When `autopause-skips-festival-days.md` merges, rewrite the last bullet to "the pause applies as on any other day; if the last player leaves during a festival, the festival ends first and the pause engages once the host is home." Note this dependency in the PR description, not in the doc.

### 4. `docs/community/faq.md`

Extend "Does time pass when I'm offline?" with one sentence: festival days are the exception today, the clock keeps running with nobody online, and link to the rollback section for recovering a lost day. Same post-merge rewrite as above.

### 5. Developer note on `IMAGE_VERSION` (small, same PR)

`docs/developers/contributing/index.md`, in the local setup section near `make install`: one sentence that `make` targets export `IMAGE_VERSION=local` for compose, so a direct `docker compose run` / `exec` outside `make` resolves to `sdvd/server:latest` and pulls it; set `IMAGE_VERSION=local` in `.env` or the shell to reuse the local build. The E2E page already lists the variable in its table; link to it rather than repeating.

### 6. Rule touch-up (same PR)

`.claude/rules/smapi-global-data-lives-on-saves-volume.md`: add that SMAPI lower-cases the key in the filename (`JunimoHost.GameLoader` becomes `junimohost.gameloader.json`), and that the directory sits at the volume root beside `Saves/`, not inside it. Both cost a first-time reader a failed `cat`.

## Verification

- `bun run build` in `docs/` (VitePress) passes with no dead links; the new anchors resolve from troubleshooting and the FAQ.
- The three commands under "Previous-Save Copy" and "Finding your active save name" run as printed against a running server via `docker compose exec`.
- The rollback block runs as printed against a throwaway copy of a real save folder and prints two different dates; running it a second time restores the original.
- The `IMAGE_VERSION` sentence is confirmed by running `docker compose run --rm --no-deps --entrypoint true server` with and without the variable and observing whether a pull happens.

## Out of scope

- The auto-pause code change itself: `autopause-skips-festival-days.md`.
- A `POST /backup` / in-mod backup hook: `../features/restore-day-transition-backup.md`.

## Follow-up: a `saves rollback` command

Everything the shell block does could be a server-side command, which would remove the shell and the Docker path knowledge from the operator's side entirely.

- **Shape:** `saves rollback` console command (sibling of the existing `saves` subcommands in `SavesCommand`), optionally exposed as an authenticated API endpoint next to the other `saves` operations.
- **Preconditions, all checked before touching a file:** no players online (`OnlineFarmers.CountOthers() == 0`, the same gate `SaveNow` documents for operator saves), no day transition in flight (`GameManagerService.IsDayTransitionComplete()`), both `_old` files present and non-empty, no leftover `_swap` files.
- **Steps:** copy the folder to `Backups/<save>_<timestamp>` at the volume root, log the current and `_old` dates parsed from the XML (a real parser, not the regex the shell uses), perform the same three-step in-folder rename, then trigger the existing reload path (`GameManagerService.RequestReloadSave`) so the server comes up on the restored day without a container restart.
- **Output:** chat/console line naming both dates and the backup folder; refuse with a specific reason when any precondition fails.
- **Docs:** once it exists, the rollback H3 above shrinks to the command plus a "manual fallback" link to a collapsed shell block.
- **Test:** an E2E that sleeps through one day on a dedicated server, runs the command, and asserts via `/status` that the date went back by one and that `Saves/<save>/` still holds four files.

Not part of this docs PR; open it as `../features/saves-rollback-command.md` when picked up and link it from here.
