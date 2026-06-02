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
    // Ensure subject is not empty
    'subject-empty': [2, 'never'],
    // Ensure type is not empty
    'type-empty': [2, 'never'],
    // Allow mixed case for acronyms like PR, CLI, STEAM_AUTH_PORT
    'subject-case': [0]
  }
};
