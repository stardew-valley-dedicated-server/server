# Plan: TAB auto-complete for the attach-cli command input

## Goal

Add **TAB auto-complete** to the `server>` prompt of the attach-cli (a tmux pane that pipes typed
lines into SMAPI's stdin via the `/tmp/smapi-input` FIFO), with minimal manual maintenance.

- Completion offers **all** console commands at the name level (ours + SMAPI + other mods).
- Chat (`!`) commands are **excluded** (different transport; no-ops on stdin).
- **Name + per-argument** completion for our commands, driven by a **declarative descriptor** each
  command adopts — not a parallel hand-maintained list.

## Approach: file drop, not HTTP

The mod and the attach-cli run in the **same container** and share `/tmp`. The mod writes the
command catalog to `/tmp/server-commands` at startup; the bash completion reads that local file
directly. This mirrors the established `InviteCodeFile.Write` → `/tmp/invite-code.txt` pattern
(`InviteCodeFile.cs`), read by the attach-cli statusbar. The bash side is instant and offline: no
`curl`, no `--max-time`, no auth header, no "API not up yet" race, no new DTOs.

The catalog content:
- **Our commands** (all under our control — every `helper.ConsoleCommands.Add` is ours): each
  declares a `CommandDescriptor` once (name + subcommands + per-subcommand flags). This is the
  authoritative source for argument-level completion.
- **Other commands** (SMAPI built-ins like `help`/`harmony_summary`, plus any third-party mod's
  commands): enumerated best-effort by reflection over SMAPI's internal `CommandManager.GetAll()`
  for names only (SMAPI exposes no arg grammar for them). Failure ⇒ our commands still complete
  fully.

```text
Our commands: each Register() declares a CommandDescriptor (name, subcommands, flags) ONCE
        +
SMAPI CommandManager.GetAll()  (reflection, best-effort: names of built-ins + other mods)
        ▼
At startup (after RegisterConsoleCommands + RegisterChatCommands run):
  CommandCatalogFile.Write("/tmp/server-commands")   ← same pattern as InviteCodeFile.Write
        ▼  (plain local file in shared /tmp — no network)
server-completion.sh (sourced by server-command-loop): read file → candidates by word position
        ▼
read -r -e -p "server> "   →  TAB completes name, then subcommands, then flags
```

Grounding facts (verified): mod + attach-cli share `/tmp` in one container (`docker-compose.yml`,
`startapp.sh`); the file-drop pattern is established (`InviteCodeFile.cs`); all our console commands
register at one synchronous point (`ModEntry.RegisterConsoleCommands`, plus `invitecode`/`info` via
`RegisterChatCommands`); SMAPI has no public command enumeration (`ICommandHelper` = `Add` only), so
reflection over internal `CommandManager.GetAll()` is the only way to also surface built-ins/other
mods — the SMAPI-internals reflection idiom is established (`ServerOptimizer.cs`,
`SmapiLogConfig.cs`); `jq` is NOT installed in either image (parse the file with bash); the input
shell is `bash --rcfile <loop> -i` so completion specs in a sourced script apply to `read -r -e`.

### File format — plain, not JSON

No `jq` in the images, and we control both writer and reader, so use a trivial line format bash
parses with `case`/`grep`:
```text
# <command>\t<source>            (source: "ours" | "smapi" | mod display name)
# <command> <sub>\t<flag> <flag> (one line per subcommand of our commands)
settings	ours
settings show
settings newgame	--confirm
settings validate
settings verbose
saves	ours
saves info
saves import	--swap-host-to --reload --force-reload
saves reload	--force
help	smapi
harmony_summary	smapi
...
```
Tabs separate fields; the first token of each line is the command; a line with one word after the
command is a subcommand; trailing tokens are that subcommand's flags. Names-only commands have just
the header line.

---

## Server side

### 1. Declarative command descriptor (new) — `mod/JunimoServer/Services/Commands/CommandDescriptor.cs`
```csharp
public sealed class CommandDescriptor
{ public string Name; public string Description; public List<SubcommandDescriptor> Subcommands = new(); }

public sealed class SubcommandDescriptor
{ public string Name; public string Description; public List<string> Flags = new(); }
```
A static `CommandDescriptorRegistry.Add(descriptor)` collects them. Each of our commands calls it in
its existing `Register(...)`, right beside the `helper.ConsoleCommands.Add(...)` call, so the
descriptor can't be forgotten. The commands and their grammar:
- `settings` → `show`, `newgame` (`--confirm`), `validate`, `verbose` (free-form `on|off`)
- `saves` → `info` (free-form `<name>`), `import` (`--swap-host-to`, `--reload`, `--force-reload`),
  `reload` (`--force`)
- `cabins` → `add`
- `rendering` → `status` (+ free-form `<fps>`)
- `invitecode`, `info`, `host-auto`, `host-visibility` → no subcommands (header line only)

**Drift control:** the descriptor is metadata; it does not drive the `switch (args[0])` dispatch, so
the two *can* drift if a future edit adds a `case` without a descriptor entry. Mitigation: drive each
command's `ShowHelp()` **from** its descriptor (replacing today's hand-written help) so help +
completion share one source, and add a parity assertion (Verification D) that each command's
descriptor subcommand set matches its switch cases. Drift is *caught*, not structurally impossible.

