# Issue #157 — xvnc fails to start ("not ready after 10000 msec")

**Verdict:** 🌍 not a repo bug — base-image/environment, and only under the
non-default `network_mode: host`.
**Action:** close as environment-specific (with guidance) or relabel
base-image/environment.

Companion plan `issue-157-steam-proxy-support.md` verifies the reporter's
*underlying need* (Steam behind a proxy) is solvable on the default bridge
network — i.e. host networking, the thing that breaks xvnc, is unnecessary.
Coordinate the #157 comment with that plan's Step 6 so the issue gets one
evidence-backed reply, not two.

## Root cause

- The only xvnc file this repo owns is
  `docker/rootfs/etc/services.d/xvnc/run`, which filters one arg and execs
  `Xvnc`. The startup args (`-rfbport=5900`, `-rfbunixpath=...`), the readiness
  check, and the 10 000 ms timeout all come from the base image
  `jlesage/baseimage-gui:debian-11-v4.11.3` (`docker/Dockerfile:129`) and its
  `cinit` supervisor — not this repo. There is no `is_ready` override for xvnc
  here (repo-managed `polybar` and `resize-handler` have `is_ready` scripts;
  xvnc has only `run`), so nothing in this repo can change the readiness
  behavior without a base-image-level override.
- The repo's `docker-compose.yml` uses default bridge networking (no
  `network_mode` anywhere); the failure is reported only under the non-default
  `network_mode: host`. The default configuration is unaffected.

## What cannot be determined from this repo's source

The exact reason readiness times out under host networking (a port-5900
conflict on the host, an IPv6 interaction, a proxy, ...). The issue log shows
Xvnc successfully creating the VNC server and binding the unix socket, then
`cinit` declaring "not ready" — diagnosing why requires reproducing in the
reporter's host-networking environment and inspecting the base image's `cinit`
internals. No root cause is asserted beyond what's provable.

## Task

1. Run (or fold into) `issue-157-steam-proxy-support.md` so the reply can say "you
   don't need `network_mode: host`" with evidence, not just analysis.
2. Comment on #157: the timeout is owned by the base image and only manifests
   under host networking; guidance is "avoid `network_mode: host` (it also
   breaks service-name DNS), or ensure the VNC port is free on the host".
3. Close as not-a-bug / environment-specific, or relabel base-image/environment
   — any genuine fix lives at the base-image or host-config layer. (The
   companion plan recommends leaving open pending the reporter's reply to the
   proxy guidance; defer to whichever lands first.)
