# Make References Cleanup

We currently have hardcoded references to run "make" commands in the **runtime startup banner**, even for the remotely pushed docker image where `make` is not available or intended to be used. Users who just pull the image and don't build it locally do not use `make` — they use normal `docker compose` commands.

See the "make setup" in the "game files not found" startup banner:

```
Game files not found! Please run setup first:
make setup
```

## Objective

Log a hint that works for people who only run the image after pulling it: instead of `make setup`, show the equivalent raw command `docker compose run --rm -it steam-auth setup`.

## Scope (verified)

The banner appears in **two** runtime scripts, byte-identical in wording. Both must be changed:

- `docker/rootfs/startapp.sh:122-124` (glibc/Debian image)
- `docker/modern/rootfs/opt/bin/start-game.sh:96-98` (Alpine/musl image)

Each prints a fixed-width ASCII box:

```
╔═══════════════════════════════════════════════════════════════════════╗
║  Game files not found! Please run setup first:                        ║
║                                                                       ║
║  make setup                                                           ║
╚═══════════════════════════════════════════════════════════════════════╝
```

**Out of scope** — every other `make setup` reference is in the test harness, developer docs, or CI, where `make` *is* the correct tool and the audience has it:
- `tests/**` (DownloadValidationTests, SharedSteamAuth, GameDataDistributor, fixtures) — developers run `make`.
- `docs/developers/**`, `Makefile`, `.github/workflows/e2e-tests.yml` — build/contributor surfaces.

Do not touch these.

## The replacement command is verified correct

`make setup` (Makefile:105-108) runs exactly:

```
docker compose run --rm -it $(if .env.test exists, --env-from-file .env.test) steam-auth setup
```

So `docker compose run --rm -it steam-auth setup` is the image-user equivalent, minus the test-only `--env-from-file .env.test` flag (correctly dropped — that file is a test artifact, not present for a pull-and-run user). The service name `steam-auth` is confirmed in both `docker-compose.yml:44` and `docker/modern/docker-compose.yml:51`.

## Box-alignment gotcha

The banner is a hand-padded fixed-width box. The new command is ~46 chars vs. 10 for `make setup`, so the trailing-space padding on that line **must be recomputed** so the right `║` border stays aligned. Measure the inner width from an existing box line (e.g. the top border `╔═…═╗`) and pad the command line to match; don't eyeball it.

If the command plus padding exceeds the existing box width, widen the whole box (all five border lines) rather than letting one line overflow.

## Changes

For each of the two banner scripts, replace the `make setup` line with `docker compose run --rm -it steam-auth setup`, preserving each script's existing wrapping (`echo -e "\e[33m…\e[0m"` vs. `print_warning "…"`) and the `║  ` / ` ║` borders. Pad the trailing spaces so the right border lines up with the rest of the box (see the gotcha above).

- `docker/rootfs/startapp.sh:124`: `echo -e "\e[33m║  docker compose run --rm -it steam-auth setup<PAD> ║\e[0m"`
- `docker/modern/rootfs/opt/bin/start-game.sh:98`: `print_warning "║  docker compose run --rm -it steam-auth setup<PAD> ║"`

`<PAD>` = enough trailing spaces to match the box's inner width.

## Verification

- Visual: re-render each banner (or print the literal box) and confirm the right `║` border aligns across all five lines.
- Grep `docker/**/*.sh` for `make ` afterward — should return zero runtime-banner hits.
- Optional smoke: start the image with an empty game volume and confirm the new hint prints (the banner is gated on `[ ! -e "${STEAM_AUTH_GAME_EXEC}" ]`).
