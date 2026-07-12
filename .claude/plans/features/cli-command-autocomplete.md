# Plan: TAB auto-complete for the attach-cli command input

## Context

The attach-cli (a tmux pane that pipes typed lines into SMAPI's stdin via the `/tmp/smapi-input`
FIFO) offers no completion — operators must remember exact command names, subcommands, and flags.
We want **TAB auto-complete** at the `server>` prompt with **minimal manual maintenance** and a
clean, resilient architecture that behaves the way people expect from a CLI.

**Key architectural decision (revised after review): file drop, not HTTP.** The mod and the
attach-cli run in the **same container** and share `/tmp`, and there is already an established
pattern of the mod writing a catalog file there for the bash layer to read:
`InviteCodeFile.Write` → `/tmp/invite-code.txt` (`mod/JunimoServer/Util/InviteCodeFile.cs:13,50`),
read by `attach-cli`'s statusbar. We reuse that pattern: the mod writes the command catalog to
`/tmp/server-commands` at startup; the bash completion reads that local file directly. This removes
an entire failure class versus an HTTP endpoint — **no curl, no `--max-time`, no auth header, no
"API not up yet" race, no new DTOs, no test API-client changes**. The bash side is instant and
offline.

The catalog content:
- **Our commands** (8 total, all under our control — every `helper.ConsoleCommands.Add` is ours):
  each declares a `CommandDescriptor` **once** (name + subcommands + per-subcommand flags). This is
  the authoritative source for argument-level completion.
- **Other commands** (SMAPI built-ins like `help`/`harmony_summary`, plus any third-party mod's
  commands): enumerated **best-effort** by reflection over SMAPI's internal `CommandManager.GetAll()`
  for **names only** (SMAPI exposes no arg grammar for them). Defensive; failure ⇒ our commands
  still complete fully.

### Decisions taken (from user)
1. Completion offers **all** console commands at the name level (ours + SMAPI + other mods).
2. Chat (`!`) commands are **excluded** (different transport; no-ops on stdin).
3. **Name + per-argument** completion for our commands, from a **declarative descriptor** (each
   command adopts it), not a parallel hand-maintained list.

---

## Architecture (end-to-end)

```
Our 8 commands: each Register() declares a CommandDescriptor (name, subcommands, flags) ONCE
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
`startapp.sh`); the file-drop pattern is established (`InviteCodeFile.cs:13,50`); all our console
commands register at one synchronous point (`ModEntry.RegisterConsoleCommands` line 291, plus
`invitecode`/`info` via `RegisterChatCommands` line 305); SMAPI has **no** public command
enumeration (`ICommandHelper` = `Add` only; `IModRegistry` can't list commands), so reflection over
internal `CommandManager.GetAll()` is the only way to also surface built-ins/other mods — the
SMAPI-internals reflection idiom is established (`ServerOptimizer.cs:159-161`, `SmapiLogConfig.cs:25`);
`jq` is **NOT** installed in either image (parse the file with bash, no JSON dep needed); the input
shell is `bash --rcfile <loop> -i` so completion specs in a sourced script apply to `read -r -e`.

### File format — plain, not JSON
No `jq` in the images, and we control both writer and reader, so use a trivial line format that
bash parses with `case`/`grep` and no dependency:
```
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
(Tabs separate fields; first token of each line is the command; a line with one word after the
command is a subcommand; trailing tokens are that subcommand's flags. Names-only commands have just
the header line.)

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
its existing `Register(...)`, **right beside** the `helper.ConsoleCommands.Add(...)` call, so the
descriptor can't be forgotten. The 8 commands and their grammar:
- `settings` → `show`, `newgame` (`--confirm`), `validate`, `verbose` (free-form `on|off`)
- `saves` → `info` (free-form `<name>`), `import` (`--swap-host-to`, `--reload`, `--force-reload`),
  `reload` (`--force`)
- `cabins` → `add`
- `rendering` → `status` (+ free-form `<fps>`)
- `invitecode`, `info`, `host-auto`, `host-visibility` → no subcommands (header line only)

