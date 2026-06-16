#!/usr/bin/env bun
// Portable test-observability queries over TestResults/, callable from any shell
// (PowerShell, cmd, bash). Replaces a family of bash+jq+python Makefile recipes that
// only ran under Git Bash. Invoked via `make test-*`; each subcommand mirrors one
// target. The latest run is resolved once here so subcommands don't reimplement it.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { gunzipSync } from "node:zlib";

const RESULTS = "TestResults";

// process.argv is [bunExecutable, scriptPath, subcommand, ...subcommandArgs].
const SUBCOMMAND_INDEX = 2;
const FIRST_ARG_INDEX = 3;

// Output caps inherited from the original Makefile recipes.
const FLAKY_TAIL_LINES = 2000; // recent flakiness records to print
const RECENT_FAILURES = 5; // failure_context events to show for triage
const CONTAINER_LIST_LIMIT = 20; // container names to suggest when one isn't found

// One per-test timing record in flakiness.jsonl (one line, one test, one run).
// testBodyMs is absent on non-passed records (e.g. canceled), so it's optional.
interface FlakinessRecord {
    runId: string;
    test: string;
    result: string;
    durationMs: number;
    testBodyMs?: number;
}

const SORT_KEYS = ["n", "p50", "p90", "max", "total"] as const;
const TIMING_FIELDS = ["durationMs", "testBodyMs"] as const;
type SortKey = (typeof SORT_KEYS)[number];
type TimingField = (typeof TIMING_FIELDS)[number];

interface TestStats {
    test: string;
    n: number;
    p50: number;
    p90: number;
    max: number;
    total: number;
}

function die(message: string): never {
    console.error(message);
    process.exit(1);
}

// Resolve the latest run the way the old Makefile did: latest.txt wins; otherwise
// the newest runs/* directory that holds a summary.json (an aborted run has none).
function latestRun(): string {
    const pointer = join(RESULTS, "latest.txt");
    if (existsSync(pointer)) {
        const path = readFileSync(pointer, "utf8").trim();
        if (path && existsSync(path)) {
            return path;
        }
    }

    const runsDir = join(RESULTS, "runs");
    if (existsSync(runsDir)) {
        const candidates = readdirSync(runsDir)
            .map((name) => join(runsDir, name))
            .filter((dir) => statSync(dir).isDirectory() && existsSync(join(dir, "summary.json")))
            .sort()
            .reverse();
        if (candidates.length > 0) {
            return candidates[0];
        }
    }

    return die("No test runs found. Run tests first.");
}

// Parse the subcommand's `KEY=value` arguments over the defaults.
function parseArgs<T extends Record<string, string>>(defaults: T): T {
    const parsed: Record<string, string> = { ...defaults };
    for (const argument of process.argv.slice(FIRST_ARG_INDEX)) {
        const separator = argument.indexOf("=");
        if (separator > 0) {
            const key = argument.slice(0, separator);
            const value = argument.slice(separator + 1);
            parsed[key] = value;
        }
    }
    return parsed as T;
}

function readJsonl<T>(path: string): T[] {
    return readFileSync(path, "utf8")
        .split("\n")
        .filter((line) => line.trim())
        .map((line) => JSON.parse(line) as T);
}

function printJsonFile(path: string): void {
    console.log(JSON.stringify(JSON.parse(readFileSync(path, "utf8")), null, 2));
}

// Render a grid of rows as a left-aligned table, padding each column to its widest cell.
function renderTable(rows: (string | number)[][]): string {
    const header = rows[0];
    const columnWidths = header.map((_, column) => Math.max(...rows.map((row) => String(row[column]).length)));
    return rows
        .map((row) => row.map((cell, column) => String(cell).padEnd(columnWidths[column])).join("  "))
        .join("\n");
}

// Narrow a CLI argument to one of its allowed values, or exit with a clear message.
function requireOneOf<T extends string>(name: string, value: string, allowed: readonly T[]): T {
    if ((allowed as readonly string[]).includes(value)) {
        return value as T;
    }
    return die(`Invalid ${name}=${value}. Allowed: ${allowed.join(", ")}`);
}

// Parse a CLI argument as a positive integer, or exit with a clear message.
function requirePositiveInt(name: string, value: string): number {
    const parsed = Number(value);
    if (!Number.isInteger(parsed) || parsed < 1) {
        return die(`Invalid ${name}=${value}. Expected a positive integer.`);
    }
    return parsed;
}

// Nearest-rank percentile over an already-ascending array. `quantile` is 0.5, 0.9, etc.
// The median uses `length` rather than `length - 1` to match the original jq index math
// the table output was validated against, so the two implementations never diverge.
function percentile(values: number[], quantile: number): number {
    const rank = quantile === 0.5 ? values.length * quantile : (values.length - 1) * quantile;
    return values[Math.floor(rank)];
}

