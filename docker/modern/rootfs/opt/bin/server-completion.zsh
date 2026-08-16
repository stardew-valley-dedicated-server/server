# ZLE completion for the attach-cli `server>` prompt. Sourced by server-command-loop before its
# vared input loop; the Debian base and Alpine modern images carry an identical copy.
#
# Candidates come from /tmp/server-commands, written at mod startup by CommandCatalogFile.cs:
# our commands with subcommands/flags, plus SMAPI/other-mod command names. Plain tab-separated
# lines parsed with pure zsh (no jq in the images). If the file is absent (mod still booting),
# TAB and the ghost suggestion complete nothing and the prompt is never blocked.
#
# Three behaviours are wired here and armed by the loop:
#   - Ghost text: `_zsh_autosuggest_strategy_catalog` feeds the vendored zsh-autosuggestions
#     plugin, which renders the first catalog candidate as grey text at the cursor.
#   - TAB: `_server_tab_complete` (registered as the ZLE widget `server-tab-complete`) extends to
#     the longest common prefix on the first TAB and cycles candidates on further TABs.
#   - Suggestion list: `_render_suggestions` paints a column-formatted candidate list in the rows
#     below the input, growing the tmux pane to fit (capped), and `_clear_suggestions` tears it
#     back down.
#
# The widget is named `server-tab-complete` (no leading underscore) deliberately: the plugin's
# `_zsh_autosuggest_bind_widgets` ignores every widget matching `_*`, so an underscore name would
# not be wrapped and the ghost would not refresh after TAB rewrites the buffer.

CATALOG="/tmp/server-commands"
PASSWORD_MODE_FILE="${PASSWORD_MODE_FILE:-/tmp/smapi-password-mode}"

#--------------------------------------------------------------------#
# Catalog parsing (tab-separated /tmp/server-commands lines)          #
#--------------------------------------------------------------------#

# First token of every header line (lines whose first tab-field is a single word).
# `cli` is skipped: word 0 always offers the pseudo-command, so a real console command
# with that name would otherwise be listed twice.
_catalog_command_names() {
    local first rest
    [[ -r "$CATALOG" ]] || return 0
    while IFS=$'\t' read -r first rest; do
        case "$first" in
            ''|*' '*|cli) ;;
            *) print -r -- "$first" ;;
        esac
    done < "$CATALOG"
}

# Subcommand names for one command (empty for names-only smapi/other-mod commands).
_catalog_subcommands_of() {
    local cmd="${(L)1}" first rest
    [[ -r "$CATALOG" ]] || return 0
    while IFS=$'\t' read -r first rest; do
        case "${(L)first}" in
            "$cmd "*) print -r -- "${first#* }" ;;
        esac
    done < "$CATALOG"
}

# Flags of one subcommand, minus any flag already typed on the line (passed as $3...).
_catalog_flags_of() {
    local cmd="${(L)1}" sub="${(L)2}"
    shift 2
    local first flags flag word skip
    local -a flag_list
    [[ -r "$CATALOG" ]] || return 0
    while IFS=$'\t' read -r first flags; do
        if [[ "${(L)first}" == "$cmd $sub" ]]; then
            flag_list=(${=flags}) # ${=var}: split on IFS without glob expansion
            for flag in "${flag_list[@]}"; do
                skip=""
                for word in "$@"; do
                    [[ "${(L)word}" == "$flag" ]] && { skip=1; break; }
                done
                [[ -z "$skip" ]] && print -r -- "$flag"
            done
            return 0
        fi
    done < "$CATALOG"
}

# Word index (0-based position of the word being completed) + all words up to and including the
# word at the cursor -> newline-separated candidates for that position. Word 0: command names +
# the `cli` pseudo-command; word 1: subcommands (or the cli verbs); word 2+: remaining flags.
# Free-form args (a save name, an fps value) yield nothing. Arrays are 1-based in zsh, so the
# first word is w[1]. The password-mode gate starves both TAB and the ghost strategy.
_collect_candidates() {
    local idx="$1"
    shift
    local -a w=("$@")
    [[ -f "$PASSWORD_MODE_FILE" ]] && return 0 # never during password entry
    case "$idx" in
        0)
            _catalog_command_names
            print -r -- cli
            ;;
        1)
            if [[ "${(L)w[1]}" == cli ]]; then
                print -l -r -- exit quit detach clear
            else
                _catalog_subcommands_of "$w[1]"
            fi
            ;;
        *)
            # (@) keeps the slice as separate words; a bare "${w[3,-1]}" would join them into one.
            # [3,-2] drops the word being completed (always the last element), so a fully typed flag
            # isn't filtered out of its own candidate set.
            [[ "${(L)w[1]}" == cli ]] || _catalog_flags_of "$w[1]" "$w[2]" "${(@)w[3,-2]}"
            ;;
    esac
}

