# Discord RAG Search

Semantic search over the community's Discord history (forum bug reports + relayed chat), exposed as an
MCP tool for the maintainer's triage/dedup workflow.

**Prerequisite:** the bot-MCP module ([`discord-bot-mcp-module.md`](./discord-bot-mcp-module.md)) —
this rides on its MCP transport and `src/` restructure. Complements but doesn't depend on the triage
skills ([`discord-github-triage-skills.md`](./discord-github-triage-skills.md)): triage's dedup
searches only GitHub; this lets it also check whether the community already discussed a report.
Independent of the Discord-auth plan ([`server-discord-auth.md`](./server-discord-auth.md)) — no
shared code. `src/search/` + `src/mcp/` are v1; `src/commands/` only serves the deferred `/search`.

## Context

`tools/discord-bot/` is a `discord.js@14` gateway bot run via Bun, holding the bot token + gateway
connection + Message Content intent. It relays chat and reads the server HTTP API/WebSocket, but can
only touch history *by link* (fetch-by-ID / recent-N) — it can't answer "find everything about X" or
"has this been discussed before" across a corpus of **tens of thousands** of messages. That's the gap.

## Goal

A search module that indexes the Discord history and answers semantic queries, exposed as an MCP tool
`search_messages`. **v1 = the MCP tool only.** Retrieval is standalone (ranked messages + deep links,
no LLM); synthesis is the maintainer's own Claude at the MCP over the raw hits. A community `/search`
command and a local synthesis model are deferred (see Beyond v1).

## Hard constraint (resolved): free/local only, no runtime paid dependency

Indexing **and** inference use free local models — **no paid key, no per-token cost**. The module
must not call any paid API at runtime. Consequences:

- **No Anthropic embeddings** — the Claude API has no embeddings endpoint; embeddings come from a
  free/local model regardless.
- **No cloud embedding provider** (Voyage/Cohere/Jina/Gemini free tiers) — the constraint moots that
  choice; don't wire one.

## Retrieval and synthesis are separate layers

- **Retrieval** — embed query → vector lookup → ranked messages + deep links. No LLM; runs on any
  hardware. This is the module's floor and the input synthesis builds on.
- **Synthesis** — the only step wanting a chat LLM, never required for correct results. **v1:** the
  maintainer's own Claude at the MCP over the raw hits (no new infra). **Deferred:** a local model
  (7–8B behind a local OpenAI-compatible endpoint) for a caller not in a Claude session.

Synthesis lives strictly at the front-end over raw hits, so the deferred local tier is a config + thin
client later, not a redesign. No runtime paid dependency either way.

## Architecture

One search module (ingest + index + query) behind one v1 front-end (the `search_messages` MCP tool).
The query layer is a clean primitive; the deferred `/search` command and local-synthesis call compose
over it.

| Concern | What it is | Where |
|---|---|---|
| Ingest + index | Pull history → embed each message (local) → store vectors | `src/search/` background job |
| Query (retrieval) | Embed query (local) → nearest-neighbor lookup → dedup to one row per `messageId` → ranked messages + links. **No LLM — the standalone floor.** | `src/search/query.ts` (core primitive) |
| Synthesis (deferred) | Summarize retrieved messages into prose — **v1: the maintainer's Claude at the MCP over raw hits**; a local endpoint is a kept-compatible future option, not built now | front-end orchestration; local path → `src/search/synthesis.ts` when adopted |
| Exposure (v1) | `search_messages` MCP tool only | thin wrapper over the query fn |

## Stack (all free/local)

