---
paths:
  - "docs/public/**"
  - "Makefile"
  - "docker-compose*.yml"
---

# Test the installer against unpublished images with `IMAGE_VERSION=local`, never a shadow tag

To exercise `install.sh` / `install.ps1` before the images are published, build both images with `make build` and run the installer with `IMAGE_VERSION=local` from a folder that has no `docker-compose.yml`. Don't `docker tag` a local build over `preview`/`latest`: every explicit `docker pull` and the update path's `docker compose pull` silently replace the shadow, and the next run is back on the published image.

**Why:** A local steam-service build tagged over `preview` fixed the "Account 0 not configured" boot, then an update run pulled the published tag back and the same error returned with no visible change. A local server build stamped with an unpushed short commit hash made the installer fetch `docker-compose.yml` from a `raw.githubusercontent.com` URL that 404s, and `make build-server` stamps `git describe --always --dirty`, so a dirty tree yields `<sha>-dirty`, which can never resolve either.

**How to apply:** The installer must keep two behaviours for `local` to work: fall back to master for any image stamp that isn't a plain hex hash, and accept a locally present image when the pull fails. `make build-server` needs Steam credentials as `.env` entries or make variables (`make build-server STEAM_USERNAME=… STEAM_PASSWORD=…`); without them the game-download stage fails before compiling anything. Setting `IMAGE_VERSION` skips the channel prompt, so that prompt is the one part not covered. The update path pulls unconditionally and can only be tested against published images, so say so rather than faking it. If a run behaves like the fix isn't there, compare `docker image ls` IDs against the running container's image before theorizing about the code.