### 2. SMAPI name enumeration (new) — `mod/JunimoServer/Util/SmapiCommandCatalog.cs`
Best-effort reflection following `SmapiLogConfig.cs` (try/catch, null-check each hop, log Warn +
return empty on any failure). Scan `AppDomain` for `StardewModdingAPI.Framework.SCore` → static
`Instance` → `CommandManager` (try field then property) → `GetAll()` → each `Command.Name`,
`.Documentation`, `.Mod` (null ⇒ `smapi`, else `DisplayName`). Returns names + source only. Reads a
static singleton + list — no `Game1` access, safe off the game thread. Failure is non-fatal: the
catalog file still gets our commands.

### 3. Catalog file writer (new) — `mod/JunimoServer/Util/CommandCatalogFile.cs`
Mirrors `InviteCodeFile` (`FilePath = "/tmp/server-commands"`, Monitor logging, try/catch so a write
failure never crashes the mod). `Write()` merges `CommandDescriptorRegistry` (ours, with
subcommands/flags) + `SmapiCommandCatalog.GetAll()` (others, names only; skip any name already
covered by a descriptor) into the plain line format above, then writes atomically (write `.tmp`,
`File.Move` over the target).

### 4. Write trigger — `ModEntry.cs`
Call `CommandCatalogFile.Write(Monitor)` once immediately after both `RegisterConsoleCommands()` and
`RegisterChatCommands()` have run (so `invitecode`/`info`, registered in the chat path, are
included). All registration is synchronous, so the catalog is complete at that point.

---

## Bash side (both rcfiles, one authored body)

One shared script sourced by both loops keeps modern + base in sync (the rootfs trees are separate,
so the file is copied into each):
- **New:** `docker/modern/rootfs/opt/bin/server-completion.sh`,
  `docker/rootfs/opt/base/bin/server-completion.sh`
- Each `server-command-loop` adds one line after its history setup, before `while true`:
  `source "$(dirname "$0")/server-completion.sh" 2>/dev/null || true`

### Completion mechanism
Put all catalog/parse/candidate logic in one mechanism-agnostic function (`_collect_candidates idx
words…` → candidates). Wire `complete -F` to it (native readline experience: longest-common-prefix
fill, double-TAB listing, trailing space after a unique match). **Hard gate (Verification B′):**
confirm TAB fires in a real container TTY on both images; if `complete -F` doesn't fire under `read
-e`, switch the binding to `bind -x` calling the *same* function — no logic rewrite. (`bind -x` is
the verified-deterministic way to run a function on TAB inside `read -e`, using
`READLINE_LINE`/`READLINE_POINT`.) Not "done" until TAB works in a real attach-cli.

### `server-completion.sh` shape
```bash
# Sourced by server-command-loop. Completes the server> prompt from /tmp/server-commands.
CATALOG="/tmp/server-commands"

# Parse helpers read CATALOG (plain tab format, no jq):
#   command_names            -> first token of every header line
#   subcommands_of <cmd>     -> sub names for one command (empty for smapi/other-mod cmds)
#   flags_of <cmd> <sub>     -> flags for one subcommand, minus any already on the line

_collect_candidates() {           # idx + already-split words -> newline candidates
    local idx="$1"; shift; local -a w=("$@")
    [ -f "$PASSWORD_MODE_FILE" ] && return 0          # never during password entry
    case "$idx" in
        0) command_names; printf '%s\n' cli ;;        # names + `cli` pseudo
        1) if [ "${w[0]}" = cli ]; then printf '%s\n' exit quit detach clear
           else subcommands_of "${w[0]}"; fi ;;
        *) flags_of "${w[0]}" "${w[1]}" ;;
    esac
}