| Layer | Pick | Notes |
|---|---|---|
| Embeddings (index + query) | Local model via Transformers.js/ONNX — **default `all-MiniLM-L6-v2`** (~23M, ~90 MB, 384-dim, MTEB ~56, **256-token** max → see Ingest policy); `nomic-embed-text-v1.5` (~137M, int8 ~137 MB, 768-dim, MTEB ~62, 8k ctx) when long forum bodies need it — **but nomic requires task-prefixes** (`search_query:` / `search_document:`) | CPU-only, no GPU. Both ship as ONNX for Transformers.js (`@huggingface/transformers`, **v4**). Runs in-process, smoke-test-gated → **Where the models run**. |
| Vector store | **sqlite-vec** | SQLite extension → rides on a DB file, no separate server. Least moving parts at this corpus size. (LanceDB/hnswlib are for larger or edge-multimodal cases we don't have; brute-force scan is even viable at tens of thousands of vectors.) |
| Synthesis LLM | **v1: the maintainer's Claude at the MCP** over raw hits (no new infra). **Deferred:** a pluggable local chat model (target 7–8B quantized, e.g. Llama-3.1-8B / Qwen2.5-7B 4-bit ~4–5 GB RAM) behind a local OpenAI-compatible endpoint | Never required — raw retrieval is the floor. The deferred local client is model-agnostic (base-URL + model-name), a config swap not a code change — but it is **not built in v1** (no verified consumer). |

## Approach

- **Ingest as a batched background job**, off the gateway thread — and **fetch-bound, not
  embed-bound** (tens of minutes for a forum; see backfill scope). Backfill the forum + chat history
  once (resumable per-channel cursor), then keep current by consuming live messages the relay already
  sees (the bot has Message Content intent). Store `{messageId, guildId, channelId, authorId,
  sourceTimestamp, text}` + vector, plus the edit/delete bookkeeping fields (`revision`, `isCurrent`,
  `deletedAt`, `editedAt`) covered in "Message edits & deletions" below. The deep-link is derived from
  `guildId/channelId/messageId`, not stored.
- **Query = embed-then-lookup:** a single query-embed is fast, but keep it fully `await`ed off the
  gateway's hot path.
- **Module layout:** land under the bot-MCP module's restructure — `src/search/` (ingest, index,
  query) alongside `src/mcp/` (the tool registration that calls into it) and `src/bot/` (gateway),
  sharing the single `Client` + token. **One process for bot + retrieval:** indexing is a background
  job and queries are `await`ed off the hot path — so retrieval rides inside the gateway bot with no
  sidecar of its own *if the in-process embedder passes its Bun smoke test* (else the embedder alone
  moves to a sidecar; see event-loop caveat + resolved embedding decision).
- **`search_messages` returns** ranked messages with deep links + score (v1 is raw only — no
  synthesized answer). A `synthesize` flag is reserved at the front-end for the deferred local tier;
  in v1 the maintainer's own Claude synthesizes over the raw hits at the MCP. (`/search` Discord
  command → Beyond v1.)

## Event-loop caveat (load-bearing)

Bun is single-threaded and shares its loop with the gateway, so nothing heavy runs inline:

- Full-history indexing is a **background/batched job**, never inline on the gateway.
- Every query-embed + lookup is `await`ed off the hot path.
- Deferred synthesis runs in a *separate* HTTP endpoint (the bot only `await`s the response), with a
  timeout that falls back to raw results if it's slow or down.
- If search latency ever threatens the relay heartbeat, split the search module into its own process
  (still sharing the token) — the escape hatch, not a day-one design.

## Message edits & deletions

Track edits/deletions so the index stays truthful and history is inspectable — without letting N
revisions of one message out-vote a distinct message in the ranking.

**discord.js reality (verified against the installed `discord.js@14` typings):**
- Events exist: `Events.MessageUpdate` → `(oldMessage, newMessage)`, `Events.MessageDelete` →
  `(message)`, `Events.MessageBulkDelete` → `(messages, channel)`.
- These fire for messages **in the in-memory cache** by default. To receive them for messages that
  aren't cached — which includes **everything we backfilled from history** — the client must enable
  `Partials.Message` (add to the `partials: [...]` client option). The current bot enables no
  partials (`src/index.ts:129`), so this is a required client-config change.
- **Load-bearing caveat — `oldMessage` is unusable, `newMessage` is fine.** For a backfilled
  (uncached) message, an edit event's `oldMessage` is a *partial* — **`oldMessage.content` is
  `null`**, and a delete event gives only a partial (reliably just the `messageId`). But
  `newMessage` in a `messageUpdate` is a **full `Message`** (verified: `messageUpdate: [oldMessage:
  Message | PartialMessage, newMessage: Message]`), so the *new* text is always reliable. Consequence:
  we never need Discord's `oldMessage` — the "previous text" comes from **our own persisted rows**,
  and the new text comes from `newMessage`. Don't add a defensive `newMessage.fetch()`.

**Storage — two tables, because sqlite-vec can't enforce the invariants (verified).** A `vec0`
virtual table allows only a **single-column** key and enforces no uniqueness — so it cannot guarantee
"one `isCurrent` per message" or "no duplicate `(messageId, revision)`", the invariants every
unbiased-results guarantee rests on. Split the store:
- **`messages`** (plain SQLite): `rowKey` (synthetic PK) · `messageId` · `revision` (0-based) ·
  `isCurrent` · `editedAt`/`deletedAt` (nullable) · `sourceTimestamp` (Discord `editedTimestamp ??
  createdTimestamp`) · `text` · base metadata (`guildId`, `channelId`, `authorId`). The deep-link is
  derived, not stored — `https://discord.com/channels/{guildId}/{channelId}/{messageId}`. Constraints:
  **`UNIQUE(messageId, revision)`** and a **partial unique index `UNIQUE(messageId) WHERE
  isCurrent=1`** — these are the backstop that makes the races below detectable, not silent.
