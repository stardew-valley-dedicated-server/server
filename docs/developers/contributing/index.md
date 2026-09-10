# Contributing

## How To Contribute

### Creating an Issue

#### Bug Reports

Before submitting, please review our [Reporting Bugs](/community/reporting-bugs) guide for tips on how to identify and report issues effectively.

#### Feature Requests

Make sure there isn't already an open issue or PR about the feature you're proposing. Check:
- [Open issues](https://github.com/stardew-valley-dedicated-server/server/issues)
- [Open pull requests](https://github.com/stardew-valley-dedicated-server/server/pulls)

### Creating a Pull Request

#### Development Setup

Before making your first contribution, run the install command to set up development dependencies:

```bash
make install
```

This installs:
- **commitlint** - Validates commit message format
- **CSharpier** - C# formatter, restored as a local dotnet tool
- **Biome** - JS/TS formatter and linter, installed via the root `npm ci`
- **git hooks** - Auto-format staged C# and JS/TS files on commit, validate commit messages, block direct pushes to `master`

::: tip Running compose outside `make`
`make` targets export `IMAGE_VERSION=local`, so compose uses your local build (`sdvd/server:local`). Outside `make` the variable is unset and services resolve to `sdvd/server:latest`, which `docker compose up`/`run` pull from the registry (`exec` reuses the running container). Set `IMAGE_VERSION=local` in `.env` or your shell to reuse the local image ([variable reference](/developers/testing/e2e-testing)). `make` also layers `docker-compose.dev.yml`, which holds the sidecar build contexts; without it `docker compose build` has nothing to build.
:::

#### Line Endings

The repository enforces **LF line endings** for all text files via `.gitattributes` (`* text=auto eol=lf`). This keeps files identical on Windows and inside the Linux containers, and is required for shell scripts and Dockerfiles to run.

You do **not** need to change your git config — an explicit `eol` in `.gitattributes` overrides `core.autocrlf`, so the result is the same whether yours is `true`, `false`, or `input`. A PR check (`Validate Line Endings`) fails if CRLF reaches the index.

If you cloned before this policy existed and see `CRLF will be replaced by LF` warnings, refresh your working tree once. Commit or stash any work first — `reset --hard` discards uncommitted changes to tracked files (untracked files are left alone):

```bash
git rm --cached -r -q .                # clear index entries (does NOT delete files)
git reset --hard                       # re-checkout every tracked file as LF on disk
git ls-files --eol | grep 'w/crlf'     # expect no output
```

#### Code Formatting

C# code is formatted by [CSharpier](https://csharpier.com/), and style rules (braces, file-scoped namespaces, unused usings) are enforced as build errors via `.editorconfig`. You usually don't need to do anything:

- **VSCode** formats on save — `.vscode/settings.json` selects the CSharpier extension; install it when prompted by the workspace recommendations.
- **The pre-commit hook** auto-formats staged C# files and re-stages them. Partial staging (`git add -p`) is safe: lefthook hides unstaged changes while the hook runs and restores them afterwards.
- **CI** (`Validate Formatting`) fails any PR with formatting drift.

JS/TS code (and Vue/JSON/CSS) is formatted and linted by [Biome](https://biomejs.dev/); the covered projects are scoped in the root `biome.jsonc`. The same conveniences apply:

- **VSCode** formats on save via the `biomejs.biome` extension (workspace recommendation).
- **The pre-commit hook** auto-fixes staged JS/TS files and re-stages them.
- **CI** (`Validate JS/TS`) runs `biome ci` and fails on formatting drift or lint errors.

Manual targets:

```bash
make lint-check # verify C# + JS/TS style rules + formatting without writing (builds the solution)
make lint-fix   # auto-fix C# + JS/TS style violations, then re-format (builds the solution)
```

CI's `Validate Formatting` runs the CSharpier half of `lint-check` plus the analyzer style rules for the test projects, and `Validate JS/TS` runs the Biome half; the mod's analyzer rules are enforced as build errors when CI builds the Docker image.

#### Development Workflow

We use **GitHub Flow** with automated CI/CD:

**Quick workflow:**

1. **Install dev dependencies** (first time only)
   ```bash
   make install
   ```

2. **Create feature branch from master**
   ```bash
   git checkout master && git pull
   git checkout -b feat/my-feature
   ```

3. **Make changes and commit**
   ```bash
   # Make your changes, then stage them explicitly by path
   git add path/to/changed-file
   git commit -m "feat: add cabin management system"
   ```

4. **Push and open PR**
   ```bash
   git push -u origin feat/my-feature
   # Then create a PR on GitHub targeting 'master'
   ```

5. **After merge** - A [Build Preview](ci-cd.md#build-preview-pipeline) publishes a preview image to DockerHub for testing — automatically on merge when `AUTO_BUILD_PREVIEW=true`, otherwise on a maintainer's manual dispatch

#### Commit Conventions

We use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for semantic versioning and automated changelog generation.

**Format:**
```
<type>(<scope>): <description>

[optional body]
```

Pick the **type** first — what *kind* of work it is; it drives the changelog and version bump. The **scope** is *which area* it touches, and is optional. **Kind → type, area → scope.**

**Types** (11, all enforced):

| Type | Use for | Changelog |
|---|---|---|
| `feat` | New player/admin-facing capability | Features (minor bump) |
| `fix` | Bug fix in shipped behavior | Bug Fixes (patch bump) |
| `perf` | Faster/lighter shipped behavior | Performance |
| `revert` | Reverts a previous commit | shown |
| `docs` | Documentation only | shown |
| `refactor` | Restructure with no behavior change | hidden |
| `test` | Test code only | hidden |
| `build` | How artifacts are compiled/produced — Dockerfile build stages, `.csproj`, `Directory.Build.props`, SMAPI-version bumps | hidden |
| `ci` | Pipelines, GitHub Actions, release automation | hidden |
| `chore` | Everything else with no more specific kind — deps, repo config, `.claude/` | hidden |
| `style` | Formatting only | hidden |

**`ci` and `build` are types, never scopes** — a CI change is `ci:`, a build-system change is `build:`, never `fix(ci)` or `chore(ci)`. Reach for a hidden type when its *kind* fits (`test`, `refactor`, `build`, `ci`, `style`); use `chore` only when none does — never `chore(<kind>)`, which throws the area away.

**Scopes** — optional, but when present must come from the enforced enum below (spelling and kebab-casing are validated, so `fix(cabin)` and `crop_saver` are rejected):

| Group | Scopes |
|---|---|
| Product / runtime | `host` `core` `api` `steam` `auth` `lobby` `cabins` `chat` `saves` `crop-saver` `networking` `gameplay` `compat` `backup` `diagnostics` `discord` |
| Infrastructure | `docker` |
| Tooling / repo | `tests` `test-runner` `test-client` `test-ui` `tools` `claude` `docs` `repo` |
| Dependencies (Renovate-emitted) | `deps` `deps-dev` `deps/*` |

Rough guide: `host` = unattended host behavior (auto-pause/sleep, festivals); `core` = the server process/infrastructure; `chat` = the command framework + Discord relay, but a feature-specific command attributes to its feature (a `!cabin` change is `feat(cabins)`); `gameplay` = server-side vanilla-behavior tweaks; `compat` = interop with other/3rd-party mods.

A bare `feat: …` with no scope is valid — that's the pressure-release valve for rare one-off areas. The enum lives in [`commitlint.config.js`](https://github.com/stardew-valley-dedicated-server/server/blob/master/commitlint.config.js); adding a recurring area is a one-line PR against it.

**Breaking changes:**
- `feat!:` or `BREAKING CHANGE:` in body (bumps major version: 1.0.0 → 2.0.0)

**Examples:**
```bash
git commit -m "feat(cabins): add cabin management system"
git commit -m "fix(core): resolve memory leak in the game loop"
git commit -m "ci: cache NuGet restore across build jobs"
git commit -m "docs: update installation guide"
git commit -m "feat!: redesign configuration format"
```

**Commit validation:**

After running `make install`, git hooks will automatically validate your commits:
- ❌ Invalid: `"update readme"` → Error: type missing
- ❌ Invalid: `"fix(cabin): typo"` → Error: scope not in enum (it's `cabins`)
- ✅ Valid: `"docs: update readme"` → Accepted

#### Making the Pull Request

- **PR title** should follow commit conventions
- **Link related issues** in the description (e.g., "Fixes #123")
- **Keep changes focused** - one feature/fix per PR
- **Avoid unrelated changes** - no formatting or whitespace changes unrelated to your PR
- **Write clear descriptions** - explain what you changed and why

We use "Squash and Merge" to combine all commits when merging.

## For Maintainers

### Repository Setup

#### 1. Configure GitHub Secrets

Go to **Settings → Secrets → Actions** and add:

| Secret | Description |
|--------|-------------|
| `DOCKERHUB_USERNAME` | DockerHub username |
| `DOCKERHUB_TOKEN` | [Create token](https://hub.docker.com/settings/security) |
| `STEAM_USERNAME` | Steam username (for game download during build) |
| `STEAM_PASSWORD` | Steam password |
| `STEAM_REFRESH_TOKEN` | Steam OAuth refresh token (optional, preferred over password) |

#### 2. Configure Branch Protection

**Protect `master`:**
- Settings → Branches → Add rule
- Pattern: `master`
- Enable:
  - ✅ Require pull request before merging
  - ✅ Require status checks: `Validate Build`, `Validate Commits`, `Validate Formatting`, `Validate JS/TS`, `Validate Line Endings`, `Validate PR Title`
  - ✅ Require approvals: 1

#### 3. Configure Fork PR Protection

Settings → Actions → General → Fork pull request workflows:
- Select "Require approval for all outside collaborators"

## Troubleshooting

### Build Issues

**Build fails with Steam auth error:**
- Verify `STEAM_USERNAME` and `STEAM_PASSWORD` (or `STEAM_REFRESH_TOKEN`) secrets are set
- Ensure Steam account owns Stardew Valley

**Docker push fails:**
- Verify `DOCKERHUB_TOKEN` has read/write permissions
- Check repository `sdvd/server` exists on DockerHub

### Version Issues

**Version doesn't bump correctly:**
- Check commit messages follow conventional format
- Use `git log <last-tag>..HEAD` to verify commits
- Commits without `feat:` or `fix:` don't bump version

**Release PR not created:**
- Ensure commits since last tag include version-bumping types (`feat:`, `fix:`)
- Check GitHub Actions logs for errors

## Resources

- [Conventional Commits](https://www.conventionalcommits.org/)
- [GitHub Flow](https://docs.github.com/en/get-started/quickstart/github-flow)
- [Semantic Versioning](https://semver.org/)
- [release-please](https://github.com/googleapis/release-please)

## Getting Help

- Ask in [Discord](https://discord.gg/w23GVXdSF7)
- Comment on the relevant issue or PR
- Check existing PRs for examples
