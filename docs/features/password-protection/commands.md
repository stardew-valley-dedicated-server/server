# Commands Reference

Lobby management commands (`!lobby`, `!authstatus`) require admin. Only `!login` and `!help` are available to unauthenticated players.

## Player Commands

These commands are available to unauthenticated players in the lobby:

| Command              | Description                    |
| -------------------- | ------------------------------ |
| `!login <password>`  | Authenticate with the password |
| `!help`              | Show available commands        |

## Admin Commands

### Authentication Status

| Command       | Description                                     |
| ------------- | ----------------------------------------------- |
| `!authstatus` | Show all connected players and their auth state |

Example output:

```
=== Auth Status ===
  PlayerOne: Authenticated
  NewPlayer: Unauthenticated (45s remaining)
  AnotherOne: Authenticated
```

## Lobby Editing Commands

| Command                | Description                                 |
| ---------------------- | ------------------------------------------- |
| `!lobby help`          | Show all available lobby commands           |
| `!lobby create <name>` | Create a new layout and enter edit mode     |
| `!lobby edit <name>`   | Edit an existing layout                     |
| `!lobby save`          | Save changes and exit edit mode             |
| `!lobby cancel`        | Discard changes and exit edit mode          |
| `!lobby spawn`         | Set spawn point at your current position    |
| `!lobby reset`         | Clear all furniture/decorations while editing |

## Lobby Management Commands

| Command                     | Description                       |
| --------------------------- | --------------------------------- |
| `!lobby list`               | Show all layouts with item counts |
| `!lobby info <name>`        | Detailed layout information       |
| `!lobby set <name>`         | Activate a layout                 |
| `!lobby rename <old> <new>` | Rename a layout                   |
| `!lobby copy <src> <dest>`  | Duplicate a layout                |
| `!lobby delete <name>`      | Delete a layout                   |

## Lobby Sharing Commands

| Command                         | Description                          |
| ------------------------------- | ------------------------------------ |
| `!lobby export <name>`          | Export layout as shareable string    |
| `!lobby import <name> <string>` | Import layout from exported string   |

Export strings are also printed to the SMAPI console/logs for easier copying.