- **`vec0`** vector table keyed by `rowKey`, holding only the embedding. Queries join `vec0 → messages`
  on `rowKey` and filter on `messages`.

**"Current" is by newest `sourceTimestamp`, not by write order** — this is what makes backfill and
live events safe to run concurrently (backfill is a tens-of-minutes background job; edits arrive live
during it):
- **On `messageUpdate`:** if `newMessage.content` differs from the stored current `text`, insert a new
  revision (`isCurrent` set only if this event's `sourceTimestamp` is the newest known for the
  `messageId`), demote the prior current row, embed **only the new revision**. Identical-content
  updates (embed-only edits, pin changes, unfurls) change no text → **no new revision, no re-embed**
  (first edit-spam guard). Do the read-current → embed → insert+flip as: `await` the embed *first*,
  then run the row-swap in **one synchronous SQLite transaction** that re-reads current state — never
  hold "which row is current" across an `await` (Bun is single-threaded, so an un-yielded transaction
  can't interleave).
- **Edit/delete for a message backfill hasn't reached yet** (no row exists): seed revision 0 **from
  the edit event's own `newMessage`** (it's a full `Message`), mark current; a later backfill insert
  for the same `messageId` is a no-op via the uniqueness constraint (`INSERT OR IGNORE`).
- **On `messageDelete` / `messageBulkDelete`:** stamp `deletedAt=now` on **every row of that
  `messageId`**, not just the current one (tombstone the whole message). Keep rows + vectors — do
  **not** hard-delete (traceable deletes). Message-level tombstoning is what closes the
  `includeDeleted`-leak below.

**Keeping RAG results unbiased (the core requirement):**
- **Default search = `isCurrent=1 AND deletedAt IS NULL`**, applied to the current row, dedup *after*
  the filter. Historical revisions of one message can never each score a separate hit — one message =
  at most one result.
- **Tombstoned messages are excluded by default**; surfaced only behind an explicit
  `search_messages(..., includeDeleted: true)` flag, **always labeled deleted** (safe because delete
  stamps *all* revisions — no live non-deleted old revision can leak a deleted message as
  not-deleted).
- **De-dup at the `messageId` level even under `includeDeleted`:** collapse to the best-scoring
  revision before ranking, so opting into history yields one row per message, not a cluster; the
  deleted marker rides along regardless of which revision wins.

**`history(messageId)`** (cheap given the schema): return the ordered revisions + deletion state for
one message — a **non-semantic** ordered fetch (by `revision`), so it never embed-ranks historical
vectors. Surfaced as a small tool alongside `search_messages`.

## Ingest eligibility & embedding-length policy

Not every message is indexable, and the default model silently truncates — both must be decided, not
left implied:

- **Skip system messages** (`message.system === true`: pins, joins, boosts, thread-created) — no
  triage value.
- **Empty-`content` messages** (attachment-only, embed-only, stickers, polls): index a synthesized
  text (attachment filenames + embed titles) when one exists, else skip. Don't embed an empty string
  (low-information vector pollutes results). An edit that empties a message's content follows the same
  rule.
- **Bot/webhook authors:** the existing relay filters `author.bot` (`src/index.ts:521`), but that is
  chat-relay-specific. For the **search corpus**, index webhook/bot forum posts (some bug-report
  workflows post via webhook — real triage content); decide per-source rather than blindly copying
  the relay's filter.
- **Length / truncation (load-bearing for the triage use-case):** `all-MiniLM-L6-v2` truncates at
  **~256 tokens** — a 1000–3000-token forum bug report is silently embedded from its opening only, so
  "has this been discussed?" misses reports whose detail is in the body. Two acceptable resolutions,
  pick at build time: **(a)** use `nomic-embed-text-v1.5` (8k ctx) as the model *for the forum
  corpus* (accepts its task-prefix requirement — `search_document:` on index, `search_query:` on
  query), or **(b)** keep MiniLM but **window** over-long messages into overlapping chunks stored as
  sibling rows under the same `messageId` (a `chunkIndex` column; dedup already collapses them to one
  hit per message). Do **not** ship silent truncation of the flagship corpus.

## Files (sketch — finalize against the bot-MCP restructure)