_server_complete() {              # complete -F wiring (primary)
    COMPREPLY=($(compgen -W "$(_collect_candidates "$COMP_CWORD" "${COMP_WORDS[@]}")" \
                 -- "${COMP_WORDS[COMP_CWORD]}"))
    return 0
}
# Readline settings tuned for the constrained 2-line pane (see tmux section):
bind 'set completion-ignore-case on'  2>/dev/null || true  # dispatch is case-insensitive (Set<TAB>→settings)
bind 'set show-all-if-ambiguous on'   2>/dev/null || true  # first TAB lists candidates (no dead first press)
bind 'set page-completions off'       2>/dev/null || true  # NEVER invoke the --More-- pager (unusable in 2 lines)
bind 'set completion-query-items 200' 2>/dev/null || true  # never ask "display all N?"
complete -F _server_complete -E
complete -F _server_complete -D
```
If the file is absent (mod still booting), candidates are empty — TAB does nothing, the prompt is
never blocked, and the next shell init picks up the file once written.

### Idiomatic behaviors the implementation must honor
- **Case-insensitive** matching (`completion-ignore-case on`) — `Set`<TAB> → `settings`.
- **First TAB shows candidates** (`show-all-if-ambiguous on`) — no dead first keypress.
- **No `--More--` pager** (`page-completions off`) — paging is unusable in a 2-line pane.
- **Trailing space** after a unique match (readline default; preserve).
- **No duplicate flags** — `flags_of` drops flags already on the line.
- **`cli` pseudo-commands** complete at word 0, and `cli `<TAB> → `exit quit detach clear`.
- **Free-form args** (`<fps>`, `<name>`, `<id>`) yield no candidates — silent where it can't help.
- **Password mode** → empty candidates (belt-and-suspenders; `read_password` bypasses readline).

---

## tmux split-pane integration

The attach-cli is a tmux session: **top pane** = read-only log tail, **bottom pane** (index `0.1`,
locked to 2 lines) = the `read -r -e` input loop. Two constraints must be handled or completion
silently never works.

### Blocker 1 — TAB is captured by tmux before readline sees it (CRITICAL)
`init_keybinds` binds TAB globally: `tmux bind-key -n Tab select-pane -t 0.1`. `-n` = no-prefix/root
table, so **every** TAB — including in the input pane — runs `select-pane` and never reaches bash.
Any `complete`/`bind -x` spec would be dead.

**Fix (idiomatic to this file):** make the TAB binding pane-conditional, mirroring the existing
mouse-wheel pattern in `init_mouse`
(`if-shell -F "#{==:#{pane_index},0}" "<output-pane action>" "send-keys -M"`):
```bash
# When focus is on the OUTPUT pane (index 0): TAB jumps to the input pane (unchanged affordance).
# When already on the INPUT pane (index 1): pass TAB through to the shell so readline completes.
tmux bind-key -n Tab if-shell -F "#{==:#{pane_index},0}" \
    "select-pane -t 0.1" "send-keys -t 0.1 Tab" 2>/dev/null || true
