---
paths:
  - ".github/**"
---

# GitHub evaluates `${{ }}` inside `run:` script text — never write the literal token in a run body

The Actions template engine interpolates `${{ ... }}` in a `run:` script before it reaches the shell, and it scans the whole block including comments. An empty or malformed `${{ }}` anywhere in the script — even inside a `# … ${{ }} …` comment — fails template validation with "An expression was expected" and aborts the job before the shell runs.

**Why:** A composite action's `run:` block carried a bash comment mentioning the literal `` `${{ }}`-like `` token; the engine tried to evaluate the empty braces, the action failed template validation, it merged anyway, and a follow-up PR was needed to reword the comment. The runtime value the comment described was already passed safely via `env:` — only the literal token in the comment text broke it.

**How to apply:** Pass data into a `run:` step through `env:` and reference it as `$VAR`/`${VAR}` in the script — never inline `${{ ... }}` into a run body, and never write the literal `${{` token even in a comment. Bash `${VAR}` and `$((...))` are untouched by the engine and safe. To mention the token in prose, break it up or describe it ("expression-like text").
