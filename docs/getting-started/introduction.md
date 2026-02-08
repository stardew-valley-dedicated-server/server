# Introduction

**JunimoServer** is a Docker-based dedicated server for [Stardew Valley](https://www.stardewvalley.net/) multiplayer. It lets you run a farm that stays online 24/7, so players can join whenever they want without you needing to be there.

## Why use a dedicated server?

In normal Stardew Valley multiplayer, one player hosts the game. When they quit, everyone gets disconnected and the farm goes offline. This means coordinating schedules and hoping the host doesn't need to step away.

With JunimoServer, the farm runs independently on a server. Players drop in and out freely. The game keeps going. No more waiting for the host to get back online.

## What you'll need

- **Docker** - JunimoServer runs in containers, so Docker is required
- **A copy of Stardew Valley** - The server downloads game files using your Steam account
- **A place to run it** - Your own PC, a VPS, or any machine that can run Docker

The server works on Linux, macOS, and Windows. Most people run it on a Linux VPS for better uptime, but local hosting works fine for testing or casual use.

## Features

- **Always-on hosting** — The farm runs continuously without needing anyone to keep the game open
- **Web-based management** — Control the server through a VNC interface in your browser
- **Password protection** — Secure your farm with customizable lobby cabins where players authenticate before entering
- **Automatic backups** — SMAPI's backup system keeps your save files safe
- **Docker volumes** — Farm data persists across container restarts and updates
- **Mod support** — Install SMAPI mods just like you would in single-player
- **Configurable** — Adjust game settings, player limits, and server behavior

## Next steps

Ready to set up your server? Head to the [Prerequisites](/getting-started/prerequisites) page to make sure you have everything you need, then follow the [Installation](/getting-started/installation) guide.
