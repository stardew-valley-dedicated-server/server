# LLM-driven affected-test selection for the E2E suite

## Context

The E2E suite has ~76 test methods across 29 classes; a full run is slow and serialized on one VPS. Today the only way to run a subset is a hand-typed `FILTER=` substring (`make test FILTER=...` locally; `/run-tests-e2e <filter>` comment or a dispatch input in CI). We want **automatic** selection: given a git diff, run only the tests the change could plausibly break.

**Why static mapping is out.** Tests are E2E black-box — every test asserts against the running server's HTTP API (`/cabins`, `/players`, `/farmhands`), so there is no compile-time call graph from a test to the mod service it exercises (per `tests-assert-via-http-api.md`). The mod is one-image-one-binary: any `mod/**` change rebuilds the whole image, so the import graph can't distinguish a CabinManager change from a CropSaver one. And Stardew mechanics are deeply coupled — a `GameLoader` save-flow change plausibly endangers cabins, farmhands, and save-import at once. No sound static map exists.

**Why LLM-only (no hand-maintained layers).** Earlier drafts proposed a `[Touches(...)]` attribute on every test class plus a hardcoded "cross-cutting path → run all" tripwire. Both were rejected as maintenance debt that silently rots (29 tags to keep in sync; a second decision-maker competing with the model). The model decides full-vs-subset itself — including "run everything" when the diff touches harness/infra code — from the same inputs. One source of truth.

### The reframing that makes this small

The entire feature reduces to one pure function: **`git diff → "ClassA|ClassB|ClassC"`** (or empty → full suite). Everything downstream already exists and is verified:
- `TestFilter.Matches` (`tests/JunimoServer.Tests/Infrastructure/TestFilter.cs:33`) — case-insensitive substring match on class FullName or `{Class}.{Method}`, `|`-separated, OR'd.
- `Program.cs:24` `ParseFilter` reads `--filter`/`--filter=` (def at `Program.cs:1038`); `Program.cs:53-56` propagates it as `SDVD_TEST_FILTER`.
- `ServerConfigDiscovery.DiscoverRequiredConfigs(methodFilter:)` (`ServerConfigDiscovery.cs:301`) applies the **same** predicate, so a class-name filter scopes server **prestart sizing** too — we skip booting the ~41s servers those tests would've needed, not just the test execution. That prestart saving is where most of the wall-clock win is.

So the selector just produces that string and hands it to the existing `--filter` path. No new filter mechanism, no changes to xUnit/broker plumbing.

### Fixed decisions (from the user — design to these)
1. **LLM-only.** No `[Touches]` attribute, no hardcoded tripwire. The model is the sole decision-maker.
2. **Lives in the C# TestRunner.** It is runner logic; reuse the runner's existing reflection-over-the-test-assembly pass.
3. **Provider-agnostic interface; free Gemini Flash as the default.** The user accepts that free-tier Gemini may train on the submitted diffs ("it's code-change summaries"). The interface keeps the privacy decision a config swap (→ paid Gemini / Vertex / Groq / self-hosted) rather than a rewrite.
4. **No gating today → no risk subsystem.** The suite is manual-trigger only, so a wrong pick just means a manual full re-run — same as today. No shadow-mode / backtest in v1; selection quality is proven during build by dry-running the prompt against real diffs on a Claude Haiku baseline.
5. **Catalog from existing XML doc comments.** Test classes are already richly documented (class `<summary>` names feature + endpoints + config); extract that, don't add new per-test metadata.

### Engineering principle: make the model's job easy so a cheap model suffices

Accuracy and cost are dominated by **pre-processing**, not model choice. Three levers:
- **Cached test catalog** so the model never reads test code.
- **Diff pre-summary** (changed files + changed symbols) so the model sees intent, not raw-diff noise.
- **Bounded scored-classification framing** with forced JSON output (a task a small model does reliably), biased to over-include (a false-positive runs one extra test; a false-negative skips a broken one).

Free-provider research (web, 2026-06-21): Gemini Flash free tier ≈ 1,500 req/day, 15 RPM, 1M TPM, no card, full JSON-Schema structured output; Groq Llama-3.3-70B ≈ 1,000 req/day, OpenAI-compatible. Both far exceed our few-requests-per-PR usage and both beat a small self-hosted 7-8B. Privacy caveat: free Gemini trains on inputs (paid/Vertex and reportedly Groq do not) — accepted per decision 3.

---

## Design

### New project area: `tests/JunimoServer.TestRunner/Selection/`

All new code lives here. Five pieces:

```
Selection/
  TestCatalog.cs          // builds + caches the catalog (reflection + doc-comment + body grep)
  TestCatalogEntry.cs     // record: ClassName, Summary, Endpoints[], ConfigFlags
  DiffSummary.cs          // git diff → changed files + changed symbols
  ISelectionModel.cs      // provider interface (one method)
  GeminiSelectionModel.cs // default impl (HttpClient → Gemini generateContent)
  AffectedTestSelector.cs // orchestrator: catalog + diff → calls model → filter string
```

### 1. `TestCatalog` — the cached, model-facing description of the suite

