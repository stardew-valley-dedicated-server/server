---
description: Where to ask for help with JunimoServer, which channel fits which question, and what to include so you get a useful answer.
---

# Getting Help

Stuck on something? We've all been there.

## Check the Docs First

A quick look might save you time:

- [FAQ](/community/faq) for common questions
- [Player Troubleshooting](/players/troubleshooting) if you can't connect or play
- [Admin Troubleshooting](/admins/troubleshooting) if the server won't start or behave
- [GitHub Issues](https://github.com/stardew-valley-dedicated-server/server/issues) for known problems

## Where to Ask

| Channel | Use For |
|---------|---------|
| [Discord](https://discord.gg/w23GVXdSF7) | Quick questions, troubleshooting, discussion |
| [GitHub Issues](https://github.com/stardew-valley-dedicated-server/server/issues) | Confirmed bugs (see [Reporting Bugs](/community/reporting-bugs)) and feature requests |

## When Asking for Help

The more context you give, the faster we can help:

- OS, Docker version, JunimoServer version
- Mods installed (if any)
- What you tried and what happened
- Logs: `docker compose exec -it server diagnostics` zips logs and setup with passwords masked (see [Collect Diagnostics](/community/reporting-bugs#collect-diagnostics)). If you paste from `docker compose logs -f` instead, check the lines first: raw logs are not masked
