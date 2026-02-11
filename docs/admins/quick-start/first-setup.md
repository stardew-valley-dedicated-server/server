# First Server Setup

## Steam Guard Methods

| Method | How It Works |
|--------|--------------|
| Email Code | Enter code from email |
| Mobile App | Enter code or approve notification |
| QR Code | Scan with Steam app (no password needed) |

If you haven't run setup yet:

```sh
docker compose run --rm -it steam-auth setup
```

Tokens last ~200 days. Re-run setup before expiry.

## Verify Server

```sh
docker compose ps      # Both containers should show "Up"
docker compose logs -f # Look for "Ready for players"
```

## Get Invite Code

```sh
docker compose exec server attach-cli
# Type: info
```

## Connect with Your Game

Once you have the invite code, connect like any other multiplayer game:

1. Launch Stardew Valley
2. Click **Co-op** → **Enter Invite Code**
3. Paste the invite code
4. You're in!

No special tools needed — just your normal game client.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Steam auth not starting | `docker compose logs steam-auth`, re-run setup |
| Token expired | `docker compose run --rm -it steam-auth setup` |
| Server can't reach steam-auth | `docker compose exec server wget -qO- http://steam-auth:3001/health` |

