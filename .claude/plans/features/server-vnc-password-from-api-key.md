# Reuse API_KEY as the VNC password (single-secret onboarding)

## Context

Onboarding asks operators for two independent secrets: `API_KEY` (HTTP API) and `VNC_PASSWORD`
(web admin page). We already steer users away from VNC ("No VNC Needed" tip in
`docs/admins/quick-start/installation.md`), yet `docker/rootfs/startapp.sh` still refuses to start
without a VNC password. Goal: when `VNC_PASSWORD` is unset, fall back to `API_KEY`, so a fresh
install needs only `STEAM_*` + `API_KEY`.

**Outcome:** `VNC_PASSWORD` unset + `API_KEY` set → the VNC web page is protected by the API key.
An operator who sets `VNC_PASSWORD` (or drops a `/config/.vncpass` file) still wins.

## Investigation findings (the expensive part — already done)

- **VNC password is owned by the jlesage base image**, not our code. `/etc/cont-init.d/10-vnc-password.sh`
  reads `VNC_PASSWORD` from the environment and writes `/tmp/.vncpass` via
  `echo "$VNC_PASSWORD" | /opt/base/bin/vncpasswd -f`, then `chown "$USER_ID:$GROUP_ID"` + `chmod 400`.
  It also supports an operator file path: `/config/.vncpass_clear` → obfuscated to `/config/.vncpass`,
  and `/config/.vncpass` (if present) wins over the env var. `/tmp/.vncpass` is ephemeral (regenerated
  each boot); `/config/.vncpass` persists on the config volume.
- **You cannot feed `VNC_PASSWORD` into `10-vnc-password.sh` from another cont-init script.** jlesage
  loads `/etc/cont-env.d` once (the `[cont-env]` stage) *before* cont-init runs, so `set-cont-env`
  (which writes `/etc/cont-env.d/NAME`) from an earlier cont-init script is **not** visible to a later
  cont-init script — verified empirically (a `set-cont-env` in script 09 read back EMPTY in script 11;
  it only affects services, which start after all cont-init). `/run/s6/container_environment` does not
  exist in jlesage's init. So the env-propagation approach is a dead end.
- **jlesage skips non-executable cont-init scripts.** A bind-mounted script (no exec bit on a Windows
  host) never ran; a `COPY` + `chmod 0755` in a derived image did. The server stage in `docker/Dockerfile`
  only explicitly `chmod +x`es `/etc/cont-init.d/50-server-init.sh` — a new script must be added to that
  same `chmod` (rootfs `COPY` exec-bit preservation is not relied on there).

## Design

Mirror jlesage's own env branch in a **new cont-init script that runs after `10-vnc-password.sh`**,
sourced from `API_KEY` instead of `VNC_PASSWORD`:

- Add `docker/rootfs/etc/cont-init.d/11-vnc-password-from-api-key.sh`: when `VNC_PASSWORD` is empty,
  `API_KEY` is set, and `/config/.vncpass` is absent, run
  `echo "$API_KEY" | /opt/base/bin/vncpasswd -f > /tmp/.vncpass`, then `chown "$USER_ID:$GROUP_ID"` +
  `chmod 400` (identical to `10-vnc-password.sh`'s env branch). `/tmp` is cleared by
  `08-clear-tmp-dir.sh` before this runs, and nothing clears it again, so the file survives to the
  `xvnc` service. Non-sticky and defers to both operator paths (`VNC_PASSWORD`, `/config/.vncpass`).
- Add the new script to the explicit `chmod +x` list in `docker/Dockerfile` (server stage), alongside
  `50-server-init.sh`.
- Update `validate_environment` in `docker/rootfs/startapp.sh`: warn/abort for VNC only when
  `VNC_PASSWORD` **and** `API_KEY` are both empty and `/config/.vncpass` is absent (mirror the full
  resolution order). Reword the VNC warning box to say `API_KEY` also secures VNC.
- Docs: `docs/admins/quick-start/installation.md` step 2 — drop `VNC_PASSWORD` from the minimal
  example (only `API_KEY` remains) and note that VNC reuses it. `.env.example` — annotate `VNC_PASSWORD`
  as "defaults to `API_KEY`". Check `docs/admins/operations/vnc.md` and
  `docs/admins/configuration/environment.md` for the same wording.

## Security note

Reusing one secret couples the two surfaces (leaking `API_KEY` also exposes the VNC page). Acceptable
given VNC is de-emphasized, but state it in the docs so operators who want isolation know to set
`VNC_PASSWORD` explicitly.

## Verification (boot tests)

Test cheaply with a derived image (`FROM sdvd/server:local`, `COPY` the script + `chmod 0755`) — this
runs the script exactly as jlesage will in the real build, no full rebuild needed for the logic. CI's
real build bakes the rootfs.

1. `API_KEY` set, `VNC_PASSWORD` unset → `/tmp/.vncpass` created, owned `USER_ID:GROUP_ID`, mode 400;
   confirm a VNC client authenticates with the API key.
2. `VNC_PASSWORD` set → jlesage's `10` creates `/tmp/.vncpass`; our script is a no-op.
3. Neither set (and no `/config/.vncpass`) → no password file; `startapp.sh` warns and aborts unless
   `ALLOW_INSECURE_SETUP=true`.