```
Replaces the unconditional bind in `init_keybinds` (both files). This is the single change that makes
the whole feature reachable.

### Blocker 2 — input pane is hard-locked to 2 lines (display constraint)
`split-window -l 2` plus re-pinning hooks (`client-resized`/`after-resize-window` →
`resize-pane -t 0.1 -y 2`, `init_resizing`) keep the pane at 2 lines forever. Readline's vertical
candidate menu + `--More--` pager don't fit.

**Fix (no layout change):** the readline settings above make the candidate display behave in 2 lines
— `page-completions off` removes the pager; `show-all-if-ambiguous on` lists on the first TAB; the
pane is full terminal width, so the common cases (a handful of subcommands, ≤3 flags, the `cli`
verbs) render on one wrapped line. The only cramped case is the top-level *name* list; it wraps
across the width and the bottom 2 lines show the tail — acceptable, and typing one more letter
narrows it immediately. No change to the pane-locking hooks or `-l 2`.

### Confirmed non-issues (traced)
- **rcfile = the session:** `server-command-loop` is the `--rcfile` *and* contains the forever
  `while` loop; it never returns to an interactive prompt. All `complete`/`bind`/`set` setup (our
  sourced `server-completion.sh`) must run before `while true` — the plan sources it right there. ✓
- **`clear` per iteration** redraws the terminal but does not corrupt readline's line buffer/undo
  state — completion is unaffected. ✓
- **`stty -echoctl`** only hides `^C` display; does not touch TAB/`\t` input. ✓
- **No `synchronize-panes`** — TAB/keys go only to the focused input pane. ✓
- **`TERM`** is tmux's default (`screen`/`tmux-256color`); readline completion works under it. ✓
- **Top pane** is `stty -echo` + `tail -f` (pure output) — completion only matters in `0.1`. ✓

---

## Files to create / modify
- **New:** `mod/JunimoServer/Services/Commands/CommandDescriptor.cs` (+ `CommandDescriptorRegistry`)
- **New:** `mod/JunimoServer/Util/SmapiCommandCatalog.cs`
- **New:** `mod/JunimoServer/Util/CommandCatalogFile.cs` (mirrors `InviteCodeFile`)
- **New:** `docker/modern/rootfs/opt/bin/server-completion.sh`,
  `docker/rootfs/opt/base/bin/server-completion.sh` (one authored body)
- **Edit:** `mod/JunimoServer/ModEntry.cs` (one `CommandCatalogFile.Write(Monitor)` call after
  registration)
- **Edit (adopt descriptor + descriptor-driven help):** `SettingsCommand.cs`, `SavesCommand.cs`,
  `CabinsConsoleCommand.cs`, `RenderingCommand.cs`, `InviteCodeCommand.cs`, `ServerCommand.cs`,
  `AlwaysOnServer/AlwaysOn.cs`
- **Edit (source the shared script):** `docker/modern/rootfs/opt/bin/server-command-loop`,
  `docker/rootfs/opt/base/bin/server-command-loop`
- **Edit (TAB pane-conditional bind — REQUIRED, else completion is unreachable):**
  `docker/modern/rootfs/opt/bin/attach-cli` and `docker/rootfs/opt/base/bin/attach-cli`
  (`init_keybinds`): replace `bind-key -n Tab select-pane -t 0.1` with the `if-shell` form.
- **Edit (Dockerfiles, if rootfs COPY is selective):** ensure both `server-completion.sh` land in
  their images and are executable — per `runtime-post-conditions-are-gates.md`, inspect the built
  image (`docker create` + `docker cp`), don't trust a green build.

---

## Verification

**A. Catalog file (automated/inspectable):** after a server boots, assert `/tmp/server-commands`
exists and contains a SMAPI built-in (`help`), a verified mod command (`info`), and `settings` with
its four subcommands + `newgame --confirm`. Inspect the file inside a running container
(`docker compose exec … cat /tmp/server-commands`) or in an E2E fixture that reads it. This exercises
the reflection field-vs-property risk and the descriptor merge.

**B. Bash smoke (manual, both images):** `docker compose exec` the attach-cli. `cat
/tmp/server-commands` → confirm format. In the input pane: `he`<TAB>→`help`; `Set`<TAB>→`settings`
(case-insensitive); `settings `<TAB>→`show newgame validate verbose`; `settings newgame `<TAB>→
`--confirm`; `saves import x `<TAB>→`--swap-host-to --reload --force-reload`;
`saves import x --reload `<TAB> → does NOT re-offer `--reload`; <TAB><TAB> on empty lists all;
`cli `<TAB>→`exit quit detach clear`; no filename completion at word 0. Fresh attach-cli before the
file is written → prompt immediate, TAB silent. Password mode → TAB offers nothing.

**B′. TAB reaches the shell (make-or-break tmux check):** in a real attach-cli, with focus on the
**input** pane, confirm TAB triggers completion (not `select-pane`); with focus on the **output**
pane, confirm TAB still jumps to the input pane. Then confirm `complete -F` fires for `read -e`; if
not, switch to the `bind -x` fallback (same `_collect_candidates`) and re-verify. Eyeball the 2-line
candidate display: no `--More--` pager, candidate list readable. Repeat on both images.

**C. Image-artifact check:** `docker create` + `docker cp` both `server-completion.sh` out of the
built images to confirm they landed and are sourced.

**D. Descriptor/dispatch parity (automated):** an assertion that each command's
`CommandDescriptor.Subcommands` matches its `switch (args[0])` cases (or, minimally, that
descriptor-driven `ShowHelp()` lists exactly what dispatch accepts). Catches descriptor↔switch drift.
Run via the **run-tests** skill.

## Residual risks
1. The `complete -F` vs `bind -x` TTY behavior is unverifiable from the dev box — gated by B′.
2. Descriptor↔switch drift is *caught* by the parity test, not structurally impossible — accepted,
   given no dispatch-from-data refactor is in scope.
3. The top-level name list is slightly cramped in 2 lines — acceptable; an optional `bind -x` handler
   could momentarily `resize-pane -y 8` to show candidates, but it fights the resize hooks and is
   deferred.
