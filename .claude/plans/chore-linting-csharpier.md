# CSharpier Integration + xUnit Analyzer Enforcement

## Context

CSharpier 1.2.6 is already declared in `.config/dotnet-tools.json`. Two pieces of work in this plan:

1. **CSharpier integration** in four places: VSCode format-on-save, lefthook pre-commit, CI on PRs, a manual Make target. Claude-hook auto-format was dropped — the lefthook pre-commit catches anything Claude writes.
2. **xUnit analyzer enforcement**: turn warnings into errors on the only test project that references xUnit.

## Verified facts

- **CSharpier 1.2.6 supports `net8.0`/`net9.0`/`net10.0`** (nuget.org). CI uses `10.0.x` — `tests/JunimoServer.Tests/` already targets net10, no new SDK introduced.
- **`.gitignore` already excludes** `**/decompiled` (line 51), `/tools/.playground` (line 69), `**/bin`, `**/obj`, `**/.output`, `**/debug`, `**/tmp`. CSharpier respects `.gitignore`, so `.csharpierignore` needs only the tracked submodule (`sub_modules/`).
- **`.dockerignore` already excludes** `decompiled/` and `sub_modules/` (lines 23, 28). Consistent.
- **No tracked generated C# files** (`git ls-files '**/*.cs' | grep -E "Generated|\.g\.cs"` returns nothing).
- **Root `.editorconfig` (`root = true`, `indent_size = 4`) already matches CSharpier's C# defaults** for the keys CSharpier reads (`indent_style`, `indent_size`, `max_line_length`, `end_of_line`). No `.csharpierrc.yaml` needed.
- **xUnit analyzers ship via `xunit.v3` transitively**: `xunit.v3 3.2.2 → xunit.v3.mtp-v1 → xunit.analyzers 1.27.0` (`~/.nuget/packages/xunit.v3.mtp-v1/3.2.2/xunit.v3.mtp-v1.nuspec`).
- **Only `tests/JunimoServer.Tests/JunimoServer.Tests.csproj` references xunit** (`git grep -l xunit -- '*.csproj'`). One project to configure.
- **A clean build of that project produces 0 warnings, 0 errors** at default severities (after `rm -rf bin obj` + `dotnet build`). Existing code is already clean.
- **`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` keeps the build clean** end-to-end. A deliberate `Assert.True(list.Contains(x))` fails with `error xUnit2017`. Both verified during planning.
- **Bulk-promoting `dotnet_analyzer_diagnostic.category-Usage.severity = error` is wrong**: `Usage` is also a Microsoft NetAnalyzers category, so it promotes unrelated CA-prefix rules. Running it produced 24 false-positive errors on this codebase. Discarded.

## Order of operations (matters — avoids red CI on the bootstrap PR)

1. **Commit A** — `chore(format): add csharpier configuration`. Adds the config files and VSCode settings only. No CI/hook wiring. Safe to land alone.
2. **Commit B** — `style(format): apply csharpier to all c# sources`. Run `dotnet tool restore && dotnet csharpier format .` and commit the (large) diff with no other changes. Add this commit's SHA to `.git-blame-ignore-revs` in the same commit so `git blame` skips it.
3. **Commit C** — `chore(ci): enforce csharpier via lefthook, make, and pr workflow`. Lefthook hook + Make targets + new CI job. Tree is already formatted, so CI passes.
4. **Commit D** — `chore(tests): treat xunit analyzer warnings as errors`. Independent of A–C; can land before, after, or interleaved. Kept separate so a future bisect can isolate "which change broke the build" cleanly.

Splitting B from C keeps the formatting commit reviewable as "no logic changes" and the wiring commit small.

## File changes

### Commit A — configuration only

#### NEW `.csharpierignore`

CSharpier respects `.gitignore`. The only tracked path that needs exclusion is the submodule.

```
sub_modules/
```

#### NEW `.vscode/extensions.json`

```json
{
  "recommendations": [
    "csharpier.csharpier-vscode",
    "ms-dotnettools.csharp"
  ]
}
```

#### NEW `.vscode/settings.json`

