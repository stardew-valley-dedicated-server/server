# Chat Command Pagination

## Context

Chat commands like `!lobby help` (17 lines), `!info` (10 lines), and `!help` (17 lines for 17 registered commands) each send individual `SendPrivateMessage` calls per line. The game's ChatBox has a **10-message** FIFO buffer (`defaultMaxMessages = 10`, `maxMessages = 10` — verified in decompiled `ChatBox.cs` lines 20 and 47). Early messages are evicted before the player reads them.

The current workaround is `maxMessages = 20` on the test-client (`tests/test-client/ModEntry.cs:1016`, comment at `:1017` notes "default is 10, which truncates long command outputs"), which masks the real UX problem. The fix is automatic pagination: buffer command output, send only one page of lines, and let the player request the next page.

Each line stays as its own ChatMessage (preserving the game's prefix formatting/alignment).

## Design: Scope-Based Pagination

### How It Works

1. **Before dispatch**: `OnChatMessage` extracts page number from args (if present), strips it
2. **Scope created**: A `ChatResponseScope` is set as `ChatResponseContext.Current`
3. **Command runs**: All `SendPrivateMessage` calls are intercepted and buffered (lines stay individual)
4. **Flush**: The scope sends only the lines for the requested page. If there are more pages, appends a footer

### Page Size

`linesPerPage = 8` — leaves room in the 10-message buffer for the player's own command echo + the pagination footer.

### Footer Format

When output exceeds one page, an extra message is sent after the page's lines:

```
(page 1/3 — !lobby help --page 2)
```

This reconstructs the original command with `--page N` for the next page, so the player can copy/type it.

### Page Number Extraction

Supports three syntaxes (all equivalent):
- `--page N` — CLI-style flag (e.g., `!lobby help --page 2`)
- `page:N` — compact colon syntax (e.g., `!lobby help page:2`)
- `-p N` — short flag (e.g., `!lobby help -p 2`)

The footer uses `--page N` for discoverability.

**Parsing**: Before dispatch, scan the args array for any of the three patterns. Extract the page number, remove the matched token(s) from args, pass cleaned args to the command. This is unambiguous — no command uses `--page`, `page:`, or `-p` as argument names.

Examples:
- `!lobby help` → args `["help"]`, page 1
- `!lobby help --page 2` → args `["help"]`, page 2
- `!lobby help page:2` → args `["help"]`, page 2
- `!lobby help -p 2` → args `["help"]`, page 2
- `!info --page 2` → args `[]`, page 2
- `!help lobby` → args `["lobby"]`, page 1

## Files to Create

### `mod/JunimoServer/Services/ChatCommands/ChatResponseScope.cs`

```csharp
public class ChatResponseScope
{
    private readonly List<(long playerId, string line)> _buffer = new();
    private const int LinesPerPage = 8;

    public void BufferLine(long playerId, string line)
    {
        _buffer.Add((playerId, line));
    }

    public void Flush(Action<long, string> sendLine, int page, string commandForFooter)
    {
        // Group by playerId (nearly always one target)
        foreach (var group in _buffer.GroupBy(b => b.playerId))
        {
            var lines = group.Select(g => g.line).ToList();
            var totalPages = (int)Math.Ceiling(lines.Count / (double)LinesPerPage);

            // Clamp page
            page = Math.Clamp(page, 1, Math.Max(1, totalPages));

            // If fits in one page, send all — no footer
            if (totalPages <= 1)
            {
                foreach (var line in lines)
                    sendLine(group.Key, line);
                continue;
            }

            // Send requested page
            var skip = (page - 1) * LinesPerPage;
            var pageLines = lines.Skip(skip).Take(LinesPerPage);
            foreach (var line in pageLines)
                sendLine(group.Key, line);

            // Footer with next page hint
            if (page < totalPages)
                sendLine(group.Key, $"(page {page}/{totalPages} — {commandForFooter} --page {page + 1})");
            else
                sendLine(group.Key, $"(page {page}/{totalPages})");
        }
        _buffer.Clear();
    }
}
```

### `mod/JunimoServer/Util/ChatResponseContext.cs`

Static holder to decouple `ModHelperExtensions` from `ChatCommandsService`:

```csharp
public static class ChatResponseContext
{
    public static ChatResponseScope? Current;
}
```

## Files to Modify

### `mod/JunimoServer/Util/ModHelperExtensions.cs`

In `SendPrivateMessage`: check for active scope, buffer if present.

```csharp
public static void SendPrivateMessage(this IModHelper helper, long uniqueMultiplayerId, string msg)
{
    var scope = ChatResponseContext.Current;
    if (scope != null)
    {
        scope.BufferLine(uniqueMultiplayerId, msg);
        return;
    }
    helper.GetMultiplayer()
        .sendChatMessage(LocalizedContentManager.CurrentLanguageCode, msg, uniqueMultiplayerId);
}
```

### `mod/JunimoServer/Services/ChatCommands/ChatCommands.cs`

In `OnChatMessage`, extract page number from args, wrap dispatch with scope:

```csharp
var (args, page) = ExtractPageNumber(receivedMessage.Command.Args);

// Reconstruct the command string for the footer (without page arg)
var commandForFooter = "!" + receivedMessage.Command.Name
    + (args.Length > 0 ? " " + string.Join(" ", args) : "");

ChatResponseContext.Current = new ChatResponseScope();
try
{
    command.Action(args, receivedMessage);
}
finally
{
    ChatResponseContext.Current.Flush(
        (playerId, msg) => _helper.GetMultiplayer()
            .sendChatMessage(LocalizedContentManager.CurrentLanguageCode, msg, playerId),
        page, commandForFooter);
    ChatResponseContext.Current = null;
}
```

Add helper method to parse all three page syntaxes:

```csharp
private static (string[] args, int page) ExtractPageNumber(string[] args)
{
    int page = 1;
    var cleaned = new List<string>(args);

    for (int i = 0; i < cleaned.Count; i++)
    {
        // --page N
        if (cleaned[i] == "--page" && i + 1 < cleaned.Count
            && int.TryParse(cleaned[i + 1], out var p1) && p1 >= 1)
        {
            page = p1;
            cleaned.RemoveAt(i); // remove --page
            cleaned.RemoveAt(i); // remove N (now at same index)
            break;
        }
        // page:N
        if (cleaned[i].StartsWith("page:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(cleaned[i][5..], out var p2) && p2 >= 1)
        {
            page = p2;
            cleaned.RemoveAt(i);
            break;
        }
        // -p N
        if (cleaned[i] == "-p" && i + 1 < cleaned.Count
            && int.TryParse(cleaned[i + 1], out var p3) && p3 >= 1)
        {
            page = p3;
            cleaned.RemoveAt(i);
            cleaned.RemoveAt(i);
            break;
        }
    }

    return (cleaned.ToArray(), page);
}
```

Note: `Flush` sends via `GetMultiplayer()` directly (not `SendPrivateMessage`) to avoid re-entering the scope.

**Concurrency invariant.** `ChatResponseContext.Current` is a single static field. Chat commands run on the game thread (one at a time), and external chat ingress via `mod/JunimoServer/Services/Api/ApiService.cs:1903` (`OnExternalChatMessage` → `RunOnGameThreadAsync`) marshals onto the same thread before reaching `ChatCommands.OnChatMessage`. External-API chat replies use `SendExternalMessage` → `SendPublicMessage` (not the paginated `SendPrivateMessage` path), so they never enter the scope. The static is therefore single-writer by construction; no lock needed.

### `tests/test-client/ModEntry.cs`

Remove the `maxMessages = 20` workaround at line 1016 (block 1011-1018, in `OnSaveLoaded`). The game's default of 10 is now sufficient.

### `tests/JunimoServer.Tests/PasswordProtectionTests.cs`

Line 75 (`helpResponseKeywords`, in `Help_Command_WorksInLobby`): change from `{ "!login" }` to `{ "!help" }` (or another command on page 1 of `!help` output). The `!login` command is registered ~15th and lands on page 2 with 8-line pagination.

## What This Does NOT Change

- **No command files modified** — LobbyCommands.cs, ServerCommand.cs, etc. all untouched
- **Non-command `SendPrivateMessage` calls** (e.g., PasswordProtectionService auth prompts) — unaffected, scope is null outside command dispatch
- **`SendPublicMessage`** — unaffected
- **Console commands** — unaffected, different output path (`_monitor.Log`)

## Message Budget (verified line counts)

| Command | Lines | Pages | Buffer messages used |
|---------|-------|-------|---------------------|
| `!lobby help` | 17 | 3 | 8 lines + footer = 9 |
| `!info` | 10 | 2 | 8 lines + footer = 9 |
| `!help` | 17 | 3 | 8 lines + footer = 9 |
| `!lobby info` | 8 | 1 | 8 (no footer) |
| `!lobby list` (5 layouts) | 6 | 1 | 6 (no footer) |
| `!lobby create` | 4 | 1 | 4 (no footer) |
| Short commands | 1-2 | 1 | 1-2 (no footer) |

All stay within the 10-message buffer. Maximum is 9 (8 lines + 1 footer).

## Test Cleanup

### Tests that work without changes

**LobbyCommandsPermissionsTests.Help_ShowsAllCommands** (`tests/JunimoServer.Tests/LobbyCommandsPermissionsTests.cs:55-66`) — sends `!lobby help` (17 lines), checks for:
- `"Lobby Commands"` → line 1 (page 1) ✓
- `"!lobby create"` → line 2 (page 1) ✓
- `"!lobby save"` → line 4 (page 1) ✓
- `"!lobby list"` → line 8 (page 1) ✓

All on page 1. ✓

**PasswordProtectionTests.UnauthenticatedPlayer_RegularChat_IsBlocked** — waits for `"authenticate"` and `"!login"` in a single message. The response is `"Please authenticate first: !login <password>"` sent directly by `PasswordProtectionService` (`mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:347`), NOT via a chat command. Not paginated. ✓

**PasswordProtectionTests.NewPlayer_InLobby_HasCorrectStateAndWelcome** — waits for `WelcomeKeywords = { "PASSWORD", "!login" }` (any match). The welcome message `"This server is password protected."` contains "PASSWORD" (case-insensitive). Not paginated. ✓

**All other LobbyCommands tests** — check for short responses (1-4 lines) from lobby subcommands. All fit in one page. ✓ (`LobbyCommandsTests.cs` was split into `LobbyCommandsCRUDTests.cs`, `LobbyCommandsEditingTests.cs`, `LobbyCommandsPermissionsTests.cs`, and `LobbyCommandsTestBase.cs` under `tests/JunimoServer.Tests/`.)

### Tests that NEED changes

**PasswordProtectionTests.Help_Command_WorksInLobby** (`tests/JunimoServer.Tests/PasswordProtectionTests.cs:75`) — sends `!help` and waits for `"!login"` in the response. The `!help` command lists all 17 registered commands. `!login` is registered ~15th. With 8-line pagination, it's on **page 2**. The test would not find it on page 1.

**Fix**: Change `helpResponseKeywords` to check for a command that appears on page 1 (e.g., `"!help"` itself which is command #1, or `"!cabin"` which is #2). The test's actual purpose is to verify that `!help` works for unauthenticated players — the specific keyword doesn't matter much.

```csharp
// Before:
string[] helpResponseKeywords = { "!login" };
// After: check for a command that's on page 1 of !help output
string[] helpResponseKeywords = { "!help" };
```

### Remove historySize parameter from SendAndWaitForResponseAsync

With the buffer fixed at 10, the `historySize` parameter is pointless — you always get at most 10 messages. No caller passes it explicitly. Remove it and hardcode 10 internally.

**File**: `tests/JunimoServer.Tests/Helpers/GameTestClient.cs:567-593` — `SendAndWaitForResponseAsync` (default `historySize = 20` at `:572`). Remove the parameter, use `10` directly in `GetHistory()` calls inside the method.

Also re-check the public `GetHistory`/`GetChatHistory` defaults already in `GameTestClient.cs:510` and `:790` (both default `count = 10` already) — no change needed.

### Remove explicit counts from all GetChatHistory/GetHistory calls

All calls should use the default (which is already `count = 10`). Remove the explicit argument.

| File | Line | Current | Change to |
|------|------|---------|-----------|
| `NoPasswordTests.cs` | 64 | `GetChatHistory(20)` | `GetChatHistory()` |
| `NoPasswordTests.cs` | 73 | `GetChatHistory(20)` | `GetChatHistory()` |
| `NoPasswordTests.cs` | 100 | `GetChatHistory(10)` | `GetChatHistory()` |
| `NoPasswordTests.cs` | 112 | `GetChatHistory(10)` | `GetChatHistory()` |
| `ServerApiTests.cs` | 324 | `GetChatHistory(20)` | `GetChatHistory()` |
| `LobbyCommandsEditingTests.cs` | 75 | `GetChatHistory(20)` | `GetChatHistory()` |
| `LobbyCommandsEditingTests.cs` | 266 | `GetChatHistory(20)` | `GetChatHistory()` |
| `PasswordProtectionTests.cs` | 49 | `GetHistory(20)` | `GetHistory()` |
| `PasswordProtectionTests.cs` | 81 | `GetHistory(20)` | `GetHistory()` |
| `PasswordProtectionTests.cs` | 115 | `GetHistory(20)` | `GetHistory()` |
| `PasswordProtectionDisruptiveTests.cs` | 143 | `GetHistory(20)` | `GetHistory()` |
| `Helpers/ChatTestHelper.cs` | 31 | `GetHistory(20)` | `GetHistory()` |
| `Helpers/ChatTestHelper.cs` | 46 | `GetHistory(20)` | `GetHistory()` |
| `Helpers/ChatTestHelper.cs` | 63 | `GetHistory(20)` | `GetHistory()` |

(Other LobbyCommands tests now use `Chat.SendAndWaitAsync` / `Chat.AssertResponseAsync` instead of explicit `GetChatHistory`, so no entries are needed for the split files beyond the two above.)

### Stale comment

The `tests/test-client/ModEntry.cs:1017` comment "default is 10, which truncates long command outputs" becomes obsolete once pagination lands. Remove that comment along with the `maxMessages = 20` line.

## Verification

1. `dotnet build mod/JunimoServer/JunimoServer.csproj` — mod builds
2. `dotnet build tests/test-client/JunimoTestClient.csproj` — test-client builds
3. `make test FILTER=LobbyCommandsPermissionsTests` — verify `Help_ShowsAllCommands` still passes with page 1 content
4. `make test FILTER=NoPasswordTests` — basic chat still works
5. `make test FILTER=PasswordProtectionTests` — non-command SendPrivateMessage unaffected
