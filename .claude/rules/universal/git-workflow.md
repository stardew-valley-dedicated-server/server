# Stage git files explicitly by path — never `git add .`

Project-specific git rules; generic git knowledge is assumed.

## Staging

- Never use `git add .` or `git add -A`. Stage files explicitly by path.
- Verify a path's ignore status with `git check-ignore -v <path>` before assuming a file is ignored. Parent directory patterns (e.g., `**/bin`) affect nested files.
- For tracked files that now sit inside an ignored directory, either stage with `-f` or fix `.gitignore` with a negation pattern (e.g., `!docker/rootfs/opt/base/bin/`).

## Chained PRs

When a child PR depends on a parent PR, after the parent merges:

```bash
gh pr edit <child-num> --base master
git checkout <child-branch> && git rebase master && git push --force-with-lease
sleep 2 && gh pr merge <child-num> --squash --admin
```

The `sleep 2` before `gh pr merge` avoids GitHub returning "not mergeable" right after a force-push (sync delay).

## PR Descriptions

Bullet points of changes. No co-author attributions.
