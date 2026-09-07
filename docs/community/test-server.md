---
description: Join the public JunimoServer test server — a free shared server running the newest preview build, with live status and invite codes for Steam and GOG.
---

<script setup>
const apiUrl = __DOCS_TEST_SERVER_API_URL__;
</script>

# Public Test Server

Try JunimoServer before you host your own. This is a free, shared server running the newest preview build. Anyone can join, but it is a testing ground, not a home for a long-term farm.

<ServerStatusWidget v-if="apiUrl" :api-url="apiUrl" />

::: warning Experimental server
Restarts and resets happen without notice, so don't get attached to anything you build here.
:::

## Who it's for

Join if you're happy testing the rough edges: you want to help test preview builds and don't mind bugs, restarts, or lost progress.

::: tip Want a farm that lasts?
[Host your own server](/admins/) and keep what you build.
:::

## What to expect

- **Restarts.** The server restarts whenever a new preview build ships.
- **Bugs.** Preview builds contain changes that haven't been through a stable release yet.
- **Resets.** The farm may be rolled back or wiped, and maintainers may change the time, spawn items, or otherwise poke at the world while chasing a bug. Items, progress, and characters can be lost at any time.

## How you help

- **Play.** Farm, fish, fight, and take the place apart. It helps find bugs and edge cases.
- **Report bugs.** See [Reporting Bugs](/community/reporting-bugs) and mention it happened on the test server.
- **Share feedback.** Questions and ideas go to [Discord](https://discord.gg/w23GVXdSF7).

## How to join

1. Copy the invite code for your platform from the status card above.
2. Enter it in the game as described in [Joining a Server](/players/joining).

| Status | Meaning |
|--------|---------|
| **Online** | Ready to join |
| **Starting** | Booting or loading the save |
| **Busy** | Saving or changing the day |
| **Offline** | Not reachable |

Starting, Busy, and Offline usually clear within a few minutes.
