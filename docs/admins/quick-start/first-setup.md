# First Server Setup

Verify Steam authentication and get your server running.

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

Or view in VNC at `http://localhost:5800` in the SMAPI console.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Steam auth not starting | `docker compose logs steam-auth`, re-run setup |
| Token expired | `docker compose run --rm -it steam-auth setup` |
| Server can't reach steam-auth | `curl http://localhost:3001/health` |

## Next Steps

- [Server Settings](/admins/configuration/server-settings) — Customize your server
- [Password Protection](/features/password-protection/) — Secure your farm
