# Reporting Bugs

## Is it a Bug or Something Else?

Before reporting, take a moment to consider whether you're encountering an actual bug or simply need help or clarification.

If you're unsure or just need assistance, it's often quicker and more effective to ask for help in our [Discord](https://discord.gg/w23GVXdSF7) community instead of filing a bug report.

::: tip What's a Bug?
A bug is:
- Unexpected crashes or errors
- Features not working as documented
- Data corruption or loss
- Performance issues that shouldn't occur

Not a bug:
- Needing help with setup or configuration
- Questions about how something works
- Feature requests or suggestions
:::

## Search Before Reporting

Please check the [open issues](https://github.com/stardew-valley-dedicated-server/server/issues) to see if the bug has already been reported.

If you find a similar issue, it's better to contribute to the discussion there by adding a comment, rather than creating a duplicate.

## How to Report a Bug

**1. Go to GitHub Issues**

Visit [github.com/stardew-valley-dedicated-server/server/issues](https://github.com/stardew-valley-dedicated-server/server/issues) and click "New Issue".

**2. Choose the Bug Report Template**

Select the bug report template to ensure you provide all necessary information.

**3. Fill Out the Template**

Include:
- **Clear title** - Briefly describe the issue
- **Description** - Explain what's happening
- **Steps to reproduce** - How can we recreate the bug?
- **Expected behavior** - What should happen instead?
- **Actual behavior** - What's actually happening?
- **Environment details** - OS, Docker version, JunimoServer version
- **Logs** - Include relevant error messages or logs
- **Screenshots** - If applicable

**4. Add Relevant Labels**

Use labels like:
- `bug` - For confirmed bugs
- `needs-verification` - If you're not 100% sure it's a bug
- `incompatible mod` - If the issue involves mod compatibility

## Found a Solution?

Don't let standards and conventions intimidate you! While guidelines help maintain consistency, they're not strict rules.

Even if you're unfamiliar with development workflows, your input is highly valued. Feel free to share your solution or idea — we're here to help refine it together.

If you've found a fix:

1. **Comment on the issue** with your solution
2. **Submit a pull request** if you can (see [Contributing](/community/contributing))
3. **Share in Discord** to help others with the same problem

## What Happens Next?

After you submit a bug report:

1. **Triage** - Maintainers will review and label your issue
2. **Investigation** - The team or community will investigate
3. **Discussion** - You may be asked for more information
4. **Resolution** - Someone will work on a fix, or it may be resolved by updates
5. **Closure** - The issue will be closed when fixed

## Priority Bugs

Some bugs are more urgent than others. Please mark issues that involve:

- Data loss or corruption
- Security vulnerabilities
- Server crashes
- Game-breaking bugs

With appropriate severity labels.

## Good Bug Report Example

```markdown
**Title:** Server crashes when loading modded save with > 10 players

**Description:**
The server crashes immediately when trying to load a save file that
has more than 10 player cabins with certain mods installed.

**Steps to Reproduce:**
1. Install Expanded Cabins mod
2. Create a farm with 12 cabins
3. Restart the server
4. Server crashes during load

**Expected Behavior:**
Server should load the save successfully

**Actual Behavior:**
Server crashes with NullReferenceException

**Environment:**
- OS: Ubuntu 22.04
- Docker: 24.0.5
- JunimoServer: 1.0.0
- Mods: Expanded Cabins v2.1.0

**Logs:**
[Attach relevant log output here]

**Additional Context:**
Works fine with 10 or fewer cabins. Issue only occurs with 11+.
```

Thank you for helping make JunimoServer better!
