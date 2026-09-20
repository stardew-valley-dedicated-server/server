# SMAPI source patches

The server and test-client images build SMAPI from source (upstream tag `SMAPI_VERSION` in
`docker/Dockerfile` and `docker/Dockerfile.test-client`) with the patch series in this directory
applied, and install that build into the game volume at boot. The patches are applied in
filename order with `git apply` in the `smapi-builder` stage; one that no longer applies fails
the image build.

| Patch | Changes |
|---|---|
| `0001-derive-one-second-divisor-from-tick-rate.patch` | `GameLoop.OneSecondUpdateTicking`/`Ticked` fire once per real second at any tick rate (derived from `TargetElapsedTime`), not once per 60 ticks |
| `0002-suppress-xact-init-error-log.patch` | Drops the `[ERROR game] Game.Initialize() caught exception initializing XACT.` line a game without audio content logs on every boot |

## Updating to a new upstream release

Renovate opens the `SMAPI_VERSION` bump; if the image build then fails in the `git apply --check`
step, re-derive the drifted patches one at a time, in order, so each file keeps only its own diff:

```bash
git clone https://github.com/Pathoschild/SMAPI.git /tmp/smapi && cd /tmp/smapi
git checkout <new-tag>
# for each patch, in filename order:
git apply --3way <repo>/patches/smapi/0001-<name>.patch   # resolve conflicts, then
git add -A && git diff --cached > /tmp/0001.diff             # new body for this patch only
git commit -q -m "0001"                                     # so the next patch diffs against it
```

Replace the diff section of the patch file (everything from the first `diff --git` line) with
the new body, keeping the description header above it.

## License

SMAPI is licensed under the GNU LGPL v3 (`LICENSE.txt` in the upstream repository). The images
ship the modified build together with that notice at `/opt/smapi/LICENSE.txt`; the modified
source is the upstream tag plus this patch series.
