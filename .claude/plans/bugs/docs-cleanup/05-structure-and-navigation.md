# 05 — Structure, Navigation & Deduplication

**Objective:** One canonical home per topic, sidebars/index pages in sync, no dead links, and the
contributor README out of the published site. Execute AFTER (or together with) the content fixes in
01–04 — several items below decide *where* those fixes land.

**Scope:** `docs/.vitepress/config.ts`, all index pages, cross-page duplication. This plan owns
placement; content correctness stays with plans 01–04.

**Verification gate:** `make docs` build clean (VitePress flags dead links unless ignored — check
`ignoreDeadLinks` in config.ts and report its setting); a scripted relative-link check over
`docs/**/*.md` passes; every sidebar entry resolves; every index page lists exactly its sidebar's
pages.

---

## Canonicalization (Medium — these prevent the drift class behind many plan-01/02 bugs)

### S1. Chat commands are documented in 3 places and have already drifted
- **Copies:** `players/commands.md` (general + !login), `admins/operations/commands.md:122-180`
  (re-lists general table, !login, partial !lobby), `features/password-protection/commands.md`
  (full !login/!authstatus/!lobby). The contradictory `!cabin` descriptions (admin copy wrong,
  player copy right) are the direct symptom.
- **Fix:** one canonical home per command class — player chat commands → `players/commands.md`;
  admin chat + console/CLI → `admins/operations/commands.md`; lobby-editing →
  `features/password-protection/commands.md`. Replace duplicated tables with one-line pointers
  (the admin page already does this correctly for lobby customization at :168).

### S2. `installation.md` steps 4-6 duplicate `first-setup.md` nearly verbatim
- Both cover `steam-auth setup`, invite-code retrieval via attach-cli/`info`, and the Co-op connect
  steps (`installation.md:32-66` vs `first-setup.md:11-43`). A sidebar reader gets the same
  instructions twice in a row.
- **Fix:** installation.md ends at "server started" and links forward; first-setup.md owns Steam
  Guard, verification, invite code, connecting (or fold first-setup's unique content into
  installation and drop the page + config.ts entry). Exactly one copy of the connect instructions.

### S3. Multi-host setup duplicated between `e2e-testing.md` and `remote-host-setup.md`
- Consolidation direction decided in plan 03 M6 (remote-host-setup.md owns schema/provisioning/SSH;
  e2e keeps concepts + cross-link). Tracked here because it's an IA change; content corrections
  ride with plan 03.

### S4. Admin↔developer twin pages share facts with zero cross-links
- Pairs: `admins/operations/networking.md` ↔ `developers/architecture/networking.md` (ports table,
  netdebug); `admins/configuration/environment.md` ↔ `architecture/steam-auth.md` env tables;
  `features/cross-platform.md` ↔ `architecture/steam-auth.md`/`networking.md` invite-code flow.
- **Fix:** add "canonical reference" cross-links in both directions per pair; where one copy is a
  strict subset (netdebug commands), make the subset page point at the complete one instead of
  growing its own copy.

## Navigation & config (Low unless noted)

- **N1. `config.ts` players sidebar (118-128) is the only section without an Overview entry** —
  add `{ text: "Overview", link: "/players/" }` (admins :133, features :167, developers :213,
  community :270 all have one).
- **N2. No `srcExclude`** — `docs/README.md` is built into the public site as `/README.html` and
  indexed by local search (config.ts has no srcExclude; search.provider local :289-291). Add
  `srcExclude: ["README.md"]`. (Pairs with the README rewrite in plan 03 H5.)
- **N3. Index pages out of sync with sidebars** (sidebar renders alongside, so impact is
  consistency, not discoverability):
  - `developers/index.md` omits 4 sidebar pages: events-schema, remote-host-setup,
    ci-log-masking-runbook, client-manipulation-techniques (config.ts :233,:241,:244,:259-262).
  - `admins/index.md:18-23` omits modern-docker (config.ts:156) and the operations overview.
  - `community/index.md:11-16` omits its own contributing page — which is itself an 11-line pure
    pointer page; consider folding it into the index and dropping the page (update config.ts:274).
- **N4. `developers/index.md` says "6-step debugging procedure"** — runbook has 7 steps; fix
  together with plan 03 M12 (CLAUDE.md companion).

## Dead links & external references

- `community/getting-help.md` Discussions row (404 — repo has Discussions disabled) and
  `community/resources.md` `truman-world/puppy-stardew-server` (404) — fixes specified in plan 02
  M15/M16; tracked here for the link-check gate.
- `CONTRIBUTING.md:17` dead anchor `#ci-cd-pipeline` — plan 04 rider.
- After all plans land, run the relative-link sweep again — consolidations in S1–S3 move anchor
  targets.

## Homepage & landing copy (Low)

- `docs/index.md` cards: the "Cross-Platform" card means host OS ("Linux or Windows") — omits
  macOS (prerequisites.md:19 supports it via Docker Desktop) and collides with
  `features/cross-platform.md`, where the same term means Steam↔GOG cross-play. Retitle the host-OS
  card (e.g. "Runs Anywhere — Docker: Linux, Windows, macOS") and let "Cross-Platform" mean
  cross-play only. Lead the Password Protection card with authentication (lobby cabins are a
  sub-feature, not the headline).

## Explicitly NOT proposed

- No re-shuffling of the five top-level audiences (admins/players/features/developers/community) —
  the audit found the split itself sound; every misplacement found was page-level, handled above.
- No sidebar reordering beyond the listed entries — current grouping matched the audited reading
  paths (install → configure → operate → troubleshoot).
