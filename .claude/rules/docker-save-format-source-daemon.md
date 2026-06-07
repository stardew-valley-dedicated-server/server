---
paths:
  - "tests/JunimoServer.TestRunner/Distribution/**"
  - ".github/workflows/e2e-tests.yml"
---

# `docker save` blob format is set by the source daemon's image store, not the images

`docker save` output format depends on the **source daemon's image store**, not on the images themselves:

- A daemon running the **containerd snapshotter** (`docker info` → `io.containerd.snapshotter.v1`) emits the **OCI layout with gzip-COMPRESSED blobs** (`blobs/sha256/…` + `oci-layout`).
- A daemon using the classic **overlay2 store with no containerd image store** emits the **legacy format with UNCOMPRESSED `layer.tar` blobs** — which equals the sum of uncompressed `docker image .Size`, roughly 2.5× the compressed save.

The `[ImageTransfer]` byte counter (`ImageDistributor.ByteCountingStream.BytesRead`, off `/images/get`) reflects this format difference, not a real size change. The buildx `docker-container` driver does NOT change it — `docker save` targets a daemon (the distributor's `_localClient`), not the builder. The lever is the daemon's image store.

**CI enables the store by reconfiguring the runner's EXISTING daemon in place — NOT `docker/setup-docker-action`.** That action installs a SECOND dockerd on a new socket and `docker context use`s it, leaving the pre-installed daemon alive on `/var/run/docker.sock`. `make build`'s `docker buildx --load` follows the active context → image lands in the NEW daemon; but the distributor's Testcontainers `_localClient` has no docker-context provider and falls through to the OLD socket → `InspectImageAsync("sdvd/server:local")` 404s ("No such image") before any test runs, and the compressed-save fix never reaches the save path. The two-daemon split is the trap. The fix eliminates the split rather than bridging it: the `Enable containerd image store on the daemon` step (`.github/workflows/e2e-tests.yml`, first step) merges `{"features":{"containerd-snapshotter":true}}` into `/etc/docker/daemon.json` (via jq + temp file — `cat f | tee f` truncates; `// {}` covers a runner with no daemon.json), `systemctl restart docker`s, waits for the socket, then fails fast unless `docker info` reports `Storage Driver: overlayfs` (the containerd store; classic store reports `overlay2`). One daemon on `/var/run/docker.sock`, so build / compose / `_localClient` all agree — no `DOCKER_HOST` plumbing. `setup-docker-action` with `set-host: true` is a valid alternative (exports `DOCKER_HOST` so every client dials the one new daemon) but is NOT what this repo does — don't reintroduce a second daemon without re-checking the split.

**How to apply:** When a transfer-size delta surprises you, check each daemon's image store before suspecting the images. When touching the CI Docker setup step, keep the store enabled on the ONE pre-installed daemon (in-place daemon.json + restart) — don't reintroduce `setup-docker-action`'s second daemon, and if you do, `set-host: true` is then mandatory.
