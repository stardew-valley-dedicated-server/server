---
paths:
  - ".claude/**/*.md"
  - "CLAUDE.md"
---

# Answer a bot's "this identifier doesn't exist" with the local citation — `decompiled/` is invisible to it

The automated PR reviewers read this repo's `.claude/` content, but they only see **tracked** files. `decompiled/sdv-*/` is gitignored, so a reviewer running `git ls-files` for `Client.cs` or `FarmhandMenu.cs` finds nothing and may conclude from a web search that the symbol isn't real game code. Treat that verdict as a structural false negative, verify the citation against the local tree, and reply with the `file:line` rather than weakening the rule.

Two further consequences worth expecting:

- **A dismissed finding resurfaces when its surrounding line changes.** Editing a line outdates the thread where a rejection was recorded, and the next scan re-raises it against the new revision. Re-state the rejection rather than assuming the earlier dismissal still stands.
- **A reviewer can be right about an internal contradiction while being wrong about the code.** One correctly identified that "any non-empty type-9" conflicted with "always builds a non-null list" — and picked the right resolution — without being able to read either source file. Judge the reasoning, not whether its evidence-gathering succeeded.

**Why:** Sustained bot review of a rules PR produced both failure directions. A reviewer declared `FarmhandMenu.checkListPopulation` "not part of the base game" after `git ls-files` came back empty; the method exists and the rule's claim about it was sound. In the same review, a purely textual contradiction catch landed a genuine defect that had been carried, unverified, through a rule merge.

**How to apply:** When a reviewer disputes a decompiled-source citation, open the path under `decompiled/` (from the main checkout — worktrees don't have it, per `git-workflow.md`), confirm the symbol, and answer with the line reference plus a note that the tree is gitignored. Reserve rule edits for findings that survive that check.
