# CI Log Dash-Over-Masking Runbook

**Symptom:** the CI log has `***` in place of `-` everywhere — `client-0`→`client***0`, `bash -e`→`bash ***e`, dates, GUIDs, flags, and even the runner boot banner mangled.

**Root cause (one line):** a GitHub Actions **secret whose stored value is multiline** (most often a trailing newline). GitHub masks multiline secrets fragment-by-fragment and registers short fragments — including a bare `-` — as global mask patterns, then replaces every `-` in the whole log. The genuinely-sensitive values still mask fine; the damage is the benign substring shredding every line. Confirmed by GitHub maintainer in [actions/runner#995](https://github.com/actions/runner/issues/995): *"When you have a multiline secret… we mask anything in that secret… this is by design."*

> The reliable signal is the **bisection** (step 5), not the value's shape. Do not assert which secret is the cause from values you can't read — the mask-length dump (step 2) and the delete/re-set bisect (step 5) turn it from guesswork into a two-run experiment.

## Steps

### 1. Confirm it's *fragment* over-masking, not normal masking

The tell: hyphens are masked in **non-secret** text — step-name echoes (`bash ***e {0}`), dates, the runner boot banner. Normal masking only blanks real secret *values*. If only secret values show `***`, there is no bug.

### 2. Get the authoritative mask dump (don't guess)

Set repo variable `ACTIONS_RUNNER_DEBUG=true`, re-run, then download the diagnostic logs:

```bash
gh api repos/<owner>/<repo>/actions/runs/<run-id>/logs > logs.zip
unzip logs.zip -d logs && cd logs
# the runner-diagnostic zip is nested; extract it, then:
grep -h 'Add new secret mask' runner-diagnostic-logs/*/Worker_*.log
```

Read the registered **lengths**. **No length-1 entry** → `-` is a *derived fragment* over-mask, i.e. a multiline/structured secret is the source — not an explicit dash secret and not an `add-mask` your code issued.

### 3. Localize: Worker vs Runner

In the diagnostic logs, check whether **`Runner_*.log`** (the earlier process) has literal hyphens while **`Worker_*.log`** (which loads job secrets) is mangled. Worker-mangled + Runner-clean ⇒ the source is a **job/environment secret**, not the platform `GITHUB_TOKEN` and not the runner itself. (You cannot fix the platform-token variant in-repo; rule out your own secrets first.)

### 4. Find the offending secret by measurement, not hypothesis

List candidate secrets with update times; suspect any set/changed since the last clean run:

```bash
gh api repos/<owner>/<repo>/environments/<env>/secrets --jq '.secrets[] | "\(.name)\t\(.updated_at)"'
```

For any secret value you *can* see (e.g. from `.env.test`), measure it — the trigger is **real newlines**, not hyphens-in-value:

```bash
printf '%s' "$VALUE" | tr -cd '\n' | wc -c    # >0 = MULTILINE = suspect
```

### 5. Bisect to prove the culprit (the decisive step)

Don't reason about stored values you can't read — **mutate one and observe**:

- **Delete** the suspect secret(s) → re-run. Dashes literal again? → that secret was the trigger.
- **Re-set** them from known-clean single-line values → re-run. Still clean? → confirmed; the *value* (not the presence) was the problem.

Guard: ensure the consuming step tolerates the secret being absent (e.g. the R2 publish step self-skips on empty), so the bisect run doesn't fail for an unrelated reason.

### 6. Fix: store the value single-line

- Re-set the secret with **no trailing newline**: `printf '%s' "$VALUE" | gh secret set NAME --env <env>` — use `printf '%s'`, never `echo` (which appends `\n`).
- For an unavoidably multiline value (a PEM, a JSON blob), either **inline it with escaped `\n`** on one line (`jq -c` does this) or **base64-encode** it to a single hyphen-free token and decode step-locally. The goal: the registered secret value has **no real newlines**.
- Move genuinely non-sensitive values (bucket names, public URLs) to **`vars.*`** so they aren't masked at all.

### 7. Verify the fix (full-log audit, not eyeballing)

Logs aren't reliable mid-run — wait for completion, then:

```bash
gh api repos/<owner>/<repo>/actions/runs/<id>/logs > l.zip && unzip -o l.zip -d out && cd out
grep -rhoE '[a-zA-Z0-9]\*\*\*[a-zA-Z0-9]' . | sort | uniq -c   # EMPTY = no dash over-mask
grep -rhc -- 'client-0' .                                       # literal hyphens present
```

Confirm the remaining `***` are only real secret values (passwords, auth headers). Run **twice** to confirm it's stable, not a one-off.

### 8. Clean up

Remove the debug variable so it doesn't bloat every subsequent log:

```bash
gh variable delete ACTIONS_RUNNER_DEBUG --repo <owner>/<repo>
```

## Two failure signatures worth recognizing

A secret accidentally set to a **single `-`** (a corruption, not a real value) produces distinctive downstream errors that look unrelated to masking:

- `jq: parse error: Invalid numeric literal at EOF at line 1, column 1` — `jq` read one `-` then EOF.
- `System.Text.Json.JsonException: Expected a digit ('0'-'9'), but instead reached end of data … BytePositionInLine: 1` (via `ConsumeNegativeSign`), exit code 139 — .NET read one `-` then EOF.

Both mean the secret's value is a lone `-`. Re-set it to the correct value. (A bare `-` value will *also* over-mask, since it registers `-` directly.)
