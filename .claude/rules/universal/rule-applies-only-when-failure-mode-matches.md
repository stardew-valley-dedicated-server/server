# A rule's license extends only as far as its own incident — re-read the `**Why:**` before invoking one

Every rule in `.claude/rules/` was written against a specific failure. Its authority covers that failure mode, not its title's literal reading and not whatever it can be stretched to permit. Before citing one — to justify a design, to explain a mistake, or to license stopping early — re-read its `**Why:**` and confirm your case has the same mechanism, not merely the same surface shape.

Three shapes, all seen in practice:

- **Design justification.** `orthogonal-fields.md` was cited to justify a seven-site field split when the real failure was one it explicitly does not cover — see that rule's "When this rule does NOT apply" section, which narrates the incident.
- **Self-diagnosis under challenge.** `answer-then-stop.md` was reached for to explain a mid-task error as over-tooling — when the actual complaint was the opposite: stopping short of a demanded verification. The apology inverted the instruction, licensing *less* thoroughness; a confident account of your own mistake is not evidence it is right.
- **Licensing an early stop.** `runtime-post-conditions-are-gates.md` permits naming an unrun check instead of silently implying full verification — a default for genuinely out-of-reach work, not a general hatch. Once the user has demanded completeness ("don't stop until fully confident"), a labelled residual is not a stopping point; reading two more files was always in reach, yet the hatch got used twice.

**Why:** Each case cost a full correction cycle; the second and third fire exactly when judgement is worst — under pushback, where the nearest quotable rule is the most available and least examined answer. Borrowing a rule's authority ends the thinking that should have happened.

**How to apply:** Before invoking a rule, ask "does my case share the incident's mechanism, or only its shape?" — and when the citation would excuse something (a stop, a scope cut, an error you're explaining), raise the bar rather than lower it: state the mechanism you're matching, out loud, so the mismatch is visible. Where the user has explicitly demanded completeness, no rule licenses a residual; finish or say plainly that you have not. Distinct from `verify-claims.md`, which guards against fabricated claims; this guards against over-applying internally-correct rules to cases they do not cover.
