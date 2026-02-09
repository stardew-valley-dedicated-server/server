# Contributing

Thank you for contributing to JunimoServer! This guide covers everything you need to know about contributing to the project.

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

Before making your first contribution, run the setup command to install development dependencies:

```bash
make setup
```

This installs:
- **commitlint** - Validates commit message format
- **git hooks** - Automatically validates commits before push

::: tip First Time?
Don't worry if this is your first open source contribution! We're here to help. The setup script and git hooks will guide you.
:::

#### Development Workflow

We use **GitHub Flow** with automated CI/CD:

**Quick workflow:**

1. **Setup** (first time only)
   ```bash
   make setup
   ```

2. **Create feature branch from master**
   ```bash
   git checkout master && git pull
   git checkout -b feat/my-feature
   ```

3. **Make changes and commit**
   ```bash
   # Make your changes
   git add .
   git commit -m "feat: add cabin management system"
   ```

4. **Push and open PR**
   ```bash
   git push -u origin feat/my-feature
   # Then create a PR on GitHub targeting 'master'
   ```

5. **After merge** - Preview builds publish automatically to DockerHub

#### Commit Conventions

We use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for semantic versioning and automated changelog generation.

**Format:**
```
<type>: <description>

[optional body]
```

**Types:**
- `feat:` - New feature (bumps minor version: 1.0.0 → 1.1.0)
- `fix:` - Bug fix (bumps patch version: 1.0.0 → 1.0.1)
- `docs:` - Documentation only (no version bump)
- `chore:` - Maintenance tasks (no version bump)
- `refactor:` - Code refactoring (no version bump)
- `test:` - Adding tests (no version bump)

**Breaking changes:**
- `feat!:` or `BREAKING CHANGE:` in body (bumps major version: 1.0.0 → 2.0.0)

**Examples:**
```bash
git commit -m "feat: add cabin management system"
git commit -m "fix: resolve memory leak in server loop"
git commit -m "docs: update installation guide"
git commit -m "feat!: redesign configuration format"
```

**Commit validation:**

After running `make setup`, git hooks will automatically validate your commits:
- ❌ Invalid: `"update readme"` → Error: type missing
- ✅ Valid: `"docs: update readme"` → Accepted

#### Making the Pull Request

- **PR title** should follow commit conventions
- **Link related issues** in the description (e.g., "Fixes #123")
- **Keep changes focused** - one feature/fix per PR
- **Avoid unrelated changes** - no formatting or whitespace changes unrelated to your PR
- **Write clear descriptions** - explain what you changed and why

We use "Squash and Merge" to combine all commits when merging.

## CI/CD Pipeline

### Overview

The project uses automated workflows for building and releasing:

| Workflow | Trigger | Output |
|----------|---------|--------|
| **Validate PR** | Pull requests → `master` | Build validation only |
| **Build Preview** | Push to `master` | `sdvd/server:preview` + versioned preview tags |
| **Build Release** | Merge Release PR | GitHub release + `sdvd/server:latest` + version tag |

### Creating Releases

Releases are automated via **release-please**, which analyzes conventional commits:

**The process:**

1. **Merge PRs to master** → Preview builds publish automatically
2. **release-please creates a Release PR** → Contains version bumps + CHANGELOG
3. **Review the Release PR** → Check version and changelog
4. **Merge the Release PR** → Production release published to GitHub + DockerHub

**How it works:**

- Analyzes commits since last release tag
- Determines version bump based on commit types
- Creates a branch with version bump commits
- Opens PR: `release-please--branches--master` → `master`
- When merged, creates GitHub release and Docker images

**Example:**
```
Last release: v1.0.2
Commits merged: feat A, fix B, feat C
Release PR created: 1.0.2 → 1.1.0
Merge Release PR → v1.1.0 published
```

### Preview Versioning

Preview builds use semantic versioning: `X.Y.Z-preview.N`

- **Base version**: Calculated from commits since last release
- **Preview number**: GitHub run number (auto-incremented)
- **Tags**: Both `preview` (rolling) and versioned (immutable)

**Example:**
```
Last release: v1.0.2
Merge feat A → 1.1.0-preview.47 published
Merge feat B → 1.2.0-preview.48 published (another feat, minor bump)
Merge fix C  → 1.2.0-preview.49 published
```

Preview versions show what the next release will be.

### Batching Features

You can merge multiple features before releasing:

```
Day 1: Merge feat A → 1.1.0-preview.1 published
       Release PR created (1.0.2 → 1.1.0)

Day 2: Merge feat B → 1.2.0-preview.2 published
       Release PR updated (1.0.2 → 1.2.0)

Day 3: Test preview.2 thoroughly

Day 4: Merge Release PR → v1.2.0 released
```

The Release PR automatically updates as you merge more commits.

## Docker Image Tags

Understanding the different Docker image tags:

| Tag | Use Case | Stability |
|-----|----------|-----------|
| `sdvd/server:latest` | Production deployments | Stable |
| `sdvd/server:1.0.2` | Specific release version | Stable, immutable |
| `sdvd/server:preview` | Latest preview build | Unstable, rolling |
| `sdvd/server:1.1.0-preview.47` | Specific preview build | Unstable, immutable |

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
  - ✅ Require status checks: `Validate Build`
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

## Getting Help with Contributing

If you're stuck or have questions:
- Ask in [Discord](https://discord.gg/w23GVXdSF7)
- Comment on the relevant issue or PR
- Check existing PRs for examples

We appreciate all contributions, big or small!
