# Contributing

Thank you for contributing to JunimoServer! This guide covers everything you need to know.

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

## Getting Help

If you're stuck or have questions:
- Ask in [Discord](https://discord.gg/w23GVXdSF7)
- Comment on the relevant issue or PR
- Check existing PRs for examples

We appreciate all contributions, big or small!