| Path | What |
|---|---|
| `tools/discord-bot/src/search/db.ts` | **Owns the sqlite-vec handle**: opens the DB, loads `vec0`, runs the `{model_id, dim}` meta check + reindex-on-mismatch, exports the handle + prepared statements + the shared **`upsertRevision(messageId, text, {kind})`** primitive (embed → assign revision → flip current in one txn). Every writer routes through it — one write path, no drift. |
| `tools/discord-bot/src/search/index.ts` | Backfill driver: paginate Discord history (resumable per-channel cursor), apply ingest-eligibility, call `db.upsertRevision` per message. Idempotent (`INSERT OR IGNORE` on `(messageId, revision)`). |
| `tools/discord-bot/src/search/live.ts` | Live index maintenance for **new** messages (`messageCreate`) — the incremental path; routes through `db.upsertRevision`. (Distinct from backfill's one-shot driver.) |
| `tools/discord-bot/src/search/history.ts` | Edit/delete maintenance: `messageUpdate`/`messageDelete`/`messageDeleteBulk` → `db.upsertRevision`(edit) / message-level tombstone (see "Message edits & deletions"). |
| `tools/discord-bot/src/search/query.ts` | **Core retrieval primitive** `search(queryText, opts) → RankedHit[]`: embed query → nearest-neighbor → dedup to one row per `messageId` → rank. **No `synthesize` param, no synthesis import** — the LLM-free floor. |
| `tools/discord-bot/src/search/context.ts` | `expandContext(hit)` → hit + neighboring messages (by channel + timestamp). Separate from the core primitive so synthesis/dedup consumers can use raw ranked hits without neighbor-padding. |
| `tools/discord-bot/src/search/embeddings.ts` | Embedding client (Transformers.js/ONNX; in-process if the Bun smoke test passes, else a Node sidecar over HTTP) — loads the model once, embeds index + query, applies task-prefixes if the model needs them. |
| `tools/discord-bot/src/search/synthesis.ts` | **Deferred** — the optional local-synthesis client (local OpenAI-compatible endpoint, base-URL + model-name config). Not built in v1; the seam exists so it drops in without a redesign. |
| `tools/discord-bot/src/mcp/tools/search.ts` | v1 front-end: `search_messages` MCP tool over `query.ts` (+ `context.ts`). Orchestrates retrieval; the `synthesize` flag is handled **here at the front-end** (raw in v1; wires `synthesis.ts` when adopted), never pushed into `query.ts`. |
| `tools/discord-bot/src/bot/` | Owns the single `Client` construction (intents **+ `Partials.Message`**) and **all `client.on(...)` registrations**; search modules export handler fns (`onMessageCreate/Update/Delete`) that the bot layer wires — no module self-registers on the shared `Client`. Note: today `GuildMessages`/`MessageContent` are requested only when `DISCORD_CHAT_CHANNEL_ID` is set (`src/index.ts:125`); the search corpus needs them for its indexed channels (e.g. the forum) too, so that gate widens to "chat relay **or** search enabled". |
| `tools/discord-bot/package.json` | Add `sqlite-vec` (pin — pre-v1, 0.1.9) + the Transformers.js embedder (`@huggingface/transformers`, **v4**; `@xenova/transformers` is its older v2 name). Confirm the latest at build time. |

## Where the models run

- **Embedder — in-process (Transformers.js/ONNX in Bun), smoke-test-gated.** It's tiny (~90 MB,
  CPU-ms) and on the always-on query path, so in-process is the clean target — but `onnxruntime-node`
  has open segfault reports on Bun 1.3.x (the pinned image is `oven/bun:1.3.14`) with no WASM
  fallback, and a crash kills the gateway. **Gate:** smoke-test a MiniLM pipeline on the Bun image
  first. **Fallback if it crashes:** a Node embedder sidecar over HTTP — not free, since it adds a hop
  to every query, so take it only if the test forces it.
- **Synthesis (deferred) — a local OpenAI-compatible endpoint (Ollama/llama.cpp/LM Studio), never
  in-process.** A 7–8B model is ~4–5 GB RAM; it stays out of the bot process, agnostic/swappable via
  base-URL + model-name config. Not built in v1.
- **One process:** gateway + retrieval + the in-process embedder share one process; splitting the
  search module out is the escape hatch only if the relay heartbeat suffers (see event-loop caveat).

## Resolved decisions

- **Prerequisite** — the bot-MCP module lands first (shared transport + `src/` restructure). Who owns
  the `index.ts → src/{bot,mcp,commands}/` split is an **open cross-plan question**: the bot-MCP plan
  assigns the restructure to the auth plan, which this plan needs to stay independent of — pin it when
  the bot-MCP module is scheduled.