Scope CSharpier as the formatter only for C# (the `[csharp]` block) so OmniSharp / C# Dev Kit defaults stay untouched everywhere else. The CSharpier extension auto-discovers the local tool from `.config/dotnet-tools.json` — no path config required.

```json
{
  "[csharp]": {
    "editor.defaultFormatter": "csharpier.csharpier-vscode",
    "editor.formatOnSave": true,
    "editor.formatOnSaveMode": "file"
  }
}
```

### Commit B — bulk reformat

```bash
dotnet tool restore
dotnet csharpier format .
git add -- '*.cs'
git commit -m "style(format): apply csharpier to all c# sources"
```

The `git add -- '*.cs'` glob pathspec is the practical exception to the "stage by path" rule in `.claude/rules/universal/git-workflow.md` — explicitly listing every reformatted file is impractical for a thousand-file diff, and the pathspec is narrow enough to make the staging set predictable. Run `git status --short | grep -v '\.cs$'` after the `git add` and before the commit to confirm nothing else was caught.

Then create `.git-blame-ignore-revs` with the freshly created commit's SHA and amend it onto commit B (or commit it on top — both work; GitHub's blame UI honors the file automatically):

```
# Bulk csharpier reformat — do not blame on this commit
<full-40-char-SHA>
```

For local `git blame` to use the file, contributors must run `git config blame.ignoreRevsFile .git-blame-ignore-revs` once.

### Commit C — wiring

#### EDIT `lefthook.yml` — append `pre-commit` block

