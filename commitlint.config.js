module.exports = {
  extends: ['@commitlint/config-conventional'],
  // GitHub's "Apply suggestions from code review" button generates a fixed,
  // non-conventional subject that the UI does not let you customize. Exempt it
  // so batch-applying review suggestions doesn't fail the commit lint. The
  // repo squash-merges into master, so these subjects never reach the changelog.
  ignores: [(message) => message.startsWith('Apply suggestions from code review')],
  rules: {
    // Match the types defined in release-please-config.json
    'type-enum': [
      2,
      'always',
      [
        'feat',     // Features
        'fix',      // Bug Fixes
        'perf',     // Performance Improvements
        'revert',   // Reverts
        'docs',     // Documentation
        'style',    // Styles
        'chore',    // Miscellaneous Chores
        'refactor', // Code Refactoring
        'test',     // Tests
        'build',    // Build System
        'ci'        // Continuous Integration
      ]
    ],
    // A scope names an *area*, never a kind of work (`ci`/`build` are types, so
    // there is no scope `ci`). Optional by design — no `scope-empty` rule. The
    // `.claude/skills/commit` skill maps files-edited→scope and must stay in sync
    // with this list. The `deps`/`deps/*` block mirrors renovate.json — keep it
    // in sync or Renovate's own PRs fail.
    'scope-enum': [
      2,
      'always',
      [
        // Product / runtime — recurring, changelog-facing areas
        'host',
        'core',
        'api',
        'steam',
        'auth',
        'lobby',
        'cabins',
        'chat',
        'saves',
        'crop-saver',
        'networking',
        'gameplay',
        'compat',
        'backup',
        'diagnostics',
        'discord',
        // Infrastructure — runtime image / container config
        'docker',
        // Tooling / repo
        'tests',
        'test-runner',
        'test-client',
        'test-ui',
        'tools',
        'claude',
        'docs',
        'repo',
        // Renovate emits these, humans don't. `deps-dev` is emitted for
        // devDependency updates and is not itself a renovate.json scope.
        'deps',
        'deps-dev',
        'deps/server',
        'deps/steam-service',
        'deps/discord-bot',
        'deps/test-runner',
        'deps/test-client',
        'deps/test-ui',
        'deps/tools',
        'deps/project',
        'deps/docs',
        'deps/docker',
        'deps/github-actions',
        'deps/dotnet-sdk'
      ]
    ],
    // Enforce kebab-case so casing drift (`cropSaver`) can't reappear; the
    // `deps/*` slash forms and `deps-dev` hyphen still pass.
    'scope-case': [2, 'always', 'kebab-case'],
    // Ensure subject is not empty
    'subject-empty': [2, 'never'],
    // Ensure type is not empty
    'type-empty': [2, 'never'],
    // Allow mixed case for acronyms like PR, CLI, STEAM_AUTH_PORT
    'subject-case': [0]
  }
};