- **Embedder location** — in-process, smoke-test-gated; sidecar fallback only if it crashes (above).
- **Author identity is stored.** `authorId` + deep-link ride on every vector so hits trace to their
  source message (which implies the author anyway). Nothing beyond what the public channel shows; if a
  stricter policy is ever wanted, `authorId` is one column to hash/drop.
- **Backfill scope** — full history once, resumable (per-channel cursor), fetch-bound (see Approach).
  The incremental relay keeps it current after.
- **Chunking** — one row per message, keyed on `messageId`; no coalescing (edit/delete events are
  per-message, so a multi-message chunk couldn't be surgically updated). Short-line context is added
  at query time via `context.ts`, not baked into storage.
- **Backfill idempotency** — `UNIQUE(messageId, revision)` + `INSERT OR IGNORE` makes an accidental
  re-run a no-op instead of duplicate rows.
- **Reindex on model change** — vectors across models aren't comparable, so a swap invalidates the
  index. Store `{model_id, dim}`; on mismatch, re-embed from stored `text` (no re-fetch). Re-embed
  every semantically-rankable row — non-deleted currents **and** tombstoned currents (`includeDeleted`
  ranks them); pure `isCurrent=0` history may be skipped only while `history()` stays non-semantic.
- **Ingest eligibility** — skip system messages, synthesize-or-skip empty content, index webhook/bot
  forum posts, resolve MiniLM's 256-token truncation before shipping (see Ingest policy).
- **Unbounded growth** — revisions + tombstones grow without bound (never hard-deleted). No compaction
  in v1; if DB size / reindex time bites later, prune old `isCurrent=0` revisions (keep tombstones).

## Beyond v1 (kept-compatible)

Not built in v1 — no consumer for them yet — but the design stays shaped so adopting either is a
config + thin client, not a redesign. Each needs a named consumer (and, for `/search`, community
demand) first.

- **Community `/search` command** — a public front-end over `query.ts`. Raw hits are poor UX for a
  member wanting an *answer*, so its useful mode is synthesis-on (the deferred tier), plus it adds a
  public command surface (rate-limit, abuse, permissions).
- **Local synthesis** — a 7–8B model behind a local OpenAI-compatible endpoint (`SYNTHESIS_BASE_URL` /
  `SYNTHESIS_MODEL`, unset → raw), swappable. v1 synthesis is the maintainer's Claude at the MCP.

## Confirm before building (cheap gates)

- **Corpus size** — "tens of thousands" is an estimate; count the real number (the bot has the intents
  for it). If it's a few thousand, reconsider whether semantic search beats keyword search at all.
- **The premise** — that dedup-relevant duplicates live in *Discord*, not just GitHub (which triage
  already searches). The maintainer answers this from experience. If yes, wire `find-related-issue` to
  query this index too, so the dedup workflow reaches the tool automatically.
- **Bun embedder smoke test** — in-process vs sidecar hinges on it (see Where the models run).

## Verification (when built)

- MCP `search_messages` resolves via ToolSearch as `mcp__discord__search_messages` and returns
  ranked messages + working deep links for a known query (e.g. a symptom string from a filed report).
- Re-running the same query is deterministic and hits the persisted sqlite-vec index (no re-embed of
  the corpus).
- Indexing the full history does **not** stall the chat relay (heartbeat steady during backfill).
- **Edited message:** edit an indexed message; a re-query for its new wording ranks it, a query for
  its old wording no longer surfaces it as current, and `history(messageId)` shows both revisions.
- **Edit-spam is not over-weighted:** a message edited N times yields **one** result, not N — default
  search returns at most one row per `messageId`.
- **Concurrent backfill + live edit:** an edit arriving for a message backfill hasn't reached yet
  produces one correct current row (seeded from `newMessage`), and a re-run of backfill inserts no
  duplicate (`UNIQUE(messageId, revision)` holds).
- **Deleted message:** delete an indexed message; it drops out of default results (no old revision
  leaks it as not-deleted), appears **labeled deleted** under `includeDeleted: true`, and its
  rows/vectors remain for tracing.
- **Long forum post is fully searchable:** a >256-token bug report is findable by a phrase from its
  *body*, not just its opening (confirms the truncation resolution landed).
- **Retrieval is standalone:** `search_messages` returns correct ranked messages + working deep links
  with **no** chat LLM — local or remote — contacted (v1 has no synthesis call at all).
- No **paid/cloud** API key is required to index or query — grep the module for cloud hosts
  (`api.anthropic.com`, `api.openai.com`, `voyage`, `cohere`, `jina`); zero runtime hits. The `.env`
  needs no key beyond the existing `DISCORD_BOT_TOKEN`. (A **local** synthesis base-URL is a deferred,
  non-cloud config that doesn't exist in v1.)