#--------------------------------------------------------------------#
# Shared line parsing + match collection                              #
#--------------------------------------------------------------------#

# Split the text up to the cursor into: _SC_WORDS (complete words before the cursor word), _SC_CUR
# (the word being completed, empty when the cursor sits right after whitespace), _SC_IDX (0-based
# index of that word), _SC_BASE (the text before the cursor word). Used by both the TAB widget and
# the ghost strategy so list, cycle and ghost always agree on the word split.
_server_split_head() {
    local head="$1"
    local -a words
    words=(${=head}) # split on IFS whitespace, empty fields dropped
    local cur=""
    if (( ${#words} > 0 )) && [[ "$head" != *[[:space:]] ]]; then
        cur="${words[-1]}"
        words=(${words[1,-2]})
    fi
    _SC_WORDS=("${words[@]}")
    _SC_CUR="$cur"
    _SC_IDX=${#words}
    _SC_BASE="${head%"$cur"}"
}

# Fill _SC_MATCHES with the candidates for the current split whose lowercased prefix matches the
# lowercased _SC_CUR, in catalog order (so ghost/list/cycle share one ordering).
_server_collect_matches() {
    _SC_MATCHES=()
    local cand cur_lc="${(L)_SC_CUR}"
    while IFS= read -r cand; do
        [[ -n "$cand" ]] || continue
        [[ "${(L)cand[1,${#cur_lc}]}" == "$cur_lc" ]] && _SC_MATCHES+=("$cand")
    done < <(_collect_candidates "$_SC_IDX" "${_SC_WORDS[@]}" "$_SC_CUR")
}

# Case-insensitive longest common prefix of _SC_MATCHES, keeping the first match's casing.
_server_lcp() {
    local lcp="${_SC_MATCHES[1]}" m m_lc lcp_lc
    for m in "${(@)_SC_MATCHES[2,-1]}"; do
        m_lc="${(L)m}"
        lcp_lc="${(L)lcp}"
        while [[ -n "$lcp" && "${m_lc[1,${#lcp}]}" != "$lcp_lc" ]]; do
            lcp="${lcp[1,-2]}"
            lcp_lc="${(L)lcp}"
        done
    done
    print -r -- "$lcp"
}

#--------------------------------------------------------------------#
# Ghost-text strategy for zsh-autosuggestions                         #
#--------------------------------------------------------------------#

# The plugin passes the full buffer as $1 and renders `suggestion` minus the typed prefix as grey.
# We complete the buffer's last word with the first catalog candidate; the plugin drops the
# suggestion unless it is a literal prefix-extension of the buffer (so a case-only mismatch simply
# shows no ghost, which is fine — TAB still completes it case-insensitively).
_zsh_autosuggest_strategy_catalog() {
    emulate -L zsh
    typeset -g suggestion
    unset suggestion
    local buffer="$1"
    [[ -z "$buffer" ]] && return
    _server_split_head "$buffer"
    _server_collect_matches
    (( ${#_SC_MATCHES} )) || return
    suggestion="${_SC_BASE}${_SC_MATCHES[1]}"
}

#--------------------------------------------------------------------#
# Suggestion list rendering (below the input, in the tmux pane)       #
#--------------------------------------------------------------------#

_SERVER_LIST_MAX_ROWS=10 # cap total pane height (input rows + list rows)

# Paint the candidate list in the rows below the input. $1 = 1-based index of the highlighted
# candidate, or 0 for none (fresh TAB, before the first cycle step). Reads _SERVER_CYCLE_MATCHES.
# Order matters (grounding probes): resize the pane first so the rows exist, then read the physical
# cursor row, then absolute-position each line — never rely on newlines that could scroll the pane.
_render_suggestions() {
    local sel="$1"
    local -a items=("${_SERVER_CYCLE_MATCHES[@]}")
    (( ${#items} )) || { _clear_suggestions; return }
    [[ -n "$TMUX_PANE" ]] || return

    local width
    width=$(tmux display-message -p -t "$TMUX_PANE" '#{pane_width}' 2>/dev/null)
    (( width > 0 )) || width=80

    # Column layout: widest candidate + 2 spaces of gutter, as many columns as fit.
    local c
    local -i maxlen=0
    for c in "${items[@]}"; do (( ${#c} > maxlen )) && maxlen=${#c}; done
    local -i colw=$((maxlen + 2))
    local -i ncols=$((width / colw))
    (( ncols < 1 )) && ncols=1
    local -i full_rows=$(( (${#items} + ncols - 1) / ncols ))

    # We only know the input height (and thus how many rows are left for the list) after asking
    # the pane, so measure the cursor row before sizing. Content is top-anchored, so the row is
    # stable across the resize below.
    local -i cy
    cy=$(tmux display-message -p -t "$TMUX_PANE" '#{cursor_y}' 2>/dev/null)
    (( cy >= 0 )) || cy=0
    local -i input_rows=$((cy + 1))
    # Reserve one blank row at the bottom so the list never butts against the footer — matches the
    # blank row the idle 2-row pane already keeps below the prompt.
    local -i max_list_rows=$((_SERVER_LIST_MAX_ROWS - input_rows - 1))
    (( max_list_rows < 0 )) && max_list_rows=0

    # Truncate with a "… +N more" summary row rather than silently capping.
    local -i list_rows=$full_rows
    local -i shown=${#items}
    local truncated=""
    if (( full_rows > max_list_rows )); then
        list_rows=$max_list_rows
        if (( list_rows >= 1 )); then
            local -i body_rows=$((list_rows - 1))
            shown=$((body_rows * ncols))
            truncated="1"
        else
            shown=0
        fi
    fi

    # Build the visible rows as absolute-positioned lines (+1 for the reserved bottom pad row).
    local -i desired=$((input_rows + list_rows + 1))
    (( desired < 2 )) && desired=2
    (( desired > _SERVER_LIST_MAX_ROWS )) && desired=$_SERVER_LIST_MAX_ROWS
    tmux resize-pane -t "$TMUX_PANE" -y "$desired" 2>/dev/null

    cy=$(tmux display-message -p -t "$TMUX_PANE" '#{cursor_y}' 2>/dev/null)
    (( cy >= 0 )) || cy=0

    # \e7 save cursor; clear everything below the input; paint; \e8 restore cursor.
    printf '\e7'
    printf '\e[%d;1H\e[0J' $((cy + 2)) # 1-based row directly below the (possibly wrapped) input
    local -i i r col row_num=$((cy + 2)) pad
    local cell line empty=""
    for (( r = 0; r < list_rows; r++ )); do
        if (( truncated != 0 && r == list_rows - 1 )); then
            local -i more=$(( ${#items} - shown ))
            printf '\e[%d;1H\e[2m… +%d more\e[0m' "$row_num" "$more"
        else
            line=""
            for (( col = 0; col < ncols; col++ )); do
                i=$(( r * ncols + col + 1 )) # 1-based index into items
                (( i > shown )) && break
                cell="${items[i]}"
                pad=$(( colw - ${#cell} ))
                if (( i == sel )); then
                    line+=$'\e[7m'"$cell"$'\e[0m'
                else
                    line+="$cell"
                fi
                (( pad > 0 )) && line+="${(l:$pad:)empty}" # $pad space fill via left-pad of ""
            done
            printf '\e[%d;1H%s' "$row_num" "$line"
        fi
        (( row_num++ ))
    done
    printf '\e8'
}

# Shrink the pane back to 2 rows and wipe the painted list rows.
_clear_suggestions() {
    _server_reset_cycle
    [[ -n "$TMUX_PANE" ]] || return
    tmux resize-pane -t "$TMUX_PANE" -y 2 2>/dev/null
    local -i cy
    cy=$(tmux display-message -p -t "$TMUX_PANE" '#{cursor_y}' 2>/dev/null)
    (( cy >= 0 )) || cy=0
    printf '\e7\e[%d;1H\e[0J\e8' $((cy + 2))
}

#--------------------------------------------------------------------#
# TAB widget: LCP + list on first TAB, cycle on further TABs          #
#--------------------------------------------------------------------#

# Cycle state persists across widget invocations (one zsh process for the whole loop). A snapshot
# of the buffer+cursor the previous TAB left lets a repeat TAB continue the cycle; any other key
# edits the buffer, so the snapshot no longer matches and the next TAB starts fresh.
_server_reset_cycle() {
    _SERVER_CYCLE_ARMED=0
    _SERVER_CYCLE_MATCHES=()
    _SERVER_CYCLE_IDX=0
}

_server_snapshot() {
    _SERVER_SNAP_BUFFER="$BUFFER"
    _SERVER_SNAP_CURSOR=$CURSOR
    _SERVER_CYCLE_ARMED=1
}

_server_cycle_active() {
    [[ "$_SERVER_CYCLE_ARMED" == 1 && "$BUFFER" == "$_SERVER_SNAP_BUFFER" && "$CURSOR" == "$_SERVER_SNAP_CURSOR" ]]
}

# Step the armed cycle one candidate in $1 ("next"/"prev", wrapping at both ends) and repaint.
_server_cycle_step() {
    local -i n=${#_SERVER_CYCLE_MATCHES}
    (( n == 0 )) && { _server_reset_cycle; return }
    if [[ "$1" == prev ]]; then
        # idx 0 (fresh list, nothing selected) and idx 1 both wrap to the last candidate.
        (( _SERVER_CYCLE_IDX <= 1 )) && _SERVER_CYCLE_IDX=$((n + 1))
        _SERVER_CYCLE_IDX=$(( _SERVER_CYCLE_IDX - 1 ))
    else
        _SERVER_CYCLE_IDX=$(( _SERVER_CYCLE_IDX % n + 1 ))
    fi
    local ins="${_SERVER_CYCLE_MATCHES[_SERVER_CYCLE_IDX]}"
    BUFFER="${_SERVER_CYCLE_BASE}${ins}${_SERVER_CYCLE_TAIL}"
    CURSOR=$(( ${#_SERVER_CYCLE_BASE} + ${#ins} ))
    _render_suggestions "$_SERVER_CYCLE_IDX"
    _server_snapshot
}

# $1 = cycle direction on a repeat press: "next" (TAB) or "prev" (Shift+TAB). A fresh press behaves
# the same either way (extend to LCP, list, arm); only the repeat-press stepping direction differs.
_server_tab_complete() {
    emulate -L zsh
    local dir="${1:-next}"
    local head="${BUFFER[1,CURSOR]}"
    local tail="${BUFFER[CURSOR+1,-1]}"

    # Repeat press during an armed cycle: step to the next/previous candidate.
    if _server_cycle_active; then
        _server_cycle_step "$dir"
        return
    fi

    # Fresh TAB: compute the matches for the word at the cursor.
    _server_split_head "$head"
    _server_collect_matches
    local -i n=${#_SC_MATCHES}
    (( n == 0 )) && { _server_reset_cycle; return }

    if (( n == 1 )); then
        local ins="${_SC_MATCHES[1]}"
        BUFFER="${_SC_BASE}${ins} ${tail}"
        CURSOR=$(( ${#_SC_BASE} + ${#ins} + 1 ))
        _clear_suggestions
        return
    fi

    # Many matches: extend to the case-insensitive LCP, list below, arm the cycle.
    local lcp="$(_server_lcp)"
    if (( ${#lcp} > ${#_SC_CUR} )); then
        BUFFER="${_SC_BASE}${lcp}${tail}"
        CURSOR=$(( ${#_SC_BASE} + ${#lcp} ))
    fi
    _SERVER_CYCLE_MATCHES=("${_SC_MATCHES[@]}")
    _SERVER_CYCLE_BASE="$_SC_BASE"
    _SERVER_CYCLE_TAIL="$tail"
    _SERVER_CYCLE_IDX=0
    _render_suggestions 0
    _server_snapshot
}

# The candidate list is painted out-of-band (escape codes below the input), so ZLE has no model of
# it and won't erase it on edits. This pre-redraw hook clears the list as soon as the buffer moves
# away from the snapshot the drawing TAB left — i.e. on any typing/deletion/space — then disarms so
# it fires once. A repeat TAB re-arms with a fresh snapshot, so cycling is unaffected. The plugin's
# `zle-*` ignore pattern means it never wraps this widget.
_server_line_pre_redraw() {
    if [[ "$_SERVER_CYCLE_ARMED" == 1 ]] \
        && { [[ "$BUFFER" != "$_SERVER_SNAP_BUFFER" ]] || [[ "$CURSOR" != "$_SERVER_SNAP_CURSOR" ]] }; then
        _clear_suggestions
    fi
}

# ZLE widgets receive no positional args, so the direction is fixed per widget via a thin wrapper.
_server_tab_complete_next() { _server_tab_complete next }
_server_tab_complete_prev() { _server_tab_complete prev }

_server_reset_cycle
zle -N server-tab-complete _server_tab_complete_next
zle -N server-tab-complete-back _server_tab_complete_prev
zle -N zle-line-pre-redraw _server_line_pre_redraw
bindkey '^I' server-tab-complete        # TAB: forward
bindkey '^[[Z' server-tab-complete-back # Shift+TAB (BackTab): backward
