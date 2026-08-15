#!/bin/bash
# TAB completion for the attach-cli `server>` prompt. Sourced by server-command-loop before
# its input loop; the Debian base and Alpine modern images carry an identical copy.
#
# Candidates come from /tmp/server-commands, written at mod startup by CommandCatalogFile.cs:
# our commands with subcommands/flags, plus SMAPI/other-mod command names. Plain tab-separated
# lines parsed with pure bash (no jq in the images). If the file is absent (mod still
# booting), TAB completes nothing and the prompt is never blocked.
#
# `read -e` consults readline but NOT programmable completion — `complete -F` specs never
# fire under it (verified in a container TTY on both images' stacks) — so TAB is bound with
# `bind -x`, which edits READLINE_LINE/READLINE_POINT directly.

CATALOG="/tmp/server-commands"
PASSWORD_MODE_FILE="${PASSWORD_MODE_FILE:-/tmp/smapi-password-mode}"

# First token of every header line (lines whose first tab-field is a single word).
# `cli` is skipped: word 0 always offers the pseudo-command, so a real console command
# with that name would otherwise be listed twice.
_catalog_command_names() {
    local first rest
    [ -r "$CATALOG" ] || return 0
    while IFS=$'\t' read -r first rest; do
        case "$first" in
            '' | *' '* | cli) ;;
            *) printf '%s\n' "$first" ;;
        esac
    done < "$CATALOG"
}

# Subcommand names for one command (empty for names-only smapi/other-mod commands).
_catalog_subcommands_of() {
    local cmd="${1,,}" first rest
    [ -r "$CATALOG" ] || return 0
    while IFS=$'\t' read -r first rest; do
        case "$first" in
            "$cmd "*) printf '%s\n' "${first#* }" ;;
        esac
    done < "$CATALOG"
}

# Flags of one subcommand, minus any flag already typed on the line (passed as $3...).
_catalog_flags_of() {
    local cmd="${1,,}" sub="${2,,}"
    shift 2
    local first flags flag word skip
    local -a flag_list=()
    [ -r "$CATALOG" ] || return 0
    while IFS=$'\t' read -r first flags; do
        if [ "$first" = "$cmd $sub" ]; then
            read -r -a flag_list <<< "$flags" # split without glob expansion
            for flag in "${flag_list[@]}"; do
                skip=
                for word in "$@"; do
                    [ "${word,,}" = "$flag" ] && skip=1 && break
                done
                [ -z "$skip" ] && printf '%s\n' "$flag"
            done
            return 0
        fi
    done < "$CATALOG"
}

# Word index + all words on the line -> newline-separated candidates for that position.
# Word 0: command names + the `cli` pseudo-command; word 1: subcommands (or the cli verbs);
# word 2+: remaining flags. Free-form args (a save name, an fps value) yield nothing.
_collect_candidates() {
    local idx="$1"
    shift
    local -a w=("$@")
    [ -f "$PASSWORD_MODE_FILE" ] && return 0 # never during password entry
    case "$idx" in
        0)
            _catalog_command_names
            printf '%s\n' cli
            ;;
        1)
            if [ "${w[0],,}" = cli ]; then
                printf '%s\n' exit quit detach clear
            else
                _catalog_subcommands_of "${w[0]}"
            fi
            ;;
        *)
            [ "${w[0],,}" = cli ] || _catalog_flags_of "${w[0]}" "${w[1]}" "${w[@]:2}"
            ;;
    esac
}

# bind -x TAB handler: completes the word at the cursor case-insensitively. Unique match ->
# insert it plus a trailing space; ambiguous -> extend to the longest common prefix and list
# the candidates (the pane scrolls one line; readline redraws the prompt line afterwards).
_server_tab_complete() {
    local head="${READLINE_LINE:0:READLINE_POINT}"
    local tail="${READLINE_LINE:READLINE_POINT}"

    # Words before the cursor; the last one is the word being completed unless the cursor
    # sits right after whitespace (then an empty word starts at the cursor).
    local -a words=()
    read -r -a words <<< "$head"
    local cur=""
    if [ "${#words[@]}" -gt 0 ] && [[ "$head" != *[[:space:]] ]]; then
        cur="${words[${#words[@]} - 1]}"
        unset "words[$((${#words[@]} - 1))]"
    fi

    local -a matches=()
    local cand cand_lc cur_lc="${cur,,}"
    while IFS= read -r cand; do
        [ -n "$cand" ] || continue
        cand_lc="${cand,,}"
        [ "${cand_lc:0:${#cur_lc}}" = "$cur_lc" ] && matches+=("$cand")
    done < <(_collect_candidates "${#words[@]}" "${words[@]}" "$cur")

    [ "${#matches[@]}" -eq 0 ] && return 0

    local base="${head%"$cur"}"

    if [ "${#matches[@]}" -eq 1 ]; then
        READLINE_LINE="${base}${matches[0]} ${tail}"
        READLINE_POINT=$((${#base} + ${#matches[0]} + 1))
        return 0
    fi

    # Ambiguous: extend to the longest common prefix of the candidates (case-insensitive,
    # keeping the first candidate's casing) ...
    local lcp="${matches[0]}" lcp_lc m m_lc
    for m in "${matches[@]:1}"; do
        m_lc="${m,,}"
        lcp_lc="${lcp,,}"
        while [ -n "$lcp" ] && [ "${m_lc:0:${#lcp}}" != "$lcp_lc" ]; do
            lcp="${lcp:0:$((${#lcp} - 1))}"
            lcp_lc="${lcp,,}"
        done
    done
    if [ "${#lcp}" -gt "${#cur}" ]; then
        READLINE_LINE="${base}${lcp}${tail}"
        READLINE_POINT=$((${#base} + ${#lcp}))
    fi

    # ... and list all candidates on one wrapped line above the prompt.
    printf '\n%s\n' "${matches[*]}"
}

bind -x '"\t": _server_tab_complete' 2>/dev/null || true
