---
name: cleanup
description: Cleans up after a merged pull request. Confirms the merge, deletes a leftover untracked plan, removes the worktree, deletes the local and remote branch, and fast-forwards master in the main checkout. Use when the user says a PR is merged and asks to clean up.
argument-hint: [PR number or branch, default: the current branch]
---

# Clean up after a merge

The main checkout is the parent folder of `git rev-parse --path-format=absolute --git-common-dir`. The worktree is the linked worktree in `git -C <main> worktree list --porcelain` (every entry after the first, which is the main checkout) whose `branch` line is `refs/heads/<headRefName>`; if there is none, skip the worktree checks in step 1 and all of step 3.

The branch name comes from PR metadata and git allows `$`, `|`, and `;` in it, so single-quote it in every command.

## 1. Confirm it's safe

- Find the PR from `$ARGUMENTS` or the current branch: `gh pr view <n or branch> --json number,state,headRefName`.
- Stop unless `state` is `MERGED`.
- Stop if another open PR targets this branch (`gh pr list --base '<branch>'`); it has to be retargeted first.
- In the worktree, stop and show what would be lost if `git status --porcelain` lists anything besides this work's untracked plan file (step 2 handles that), or if local commits aren't in the PR: run `git fetch origin pull/<n>/head`, then `git cherry -v FETCH_HEAD HEAD`; a line starting with `+` is a commit the PR doesn't contain. This comparison still works when the branch was rebased on GitHub.

## 2. Plan file

If an untracked plan in `.claude/plans/` (worktree or main checkout) belongs to this work, check that its listed changes are in master. Delete it if they all landed; otherwise trim it to what's left and say so. A trimmed plan in the worktree moves to the main checkout's `.claude/plans/` before step 3 removes the worktree. A plan still tracked in master needs its own change; report it instead.

## 3. Worktree

- If this session entered the worktree with `EnterWorktree`, leave it first with `ExitWorktree` and `keep`.
- `git -C <main> worktree remove --force <worktree>`
- On `Filename too long`: `powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '<worktree>' -Recurse -Force"`, then `git -C <main> worktree prune`.
- If the folder is in use (a terminal or session is still open in it), finish the other steps and tell the user which folder is left.

## 4. Branches

- Local: `git -C <main> branch -D '<branch>'`. Squash merges leave the branch's commits out of master, so `-d` refuses.
- Remote: from the main checkout (`cd <main>` first; the current directory may be the removed worktree), if `git ls-remote --heads origin '<branch>'` lists it, delete it with `gh api -X DELETE 'repos/{owner}/{repo}/git/refs/heads/<branch>'`. `git push --delete` is blocked there by the `block-master` hook while master is checked out.

## 5. Update master

If the main checkout is on master: `git -C <main> pull --ff-only`. If git refuses because of local changes, report it; never stash or reset.

Report one line per step: what was removed and what is left.
