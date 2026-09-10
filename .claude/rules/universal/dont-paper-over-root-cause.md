# Don't paper over a root cause — not in time (retries) and not in space (file/container isolation)

A retry loop, backoff, "transient error" classifier, or swallow-and-continue `try/catch` papers over a root cause in *time*; a per-container tmpfs, randomized volume path, or isolated mount papers over one in *space*. Both are symptoms of an unfixed underlying failure, not the correct shape of the code. Before extending either — adding an error type, raising an attempt count, isolating another mount — find the root cause and fix it at source, or prove it is genuinely outside your control and costs more to fix than the band-aid.

## In time — retry loops and fallbacks are evidence, not infrastructure

When you meet a retry loop, exponential backoff, transient-error classifier, or a catch-and-continue, treat it as a symptom: ask what underlying failure it papers over and whether that can be fixed at source. Only extend the band-aid if (a) the root cause is provably outside our control (third-party API, real network flake) and (b) fixing it costs more than the retry; otherwise fix the root cause and delete the band-aid.

**Why:** E2E tests intermittently failed at container creation and the instinct was to copy `ClientPool.CreateClientGuardedAsync`'s 3× retry server-side — but the root cause, found only after "no bandaids" pushback, was Testcontainers 4.3.0's default 100ms `namedPipeConnectTimeout` failing under parallel-startup pressure on Windows, fixed by 6 lines of `.WithDockerEndpoint(...)` and *deleting* the retry.

**How to apply:** Read the surrounding comments and commit message for *why* the retry exists; if the reason names a symptom but not a root cause ("under load", "sometimes flaky", "transient"), that's the tell — investigate before adding code. Prompt yourself: "if I fixed the underlying problem, would this retry become dead code?" If yes, fix the problem. The retry's existence hints the root cause was known to a past contributor but not pursued — don't repeat that trade-off without revisiting it.

## In space — file/container isolation can't move a protocol-enforced invariant

When a failure is caused by an invariant enforced by a remote protocol or external authority (Steam's "one live login per account", a database unique constraint, a cluster's leader election, a shared TCP port), a fix that operates only at the filesystem, container, or process layer is theatre — copying state into a per-container tmpfs, randomizing volume paths, isolating mounts — none of it helps when the invariant is enforced *above* your code. Eliminate the trample at the enforcement layer (disjoint identifiers, fail-fast detection, accept the constraint by design) or accept it.

**Why:** Bug-4's first attempt copied the operator's `session.json` into a per-container tmpfs so each test sidecar had its "own" file — but SteamKit2's `LogonSessionReplaced` fires when any two clients log in with the same Steam *account*, whichever file carried the token ("JUST NO, IF SOMETHING TRAMPLES, WE NEED TO ELIMINATE THE TRAMPLE"); the fix was disjoint accounts plus fail-fast overlap detection keyed on the account, not the credential's location.

**How to apply:** Identify the invariant's *enforcement layer* first. (1) Is the failure produced by code in this repo, or by an external authority (cloud API, kernel, remote daemon, network protocol)? (2) Would it still happen if every file path, mount, and container were swapped for fresh ones? (3) Does the invariant key on an *identifier* (username, port, leader-id, primary key) rather than a *storage location*? If any answer points to "external authority enforcing on an identifier," file-level isolation is theatre — use disjoint identifiers or detect overlap before the second client even tries.