Reuse the reflection pass `ServerConfigDiscovery` already performs (`assembly.GetTypes()` → `TestBase` subclasses, read `[TestServer]`). For each class produce a `TestCatalogEntry`:
- **ClassName** — `type.Name` (the filter token).
- **Summary** — the class XML `<summary>`. Doc comments aren't in runtime metadata, so extract them at build time from the generated XML doc file. Add `<GenerateDocumentationFile>true</GenerateDocumentationFile>` to `JunimoServer.Tests.csproj`; the catalog reads `JunimoServer.Tests.xml` next to the test DLL and pulls each `T:JunimoServer.Tests.<Class>` member's `<summary>`. (Falls back to a thin "no summary" placeholder if absent — see Risks; this is where catalog quality lives.)
- **Endpoints** — deterministic regex over the class source file: `ServerApi\.(\w+)` calls and `POST /test/\w+` / endpoint string literals. Cheap, high-signal ("this test hits /cabins and /settings").
- **ConfigFlags** — from the merged `[TestServer]` attribute already resolved by the reflection pass: `WithSteam`, `Password != null`, `CabinStrategy`, etc.

**Caching.** Serialize the catalog to `TestResults/.cache/test-catalog.json`, keyed by a hash of (test-assembly mtime + the XML doc file mtime). Rebuild only on change. The catalog is ~3-5 lines/class × 29 ≈ ~1-2K tokens — small enough to send whole every call, no per-call rebuild cost.

### 2. `DiffSummary` — pre-chewed diff

Shell out to git (the runner runs in a checked-out repo both locally and on the VPS):
- **Changed files**: `git diff --name-only <base>...HEAD` (+ `git diff --name-only` for un-committed working-tree changes locally).
- **Changed symbols**: `git diff <base>...HEAD -U0`, parse hunk headers / changed lines for changed C# method/class names under `mod/JunimoServer/**`. Keep it simple — a regex for `(public|private|...)\s+...\s+(\w+)\s*\(` near changed lines; the goal is signal, not a parser.

