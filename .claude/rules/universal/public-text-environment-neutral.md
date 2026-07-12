# Repo-public text is environment-neutral

Commit messages, PR descriptions, and docs describe the change or behavior generically — no personal-setup narrative (which machine, host, or container runtime hit the problem), and no niche third-party tool names unless the text is specifically instructions for that tool.

**Why:** Two corrections in one session. A commit/PR removing GUI components was first framed around the discovery environment (ARM host, the specific container runtime); the user reframed it as plain "optimization/cleanup of non-essentials". Then docs guidance for `socketPath` named a niche macOS runtime; the user: generalize to Docker — "other tooling is implicit, or at each user's own effort". Repo-public text outlives the incident and the maintainer's personal stack; framing around them makes it parochial and stale.

**How to apply:** Before publishing commit/PR/docs text, strip the discovery story (who hit it, on what machine, under which runtime) and state the generic mechanism instead ("under emulation", "daemons listening elsewhere"). Name a third-party tool only when the sentence is an instruction about that tool (e.g. a Docker Desktop settings path in a troubleshooting runbook). Complements `no-refactor-history-in-code.md` (no change-history in code); this covers environment/incident neutrality in public-facing text.