const commands: Record<string, () => void> = {
    summary() {
        const run = latestRun();
        const file = join(run, "summary.json");
        if (!existsSync(file)) {
            die(`No summary.json in ${run} (run may have been aborted).`);
        }
        printJsonFile(file);
    },

    metadata() {
        const run = latestRun();
        const file = join(run, "run-metadata.json");
        if (!existsSync(file)) {
            die(`No metadata in ${run}.`);
        }
        printJsonFile(file);
    },

    "infra-log"() {
        const run = latestRun();
        const file = join(run, "diagnostics", "infrastructure.jsonl");
        if (!existsSync(file)) {
            die(`No infrastructure log in ${run}.`);
        }
        process.stdout.write(readFileSync(file, "utf8"));
    },

    events() {
        const { TEST } = parseArgs({ TEST: "" });
        if (!TEST) {
            die("Usage: make test-events TEST=ClassName.MethodName[(arg=value)]");
        }
        const run = latestRun();
        const file = join(run, "diagnostics", "infrastructure.jsonl");
        if (!existsSync(file)) {
            die(`No infrastructure.jsonl in ${run}.`);
        }
        for (const event of readJsonl<{ test?: { displayName?: string } }>(file)) {
            if ((event.test?.displayName ?? "").includes(TEST)) {
                console.log(JSON.stringify(event));
            }
        }
    },

    diagnose() {
        const run = latestRun();
        const file = join(run, "diagnostics", "infrastructure.jsonl");
        if (!existsSync(file)) {
            die(`No infrastructure log in ${run}.`);
        }
        const failures = readFileSync(file, "utf8")
            .split("\n")
            .filter((line) => line.includes('"event":"failure_context"'))
            .slice(-RECENT_FAILURES);
        for (const line of failures) {
            console.log(JSON.stringify(JSON.parse(line), null, 2));
        }
    },

    "container-log"() {
        const { CONTAINER } = parseArgs({ CONTAINER: "" });
        if (!CONTAINER) {
            die("Usage: make test-container-log CONTAINER=server-0|client-0|steam-auth-shared|steam-auth-per-N");
        }
        const run = latestRun();
        const dir = join(run, "containers", CONTAINER);
        const plain = join(dir, "container.log");
        const gzipped = join(dir, "container.log.gz");

        if (existsSync(plain)) {
            process.stdout.write(readFileSync(plain, "utf8"));
            return;
        }
        if (existsSync(gzipped)) {
            process.stdout.write(gunzipSync(readFileSync(gzipped)).toString("utf8"));
            return;
        }

        console.error(`No container.log for ${CONTAINER} in ${run}.`);
        const containers = join(run, "containers");
        if (existsSync(containers)) {
            console.error(readdirSync(containers).slice(0, CONTAINER_LIST_LIMIT).join("\n"));
        }
        process.exit(1);
    },

    flaky() {
        const file = join(RESULTS, "flakiness.jsonl");
        if (!existsSync(file)) {
            die("No flakiness data. Run tests multiple times first.");
        }
        const lines = readFileSync(file, "utf8")
            .split("\n")
            .filter((line) => line.trim());
        console.log(lines.slice(-FLAKY_TAIL_LINES).join("\n"));
    },

    slowest() {
        const args = parseArgs({ N: "15", SORT: "total", FIELD: "durationMs", LASTRUNS: "20", MINRUNS: "3" });
        const limit = requirePositiveInt("N", args.N);
        const lastRuns = requirePositiveInt("LASTRUNS", args.LASTRUNS);
        const minRuns = requirePositiveInt("MINRUNS", args.MINRUNS);
        const field: TimingField = requireOneOf("FIELD", args.FIELD, TIMING_FIELDS);
        const sort: SortKey = requireOneOf("SORT", args.SORT, SORT_KEYS);

        const file = join(RESULTS, "flakiness.jsonl");
        if (!existsSync(file)) {
            die("No flakiness data. Run tests multiple times first.");
        }
        const records = readJsonl<FlakinessRecord>(file);

        // Keep only the most recent LASTRUNS runs (the file is append-chronological).
        const runOrder: string[] = [];
        const seenRuns = new Set<string>();
        for (const record of records) {
            if (!seenRuns.has(record.runId)) {
                seenRuns.add(record.runId);
                runOrder.push(record.runId);
            }
        }
        const recentRuns = new Set(runOrder.slice(-lastRuns));

        // Collect passing durations per test within the window.
        const durationsByTest = new Map<string, number[]>();
        for (const record of records) {
            if (record.result !== "passed" || !recentRuns.has(record.runId)) {
                continue;
            }
            const value = record[field];
            if (typeof value !== "number") {
                continue;
            }
            const durations = durationsByTest.get(record.test) ?? [];
            durations.push(value);
            durationsByTest.set(record.test, durations);
        }

        const stats: TestStats[] = [];
        for (const [test, durations] of durationsByTest) {
            if (durations.length < minRuns) {
                continue;
            }
            const ascending = durations.slice().sort((a, b) => a - b);
            stats.push({
                test: test.replace(/^JunimoServer\.Tests\./, ""),
                n: ascending.length,
                p50: percentile(ascending, 0.5),
                p90: percentile(ascending, 0.9),
                max: ascending[ascending.length - 1],
                total: ascending.reduce((sum, value) => sum + value, 0),
            });
        }
        stats.sort((a, b) => b[sort] - a[sort]);

        const rows: (string | number)[][] = [["TEST", "n", "p50ms", "p90ms", "maxms", "totalms"]];
        for (const stat of stats.slice(0, limit)) {
            rows.push([stat.test, stat.n, stat.p50, stat.p90, stat.max, stat.total]);
        }
        console.log(renderTable(rows));
    },
};

const subcommand = process.argv[SUBCOMMAND_INDEX];
const command = commands[subcommand];
if (!command) {
    die(`Unknown subcommand: ${subcommand ?? "(none)"}. Known: ${Object.keys(commands).join(", ")}`);
}
command();