**Drift control (honest):** the descriptor is metadata; it does not drive the `switch (args[0])`
dispatch, so the two *can* drift if a future edit adds a `case` without a descriptor entry. There is
no existing test harness for these commands. Mitigation: drive each command's `ShowHelp()` **from**
its descriptor (replacing today's hand-written help) so help + completion share one source, and add
a single unit-style assertion (see Verification D) that each command's descriptor subcommand set
matches its expected switch cases. This is "drift is caught," not the overclaim "drift is
impossible."

### 2. SMAPI name enumeration (new) — `mod/JunimoServer/Util/SmapiCommandCatalog.cs`
Best-effort reflection following `SmapiLogConfig.cs` (try/catch, null-check each hop, log Warn +
return empty on any failure). Scan `AppDomain` for `StardewModdingAPI.Framework.SCore` → static
`Instance` → `CommandManager` (try field then property) → `GetAll()` → each `Command.Name`,
`.Documentation`, `.Mod` (null ⇒ `smapi`, else `DisplayName`). Returns names + source only. Reads a
static singleton + list — no `Game1` access, safe off the game thread. **Failure is non-fatal:** the
catalog file still gets our commands.

### 3. Catalog file writer (new) — `mod/JunimoServer/Util/CommandCatalogFile.cs`
Mirrors `InviteCodeFile` exactly (`FilePath = "/tmp/server-commands"`, `File.WriteAllText`, Monitor
logging, try/catch so a write failure never crashes the mod). `Write()` merges
`CommandDescriptorRegistry` (ours, with subcommands/flags) + `SmapiCommandCatalog.GetAll()` (others,
names only; skip any name already covered by a descriptor) into the plain line format above, then
writes atomically (write `.tmp`, `File.Move` over the target — same safety as the bash side uses).

### 4. Write trigger — `ModEntry.cs`
Call `CommandCatalogFile.Write(Monitor)` **once** immediately after both `RegisterConsoleCommands()`
and `RegisterChatCommands()` have run (so `invitecode`/`info`, registered in the chat path, are
included). All registration is synchronous, so the catalog is complete at that point. No HTTP, no
endpoint, no DTOs, no `ApiService` change.

---

## Bash side (both rcfiles, one authored body)

One shared script sourced by both loops keeps modern + base in sync (the rootfs trees are separate
so the file is copied into each):
- **New:** `docker/modern/rootfs/opt/bin/server-completion.sh`,
  `docker/rootfs/opt/base/bin/server-completion.sh`
- Each `server-command-loop` adds one line after its history setup, before `while true`
  (modern: after line 11; base: after line 10):
  `source "$(dirname "$0")/server-completion.sh" 2>/dev/null || true`

### Completion mechanism — decided, with a hard pre-commit gate
- **`complete -F` (preferred):** native readline experience users expect — longest-common-prefix
  fill, double-TAB listing, trailing space after a unique match — no custom buffer surgery. Open
  question: whether `complete -D/-E` specs are consulted by `read -e` (verified to *register*
  cleanly locally; TAB invocation only confirmable in a real TTY).
- **`bind -x '"\t": _fn'` (fallback):** deterministic documented way to run a function on TAB inside
  `read -e` (verified locally: binding accepted, `READLINE_LINE`/`READLINE_POINT` are the right
  vars). Costs hand-rolled prefix-fill/listing, so it's the fallback.

**Decision:** put all catalog/parse/candidate logic in one mechanism-agnostic function
(`_collect_candidates idx words…` → candidates). Wire `complete -F` to it. **Hard gate (Verification
B′):** confirm TAB fires in a real container TTY on **both** images; if `complete -F` doesn't fire
under `read -e`, switch the binding to `bind -x` calling the *same* function — no logic rewrite. Not
"done" until TAB works in a real attach-cli.

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
# Readline settings tuned for the constrained 2-line pane (see tmux section below):
bind 'set completion-ignore-case on'  2>/dev/null || true  # dispatch is case-insensitive (Set<TAB>→settings)
bind 'set show-all-if-ambiguous on'   2>/dev/null || true  # first TAB lists candidates (no dead first press)
bind 'set page-completions off'       2>/dev/null || true  # NEVER invoke the --More-- pager (unusable in 2 lines)
bind 'set completion-query-items 200' 2>/dev/null || true  # never ask "display all N?"
complete -F _server_complete -E
complete -F _server_complete -D
```
The completion reads `/tmp/server-commands` (written by the mod at startup). If the file is absent
(mod still booting), candidates are empty — TAB simply does nothing, the prompt is never blocked,
and the next shell init picks up the file once written. No fetch, no timeout, no network.

### Idiomatic behaviors the implementation must honor ("no weird quirks")
- **Case-insensitive** matching (`completion-ignore-case on`) — `Set`<TAB> → `settings`.
- **First TAB shows candidates** (`show-all-if-ambiguous on`) — no dead first keypress.
- **No `--More--` pager** (`page-completions off`) — paging is unusable in a 2-line pane.
- **Trailing space** after a unique match (readline default; preserve).
- **No duplicate flags** — `flags_of` drops flags already on the line.
- **`cli` pseudo-commands** complete at word 0, and `cli `<TAB> → `exit quit detach clear`.
- **Free-form args** (`<fps>`, `<name>`, `<id>`) yield no candidates — silent where it can't help.
- **Password mode** → empty candidates (belt-and-suspenders; `read_password` bypasses readline).

---

## tmux split-pane integration (the part that makes or breaks this)

The attach-cli is a tmux session: **top pane** = read-only log tail, **bottom pane** (index `0.1`,
locked to **2 lines**) = the `read -r -e` input loop. Tracing the composition surfaced **two
blockers that would otherwise make completion silently never work**, plus the constrained-height
concern. All are handled below; none were in the first draft.

### Blocker 1 — TAB is captured by tmux before readline sees it (CRITICAL)
`init_keybinds` binds TAB globally: `tmux bind-key -n Tab select-pane -t 0.1` (`attach-cli`
modern:60 / base:76). `-n` = no-prefix/root table, so **every** TAB — including in the input pane —
runs `select-pane` and never reaches bash. Any `complete`/`bind -x` spec would be dead.

**Fix (idiomatic to this file):** make the TAB binding **pane-conditional**, mirroring the existing
mouse-wheel pattern in `init_mouse`, which already does
`if-shell -F "#{==:#{pane_index},0}" "<output-pane action>" "send-keys -M"`:
```bash
# When focus is on the OUTPUT pane (index 0): TAB jumps to the input pane (unchanged affordance).
# When already on the INPUT pane (index 1): pass TAB through to the shell so readline completes.
tmux bind-key -n Tab if-shell -F "#{==:#{pane_index},0}" \
    "select-pane -t 0.1" "send-keys -t 0.1 Tab" 2>/dev/null || true
```
Replaces the unconditional bind in `init_keybinds` (both files). Keeps the "TAB to focus input"
behavior from the log pane while letting TAB do completion once you're typing. This is the single
change that makes the whole feature reachable.

### Blocker 2 — input pane is hard-locked to 2 lines (display constraint)
`split-window -l 2` plus two re-pinning hooks (`client-resized`/`after-resize-window` →
`resize-pane -t 0.1 -y 2`, `init_resizing`, modern:95-96 / base:125-126) keep the pane at 2 lines
forever. Readline's vertical candidate menu + `--More--` pager don't fit.

**Fix (no layout change needed):** the readline settings above make the candidate display behave in
2 lines — `page-completions off` removes the pager entirely; `show-all-if-ambiguous on` lists on the
first TAB; the pane is **full terminal width**, so the common cases (4 subcommands, ≤3 flags, the
`cli` verbs) render on one wrapped line within the 2 visible lines. The only cramped case is the
~12-15-entry top-level *name* list; it wraps across the width and the bottom 2 lines show the tail —
acceptable, and typing one more letter narrows it immediately. **No change to the pane-locking hooks
or `-l 2`.**

> **Optional enhancement (out of scope for v1, noted not hidden):** a `bind -x` TAB handler could
> momentarily `tmux resize-pane -y 8`, show candidates, then restore — nicer for the long name list
> but it fights the resize hooks and adds real complexity. Deferred; the readline-settings fix is
> the simple robust baseline.

### Confirmed non-issues (traced, so they're not surprises later)
- **rcfile = the session:** `server-command-loop` is the `--rcfile` *and* contains the forever
  `while` loop; it never returns to an interactive prompt. So all `complete`/`bind`/`set` setup
  (our sourced `server-completion.sh`) **must run before `while true`** — the plan sources it right
  there. ✓
- **`clear` per iteration** (loop startup + after each command) redraws the terminal but does not
  corrupt readline's line buffer/undo state — completion is unaffected. ✓
- **`stty -echoctl`** only hides `^C` display; does not touch TAB/`\t` input. ✓
- **No `synchronize-panes`** — TAB/keys go only to the focused input pane, never mirrored to the log
  pane. ✓
- **`TERM`** is tmux's default (`screen`/`tmux-256color`); readline completion works under it. No
  override needed. ✓
- **Top pane** is `stty -echo` + `tail -f` (pure output) — completion only ever matters in `0.1`. ✓

### tmux-side files to edit (both)
- `docker/modern/rootfs/opt/bin/attach-cli` (`init_keybinds`, ~line 60) and
  `docker/rootfs/opt/base/bin/attach-cli` (~line 76): replace the unconditional TAB bind with the
  pane-conditional `if-shell` form above.

---

## Files to create / modify
- **New:** `mod/JunimoServer/Services/Commands/CommandDescriptor.cs` (+ `CommandDescriptorRegistry`)
- **New:** `mod/JunimoServer/Util/SmapiCommandCatalog.cs`
- **New:** `mod/JunimoServer/Util/CommandCatalogFile.cs` (mirrors `InviteCodeFile`)
- **New:** `docker/modern/rootfs/opt/bin/server-completion.sh`,
  `docker/rootfs/opt/base/bin/server-completion.sh` (one authored body)
- **Edit:** `mod/JunimoServer/ModEntry.cs` (one `CommandCatalogFile.Write(Monitor)` call after
  registration, ~after line 338-era call site)
- **Edit (adopt descriptor + descriptor-driven help):** `SettingsCommand.cs`, `SavesCommand.cs`,
  `CabinsConsoleCommand.cs`, `RenderingCommand.cs`, `InviteCodeCommand.cs`, `ServerCommand.cs`,
  `AlwaysOnServer/AlwaysOn.cs`
- **Edit (source the shared script):** `docker/modern/rootfs/opt/bin/server-command-loop`,
  `docker/rootfs/opt/base/bin/server-command-loop`
- **Edit (TAB pane-conditional bind — REQUIRED, else completion is unreachable):**
  `docker/modern/rootfs/opt/bin/attach-cli` and `docker/rootfs/opt/base/bin/attach-cli`
  (`init_keybinds`): replace `bind-key -n Tab select-pane -t 0.1` with the `if-shell` form.
- **Edit (Dockerfiles, if rootfs COPY is selective):** ensure both `server-completion.sh` land in
  their images and are executable — per `verify-edit-landed-in-artifact.md`, inspect the built
  image (`docker create` + `docker cp`), don't trust a green build.

**No** `ApiService.cs` change, **no** new HTTP endpoint, **no** test API-client change.

---

## Verification

**A. Catalog file (automated/inspectable):** after a server boots, assert `/tmp/server-commands`
exists and contains: a SMAPI built-in (`help`), a verified mod command (`info`), and `settings` with
its four subcommands + `newgame --confirm`. Because there's no HTTP surface, exercise this by
inspecting the file inside a running container (`docker compose exec … cat /tmp/server-commands`) or
in an E2E fixture that reads it. This is where the reflection field-vs-property risk and the
descriptor merge are exercised.

**B. Bash smoke (manual, both images):** `docker compose exec` the attach-cli. `cat
/tmp/server-commands` → confirm format. In the input pane: `he`<TAB>→`help`; `Set`<TAB>→`settings`
(case-insensitive); `settings `<TAB>→`show newgame validate verbose`; `settings newgame `<TAB>→
`--confirm`; `saves import x `<TAB>→`--swap-host-to --reload --force-reload`;
`saves import x --reload `<TAB> → does NOT re-offer `--reload`; <TAB><TAB> on empty lists all;
`cli `<TAB>→`exit quit detach clear`; no filename completion at word 0. Fresh attach-cli before the
file is written → prompt immediate, TAB silent. Password mode → TAB offers nothing.

**B′. TAB reaches the shell (the make-or-break tmux check):** in a real attach-cli, with focus on
the **input** pane, confirm TAB now triggers completion (not `select-pane`); with focus on the
**output** pane, confirm TAB still jumps to the input pane (affordance preserved). Then confirm
`complete -F` fires for `read -e`; if not, switch to the `bind -x` fallback (same
`_collect_candidates`) and re-verify. Also eyeball the 2-line candidate display: no `--More--`
pager, candidate list readable. Repeat on **both** images. Not done until TAB completes in a real
attach-cli.

**C. Image-artifact check:** `docker create` + `docker cp` both `server-completion.sh` out of the
built images to confirm they landed and are sourced.

**D. Descriptor/dispatch parity (automated):** a small assertion that each command's
`CommandDescriptor.Subcommands` matches its `switch (args[0])` cases (or, minimally, that
descriptor-driven `ShowHelp()` lists exactly what dispatch accepts). Catches the descriptor↔switch
drift that the metadata design otherwise allows. Run via the **run-tests** skill.

---

## Why this is cleaner than an HTTP endpoint (review outcome)

The first draft exposed the catalog via a new `GET /commands` endpoint. Re-examined under "is this
the best plan?", the file-drop design is strictly simpler and more resilient and was adopted:

| Concern | HTTP endpoint (rejected) | File drop (chosen) |
|---|---|---|
| Bash dependency | `curl` + `--max-time` + retry | `cat` a local file |
| "API not up yet" race | real failure class to handle | absent file ⇒ TAB silent, self-heals next init |
| Auth | must thread `API_KEY` / `Authorization` | none (local file) |
| New surface | DTOs, route, handler, swagger, test API client | one writer file (mirrors `InviteCodeFile`) |
| Latency | network round-trip per shell init | instant, offline |
| Precedent | none | `InviteCodeFile` → `/tmp/invite-code.txt` |

**The decisive finding was the tmux composition** (the second review pass). Tracing the split-pane
CLI surfaced that `tmux bind-key -n Tab select-pane` **captures TAB before bash ever sees it** —
without the pane-conditional `if-shell` fix, every completion mechanism in this plan would silently
do nothing. The first draft missed this entirely. The 2-line pane height (locked by `-l 2` + resize
hooks) is the second tmux constraint, handled by readline settings (`page-completions off`,
`show-all-if-ambiguous on`) rather than fighting the layout. Both fixes reuse idioms already in
`attach-cli` (the `if-shell` pane test from `init_mouse`).

Other review outcomes folded in: completion mechanism is decided with a deterministic fallback and a
real pre-commit TTY gate (not a deferred gamble); idiomatic behaviors (case-insensitivity, first-TAB
listing, no pager, trailing space, no-duplicate-flags, `cli` pseudo-commands, silent free-form args)
are specified; descriptor↔dispatch drift is honestly mitigated by a parity test rather than an
overclaim; reflection is best-effort and non-fatal; the two image rootfs trees share one authored
completion body.

Honest residual risks (not hidden): (1) the `complete -F` vs `bind -x` TTY behavior is the one thing
unverifiable from the dev box — gated by B′; (2) descriptor↔switch drift is *caught*, not
*structurally impossible* — accepted as the right cost/benefit given there's no dispatch-from-data
refactor in scope; (3) the top-level name list (~12-15 entries) is slightly cramped in 2 lines —
acceptable, with the optional pane-grow enhancement noted but deferred.