Run `csharpier format` on staged files and re-stage (per CSharpier's documented lefthook pattern). `--no-cache` because lefthook only passes staged paths; cached entries from prior whole-tree runs would mismatch. CI's `csharpier check` is the safety net.

```yaml
pre-commit:
  parallel: true
  commands:
    csharpier:
      glob: "*.{cs,csx}"
      run: dotnet csharpier format --no-cache {staged_files}
      stage_fixed: true
```

#### EDIT `Makefile`

**(a)** Update `install` target so the pre-commit hook works on a fresh checkout:

```makefile
install:
	@echo Installing development dependencies...
	@npm ci
	@dotnet tool restore
	@echo Setup complete. Git hooks are now active.
```

**(b)** Add format targets (insert before the `help:` block):

```makefile
# Auto-format all C# files using CSharpier (rewrites in place)
format:
	@dotnet csharpier format .

# Verify all C# files are formatted (CI). Exits non-zero on diff.
format-check:
	@dotnet csharpier check .
```

**(c)** Update the `help` target — insert a Formatting section. Match existing style (lines 257–294):

```makefile
	@echo Formatting:
	@echo "  make format       - Format all C# files (CSharpier)"
	@echo "  make format-check - Check formatting without writing (CI)"
	@echo ""
```

#### EDIT `.github/workflows/validate-pr.yml` — add a third job

Append after `validate-build`. Pin actions by SHA + version comment per repo convention (see line 30 `actions/setup-node@6044e13b… # v6.2.0`). The implementer must look up current SHAs for `actions/setup-dotnet@v4` and `actions/cache@v4` from each action's GitHub release page — do not invent SHAs.

```yaml
  validate-format:
    name: Validate Formatting
    runs-on: ubuntu-latest

    steps:
      - name: Checkout code
        uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
        with:
          ref: ${{ github.event.pull_request.head.sha }}

      - name: Setup .NET
        uses: actions/setup-dotnet@<lookup-sha> # v4.x.y
        with:
          dotnet-version: '10.0.x'

      - name: Cache NuGet packages
        uses: actions/cache@<lookup-sha> # v4.x.y
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-tools-${{ hashFiles('.config/dotnet-tools.json') }}
          restore-keys: ${{ runner.os }}-nuget-tools-

      - name: Restore .NET tools
        run: dotnet tool restore

      - name: Check C# formatting
        run: dotnet csharpier check .
```

### Commit D — `<TreatWarningsAsErrors>` on the test project

#### EDIT `tests/JunimoServer.Tests/JunimoServer.Tests.csproj`

Add one line inside the existing `<PropertyGroup>` (alongside `TargetFramework`, `Nullable`, etc.):

```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

## Critical files

- `.csharpierignore` (new — single line: `sub_modules/`)
- `.vscode/settings.json` (new)
- `.vscode/extensions.json` (new)
- `.git-blame-ignore-revs` (new, in commit B)
- `lefthook.yml` (edit — append `pre-commit:` block; keep existing `commit-msg:`)
- `Makefile` (edit — `install`, two new targets, help text)
- `.github/workflows/validate-pr.yml` (edit — append `validate-format` job)
- `tests/JunimoServer.Tests/JunimoServer.Tests.csproj` (edit, in commit D — one new line in the existing `<PropertyGroup>`)

## Verification (run end-to-end after all four commits)

| Requirement | How to verify | Expected |
|---|---|---|
| (1) Format on save in VSCode | Open `mod/JunimoServer/ModEntry.cs`, add stray spaces, save | File reformats; CSharpier shown in status bar |
| (2) Pre-commit hook | Mis-format a `.cs` file, `git add`, `git commit -m "test"` | Hook reformats and re-stages; commit succeeds with formatted code |
| (3) GitHub workflow | Open a PR with one mis-formatted line | `validate-format` job fails; pushing the fix passes |
| (4) Manual Make target | `make format-check` on clean tree → exit 0; mis-format a file → `make format-check` exits non-zero; `make format` fixes it | As described |
| (5) xUnit warnings as errors | `rm -rf tests/JunimoServer.Tests/{bin,obj} && dotnet build tests/JunimoServer.Tests/JunimoServer.Tests.csproj` clean; then add a `.cs` with `Assert.True(list.Contains(x))` and rebuild | Clean build: 0 warnings / 0 errors. With violation: `error xUnit2017`, build fails. (Both verified during planning.) |
| Bonus: fresh checkout | `rm -rf node_modules && make install` then test (2) above without manual `dotnet tool restore` | Hook works |

Items (1)–(5) are runtime checks. Per `runtime-post-conditions-are-gates.md`, do not declare done from static review — actually exercise each one. Smoke commits for (2) and (3) can be discarded with `git reset --hard HEAD~`.

## Sharp edges

- **First-format diff size**: ~10 in-scope csproj projects → expect commit B to touch hundreds–thousands of files. Reviewers should use GitHub's "Hide whitespace" toggle. The `.git-blame-ignore-revs` entry keeps blame archaeology clean.
- **Sanity-check one file before bulk-formatting**: format a single file (e.g. `mod/JunimoServer/ModEntry.cs`) and inspect the diff for surprises before running `csharpier format .` on the whole tree. If the result is unexpected, add a `.csharpierrc.yaml` with the desired override rather than committing thousands of files we're not happy with.
- **Action SHAs are placeholders**: `<lookup-sha>` for `setup-dotnet` and `cache` must be replaced with real SHAs from each action's GitHub release page. Per repo convention, format is `<sha> # v<version>`.
- **`{staged_files}` and Windows paths with spaces**: lefthook quotes paths, but the only path with a space (`decompiled/sdv-1.6.15-24356/Stardew Valley.csproj`) is in an excluded directory, so this won't bite.
- **Dependabot picks up bumps automatically**: CSharpier 1.x bumps will arrive via the existing `nuget` group in `.github/dependabot.yml` — no dependabot changes needed.
- **CI runtime**: `dotnet tool restore` + `csharpier check` ≈ 30–60s cold cache, ≈10s warm on net10 SDK. Acceptable as a third PR job.
- **Out-of-scope, flagged**: existing `.editorconfig` line `[{*.yml, *.yaml}]` has spaces inside the braces, which some editorconfig parsers reject. Doesn't affect CSharpier (it only looks at C#-applicable keys), but a future cleanup should change it to `[*.{yml,yaml}]`.
- **`TreatWarningsAsErrors` covers all warnings, not just xUnit**: it includes Microsoft NetAnalyzer rules (CA-prefix), nullable-reference warnings, and compiler warnings. The current code is clean against all of these on a fresh build, so no work is needed today; future violations from any source will block the build. If a future Microsoft analyzer release introduces a false positive, the escape hatch is `<WarningsNotAsErrors>CAxxxx</WarningsNotAsErrors>` in the same `<PropertyGroup>`, or a targeted `dotnet_diagnostic.CAxxxx.severity = warning` in a project-local `.editorconfig`.
