---
name: handoff
description: Writes a self-contained handoff that lets a fresh session continue the current task without this conversation, covering the goal, where the work is, its state, decisions already made, next steps, and how to verify. Use when the user asks for a session handoff, a prompt for another session, or to continue in a fresh session.
argument-hint: [what the next session should do, e.g. implement, review, investigate]
---

# Session handoff

Purpose of the next session: `$ARGUMENTS` (default: continue this task).

## 1. Check the facts

Look up the branch, the worktree path, `git status`, `git log --oneline origin/master..HEAD`, the PR (`gh pr view --json number,state,url`), and the plan file, if any. Write only what you checked.

## 2. Write it

Use these sections and leave out empty ones:

- **Goal**: one or two sentences.
- **Where**: worktree path, branch, PR.
- **State**: what is done, committed, and pushed; what is uncommitted.
- **Decisions**: choices made in this session, one line each, so the next session doesn't reopen them.
- **Next**: the steps, in order.
- **Verify**: exact commands or test classes.
- **Constraints**: user instructions still in force, such as "don't push yet" or "the test runner is shared".

For a review handoff, describe what changed and why, not what to look for. The reviewer should come to it fresh.

## 3. Keep it lean

- State verified facts as facts, so the next session doesn't redo the work.
- No history of how you got here, no dead ends, no references to rule files (the next session loads them), and no branches or files it doesn't need.
- Plain English, no em dashes.

## 4. Output

Print the handoff as one block the user can copy. Write it to a file only when the user asks, as `.claude/plans/<topic>-handoff.md`, left untracked.
