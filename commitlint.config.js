module.exports = {
  extends: ['@commitlint/config-conventional'],
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
