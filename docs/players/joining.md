# Joining a Server

How to connect to a JunimoServer-hosted farm.

## What You Need

- **Stardew Valley** (Steam or GOG)
- An **invite code** from the server admin
- Any **required mods** (ask your admin)

## Invite Codes

| Platform | Format | Example |
|----------|--------|---------|
| Steam | Starts with "S" | `S123456789` |
| GOG Galaxy | Starts with "G" | `G1234567890ABCDEF` |

## How to Connect

1. Launch Stardew Valley
2. Click **Co-op** → **Join** → **Enter Invite Code**
3. Paste the invite code
4. Click **OK**

## Password-Protected Servers

If the server requires a password, you'll spawn in a lobby. Type in chat:

```
!login serverpassword
```

On success, you'll warp to your cabin.

::: warning
Don't drop items while in the lobby. They will be lost.
:::

## First Time vs Returning

- **First time**: A cabin is created for you automatically
- **Returning**: You rejoin where your farmhand last slept, usually your cabin. If you reconnect on the same in-game day, you return to where you left off.

## Connection Methods

| Method | Platform | Notes |
|--------|----------|-------|
| Steam SDR | Steam | Most reliable (~99% success); use it when you have both codes |
| GOG Galaxy | GOG | Works through most networks (~50% success) |
| Direct IP | Any | Disabled by default and [not recommended](/admins/operations/networking#direct-ip) |

Most servers use Steam SDR or GOG Galaxy. No special setup needed on your end. Admins can find ports and setup details under [Networking](/admins/operations/networking#connection-methods).