**Base resolution** (the one local-vs-CI difference):
- **Local**: `git merge-base HEAD master` (the working branch's divergence point), plus uncommitted changes. New `--diff-base <ref>` flag overrides.
- **CI**: the workflow passes `--diff-base <base_sha>`. The gate already resolves the PR base — but to avoid a second filter-resolution site in the workflow JS (a `one-writer-per-artifact` smell), CI passes only the *base ref* and lets the runner compute the diff. Requires `fetch-depth: 0` (or fetch of the base) in the e2e checkout — currently unset (shallow); the plan adds a base fetch in the run step (see CI section).

### 3. `ISelectionModel` — the provider seam

```csharp
internal interface ISelectionModel
{
    // Returns per-class verdicts. Implementation forces structured JSON output.
    Task<SelectionResult> SelectAsync(
        IReadOnlyList<TestCatalogEntry> catalog,
        DiffSummary diff,
        CancellationToken ct);
}

internal record SelectionResult(
    bool RunAll,                       // model's explicit "this is broad, run everything"
    IReadOnlyList<ClassVerdict> Verdicts,
    string Rationale);                 // one paragraph, logged for operator insight

internal record ClassVerdict(string ClassName, bool Relevant, double Confidence, string Why);
```

The orchestrator turns this into a filter: `RunAll` → empty filter (full suite); else join `Verdicts.Where(v => v.Relevant && v.Confidence >= threshold).Select(v => v.ClassName)` with `|`. Threshold default ~0.3 (low — bias to inclusion).

### 4. `GeminiSelectionModel` — the default impl

- Plain `HttpClient` POST to `https://generativelanguage.googleapis.com/v1beta/models/<model>:generateContent?key=<key>`.
- API key from `SDVD_SELECTION_API_KEY` (env). Model from `SDVD_SELECTION_MODEL` (default e.g. `gemini-2.0-flash`). Both documented + consumer-grepped per `verify-documented-config-is-consumed.md`.
- Use Gemini's `responseSchema` / `responseMimeType: application/json` for forced structured output (verified supported on Flash).
- The **prompt** (system+user): the engineering core. Contains (a) the role ("select which E2E tests a code diff could break"), (b) the coupling guidance (save-load flow touches cabins/farmhands/import; auth touches lobby/password; "when the diff touches test harness / infrastructure / Dockerfile / build config, set runAll=true"), (c) the catalog, (d) the diff summary, (e) the output contract. Bias-to-include instruction explicit.
- On ANY model failure (network, quota, malformed) → **fail open: return `RunAll=true`** and log why. A selection error must never skip tests silently.

### 5. `AffectedTestSelector` — orchestrator

`Task<string?> ResolveFilterAsync(string? diffBase, ct)`: build/load catalog → build diff summary → if diff touches nothing relevant (e.g. only docs/`*.md`), short-circuit to empty-or-skip per a tiny deterministic pre-check (docs-only diff → the model would say runAll=false with everything irrelevant anyway, but skipping the call saves a request); → else call `ISelectionModel` → map to filter string. Emits a structured `selection_completed` event (chosen classes, rationale, confidence histogram, model, token usage) to `InfrastructureEventLog` for operator visibility.

### Wiring into `Program.cs`

A new mode/flag, resolved **before** `ParseFilter` at line 24:
- New flag `--select-affected` (optionally `--diff-base <ref>`). When present and no explicit `--filter` was given, the runner calls `AffectedTestSelector.ResolveFilterAsync(...)`, and the resulting string becomes `filter` exactly as if `--filter` had been passed. Everything after (the `SDVD_TEST_FILTER` propagation at line 53, prestart scoping) is unchanged.
- `--filter` always wins if both are given (explicit beats inferred).

### Makefile

New target mirroring the existing ones:
```make
test-affected:
	@dotnet run --project $(RUNNER_PROJECT) -- --select-affected $(if $(DIFF_BASE),--diff-base "$(DIFF_BASE)") $(if $(VERBOSE),--verbose)
```

### CI: a second sticky-comment checkbox

The selector runs in the runner, so CI does NOT resolve a filter for this path — it tells the runner to self-select:
- Add a second checkbox to the bot's sticky comment: **"Re-run E2E (affected tests only)"**, alongside the existing "Re-run E2E tests".
- In the gate JS (`e2e-tests.yml`, the `issue_comment: edited` branch ~line 248-256), detect which box was ticked; for the new box, set a new gate output `select_affected=true` (instead of resolving `filter`).
- In the "Run E2E suite" step (`e2e-tests.yml:560-575`), when `select_affected == 'true'`, invoke `dotnet run ... -- --select-affected --diff-base "$BASE_SHA"` (base from `gate.outputs` PR base) instead of the `--filter` form. Add `SDVD_SELECTION_API_KEY` from a new secret; add a base-ref fetch (the checkout is shallow today).

---

## Implementation steps

1. **Catalog plumbing.** Enable `<GenerateDocumentationFile>` in `JunimoServer.Tests.csproj`; add `TestCatalogEntry` + `TestCatalog` (reflection reuse, XML-doc `<summary>` read, endpoint grep, config flags, JSON cache keyed on assembly+xml mtime). Verify the produced `JunimoServer.Tests.xml` actually lands next to the DLL (`verify-edit-landed-in-artifact.md`).
2. **Diff summary.** `DiffSummary` + git invocation; base resolution (local merge-base, `--diff-base` override). Handle "not a git repo" / detached HEAD gracefully → fail open to full suite.
3. **Model interface + Gemini impl.** `ISelectionModel`, `SelectionResult`, `GeminiSelectionModel` with `responseSchema` JSON mode; env-var config; fail-open on every error path.
4. **Prompt.** Author the prompt; dry-run against ~5 real diffs (using a Claude Haiku impl of the same interface as a baseline) to tune coupling guidance + threshold before trusting Gemini. (Prove quality per decision 4.)
5. **Orchestrator + event.** `AffectedTestSelector`; `selection_completed` event (add to the event catalog per `event-catalog-no-inline-enums.md`).
6. **Program.cs wiring.** `--select-affected` / `--diff-base` parsing before line 24; explicit-`--filter`-wins precedence.
7. **Makefile.** `test-affected` target.
8. **CI.** Second sticky checkbox + gate `select_affected` output + run-step branch + `SDVD_SELECTION_API_KEY` secret + base fetch.
9. **Docs.** Document `SDVD_SELECTION_*` env vars and the new make target / checkbox; grep-confirm each documented knob has a consumer (`verify-documented-config-is-consumed.md`).

## Risks / failure modes (split per `adversarial-review-split-findings.md`)

- **Inherent, mitigated — selection error skips a real failure.** Every model/diff/git failure path fails OPEN (full suite). A selection error is never silently a skip. This is the load-bearing safety property; v1 has no gate anyway, so worst case = a manual full re-run.
- **Inherent — catalog quality == doc-comment quality.** A thin/missing class `<summary>` starves the model. Today they're rich. Mitigation: the catalog flags classes with no summary; a follow-up could lint for undocumented test classes. Not blocking for v1.
- **Inherent — image build still runs** for any `mod/**` change (one-image-one-binary). Selection saves test-execution + prestart, not the image build. Gains concentrate on test-only and single-service diffs. Stated, not fixable here.
- **Adjacent, in scope — privacy.** Free Gemini may train on submitted diffs (accepted per decision 3). The provider interface makes switching to a no-train provider a config change; documented as a known tradeoff, not hidden.
- **OOS (named) — gating policy + shadow-mode validation.** Deliberately deferred: the suite doesn't gate. When/if it does, add selected-vs-actual-failure logging before trusting the subset to gate. Not built in v1.

## Post-conditions (runtime gates per `runtime-post-conditions-are-gates.md`)

- `make test-affected` on a branch with a cabin-only change prints a `selection_completed` rationale and runs only cabin-ish classes (observe the actual filter + which servers prestart).
- A diff touching `tests/.../Infrastructure/**` yields `runAll=true` (full suite) from the model, with no hardcoded path rule.
- Killing the network / unsetting the API key → fails open to full suite, with a logged reason (not a crash, not a silent empty filter).
