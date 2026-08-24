// Stamps the issues shipped by a build with the image version that first contains their fix.
//
// Resolves the issues closed by the PRs in the commit range BASE_OID..HEAD_OID (via GitHub's own
// PR↔issue linkage), writes VALUE into the org Issue Field FIELD_ID (first value wins), and posts
// a human-readable "your fix shipped in <version>" comment once per channel (marker-deduped).
// Run with Bun; invoked by .github/actions/stamp-issue-versions.
import { appendFileSync } from "node:fs";
import { Octokit } from "@octokit/rest";

interface ChannelConfig {
    commentBody: (value: string, docsUrl: string) => string;
}

const DOCS_URL = "https://stardew-valley-dedicated-server.github.io/server/admins/operations/upgrading";

const CHANNELS: Record<string, ChannelConfig> = {
    preview: {
        commentBody: (value, docsUrl) =>
            `🧪 A fix for this issue is available in a **preview build**. Set \`IMAGE_VERSION=${value}\` ` +
            `(or \`IMAGE_VERSION=preview\` to always track the latest preview) and update — ` +
            `see [how to run preview builds](${docsUrl}#using-preview-builds).`,
    },
    release: {
        commentBody: (value, docsUrl) =>
            `🎉 This fix has been **released** in \`${value}\`. Set \`IMAGE_VERSION=${value}\` ` +
            `(or \`IMAGE_VERSION=latest\` to track the latest stable) and update — see [how to update](${docsUrl}).`,
    },
};

function requireEnv(name: string): string {
    const value = process.env[name];
    if (value === undefined || value === "") {
        throw new Error(`Missing required env var ${name}`);
    }
    return value;
}

const OWNER = requireEnv("OWNER");
const REPO = requireEnv("REPO");
const HEAD_OID = requireEnv("HEAD_OID");
const BASE_OID = requireEnv("BASE_OID");
const FIELD_ID = Number.parseInt(requireEnv("FIELD_ID"), 10);
const EXPECTED_FIELD_NAME = requireEnv("EXPECTED_FIELD_NAME");
const VALUE = requireEnv("VALUE");
const CHANNEL = requireEnv("CHANNEL");
const MAX_ISSUES = Number.parseInt(requireEnv("MAX_ISSUES"), 10);
const DRY_RUN = process.env.DRY_RUN === "true";

if (!Number.isInteger(FIELD_ID) || FIELD_ID <= 0) {
    throw new Error(`FIELD_ID must be a positive integer, got "${process.env.FIELD_ID}"`);
}
if (!Number.isInteger(MAX_ISSUES) || MAX_ISSUES <= 0) {
    throw new Error(`MAX_ISSUES must be a positive integer, got "${process.env.MAX_ISSUES}"`);
}
const channel = CHANNELS[CHANNEL];
if (!channel) {
    throw new Error(`CHANNEL must be one of ${Object.keys(CHANNELS).join(", ")}, got "${CHANNEL}"`);
}
const COMMENT_MARKER = `<!-- image-version-stamp:${CHANNEL} -->`;

const octokit = new Octokit({ auth: requireEnv("GH_TOKEN") });

async function withRetry<T>(label: string, fn: () => Promise<T>): Promise<T> {
    const maxAttempts = 4;
    for (let attempt = 1; ; attempt++) {
        try {
            return await fn();
        } catch (error) {
            const status = (error as { status?: number }).status ?? 0;
            const headers = (error as { response?: { headers?: Record<string, string> } }).response?.headers ?? {};
            const retryAfterSec = Number.parseInt(headers["retry-after"] ?? "", 10);
            // 403 is retryable only as rate limiting — primary (remaining=0) or secondary (retry-after).
            const rateLimited =
                status === 429 ||
                (status === 403 && (headers["x-ratelimit-remaining"] === "0" || !Number.isNaN(retryAfterSec)));
            if (attempt === maxAttempts || !(status >= 500 || rateLimited)) {
                throw error;
            }
            const delayMs = Number.isNaN(retryAfterSec) ? 2 ** attempt * 1000 : retryAfterSec * 1000;
            console.warn(`${label}: HTTP ${status}, retry ${attempt}/${maxAttempts - 1} in ${delayMs}ms`);
            await new Promise((resolve) => setTimeout(resolve, delayMs));
        }
    }
}

interface HistoryPage {
    repository: {
        object: {
            history: {
                nodes: Array<{
                    oid: string;
                    associatedPullRequests: {
                        nodes: Array<{
                            number: number;
                            closingIssuesReferences: {
                                nodes: Array<{ number: number; repository: { nameWithOwner: string } }>;
                                pageInfo: { hasNextPage: boolean };
                            };
                        }>;
                        pageInfo: { hasNextPage: boolean };
                    };
                }>;
                pageInfo: { hasNextPage: boolean; endCursor: string | null };
            };
        } | null;
    };
}

const HISTORY_QUERY = `
query ($owner: String!, $repo: String!, $headOid: GitObjectID!, $cursor: String) {
    repository(owner: $owner, name: $repo) {
        object(oid: $headOid) {
            ... on Commit {
                history(first: 100, after: $cursor) {
                    nodes {
                        oid
                        associatedPullRequests(first: 5) {
                            nodes {
                                number
                                closingIssuesReferences(first: 50) {
                                    nodes {
                                        number
                                        repository { nameWithOwner }
                                    }
                                    pageInfo { hasNextPage }
                                }
                            }
                            pageInfo { hasNextPage }
                        }
                    }
                    pageInfo { hasNextPage endCursor }
                }
            }
        }
    }
}`;

/** Walks HEAD_OID's history (exclusive of BASE_OID) and returns the closed same-repo issue numbers. */
async function resolveIssues(): Promise<{ issues: number[]; commitCount: number }> {
    const issues = new Set<number>();
    let commitCount = 0;
    let cursor: string | null = null;
    let baseReached = false;

    while (!baseReached) {
        const page: HistoryPage = await withRetry("history query", () =>
            octokit.graphql<HistoryPage>(HISTORY_QUERY, { owner: OWNER, repo: REPO, headOid: HEAD_OID, cursor }),
        );
        const history = page.repository.object?.history;
        if (!history) {
            throw new Error(`HEAD_OID ${HEAD_OID} is not a commit in ${OWNER}/${REPO}`);
        }

        for (const commit of history.nodes) {
            if (commit.oid === BASE_OID) {
                baseReached = true;
                break;
            }
            commitCount++;
            if (commit.associatedPullRequests.pageInfo.hasNextPage) {
                throw new Error(`Commit ${commit.oid} has more than 5 associated PRs — raise the page size`);
            }
            for (const pr of commit.associatedPullRequests.nodes) {
                if (pr.closingIssuesReferences.pageInfo.hasNextPage) {
                    throw new Error(`PR #${pr.number} closes more than 50 issues — raise the page size`);
                }
                for (const issue of pr.closingIssuesReferences.nodes) {
                    if (issue.repository.nameWithOwner === `${OWNER}/${REPO}`) {
                        issues.add(issue.number);
                    }
                }
            }
        }

        if (!baseReached) {
            if (!history.pageInfo.hasNextPage) {
                throw new Error(`BASE_OID ${BASE_OID} not found in the history of ${HEAD_OID}`);
            }
            cursor = history.pageInfo.endCursor;
        }
    }

    return { issues: [...issues].sort((a, b) => a - b), commitCount };
}

interface FieldValue {
    issue_field_id: number;
    issue_field_name: string;
    data_type: string;
    value: string;
}

/** A misconfigured FIELD_ID poisons every write — abort the whole run instead of per-issue skipping. */
class FieldConfigError extends Error {}

/** First-value-wins: returns false (skipped) when FIELD_ID already carries a value. */
async function stampField(issueNumber: number): Promise<boolean> {
    const current = await withRetry(`get field values #${issueNumber}`, () =>
        octokit.request("GET /repos/{owner}/{repo}/issues/{issue_number}/issue-field-values", {
            owner: OWNER,
            repo: REPO,
            issue_number: issueNumber,
        }),
    );
    const existing = (current.data as FieldValue[]).find((entry) => entry.issue_field_id === FIELD_ID);
    if (existing) {
        console.log(`#${issueNumber}: field already set to "${existing.value}", keeping it`);
        return false;
    }
    if (DRY_RUN) {
        console.log(`#${issueNumber}: [dry-run] would set field ${FIELD_ID} (${EXPECTED_FIELD_NAME}) = "${VALUE}"`);
        return true;
    }
    const response = await withRetry(`set field #${issueNumber}`, () =>
        octokit.request("POST /repos/{owner}/{repo}/issues/{issue_number}/issue-field-values", {
            owner: OWNER,
            repo: REPO,
            issue_number: issueNumber,
            issue_field_values: [{ field_id: FIELD_ID, value: VALUE }],
        }),
    );
    // Catch a stale or wrong FIELD_ID (field ids change when a field is deleted and recreated).
    const echoed = [response.data as FieldValue | FieldValue[]]
        .flat()
        .find((entry) => entry.issue_field_id === FIELD_ID);
    if (!echoed || echoed.issue_field_name !== EXPECTED_FIELD_NAME || echoed.data_type !== "text") {
        throw new FieldConfigError(
            `Field ${FIELD_ID} echoed back as "${echoed?.issue_field_name}" (${echoed?.data_type}), ` +
                `expected text field "${EXPECTED_FIELD_NAME}" — stale field id, or the endpoint's response ` +
                `shape changed (raw: ${JSON.stringify(response.data)})`,
        );
    }
    console.log(`#${issueNumber}: field set to "${VALUE}"`);
    return true;
}

/** Posts the channel comment unless one with this channel's marker already exists. */
async function postComment(issueNumber: number): Promise<boolean> {
    const comments = await withRetry(`list comments #${issueNumber}`, () =>
        octokit.paginate(octokit.issues.listComments, {
            owner: OWNER,
            repo: REPO,
            issue_number: issueNumber,
            per_page: 100,
        }),
    );
    if (comments.some((comment) => comment.body?.includes(COMMENT_MARKER))) {
        console.log(`#${issueNumber}: ${CHANNEL} comment already present, skipping`);
        return false;
    }
    const body = `${channel.commentBody(VALUE, DOCS_URL)}\n\n${COMMENT_MARKER}`;
    if (DRY_RUN) {
        console.log(`#${issueNumber}: [dry-run] would comment:\n${body}`);
        return true;
    }
    await withRetry(`comment #${issueNumber}`, () =>
        octokit.issues.createComment({ owner: OWNER, repo: REPO, issue_number: issueNumber, body }),
    );
    console.log(`#${issueNumber}: ${CHANNEL} comment posted`);
    return true;
}

function writeStepSummary(lines: string[]): void {
    const summaryPath = process.env.GITHUB_STEP_SUMMARY;
    if (summaryPath) {
        appendFileSync(summaryPath, `${lines.join("\n")}\n`);
    }
}

const { issues, commitCount } = await resolveIssues();
console.log(
    `Range ${BASE_OID}..${HEAD_OID}: ` +
        `${commitCount} commits, ${issues.length} closed issues${issues.length > 0 ? ` (#${issues.join(", #")})` : ""}`,
);
if (issues.length > MAX_ISSUES) {
    throw new Error(
        `Resolved ${issues.length} issues, exceeding MAX_ISSUES=${MAX_ISSUES} — ` +
            `suspicious range (bad base tag?), refusing to stamp`,
    );
}

const stamped: number[] = [];
const skipped: number[] = [];
const commented: number[] = [];
const failed: number[] = [];
for (const issueNumber of issues) {
    try {
        (await stampField(issueNumber)) ? stamped.push(issueNumber) : skipped.push(issueNumber);
        if (await postComment(issueNumber)) {
            commented.push(issueNumber);
        }
    } catch (error) {
        if (error instanceof FieldConfigError) {
            throw error;
        }
        // One broken issue (deleted, access-restricted, …) must not abandon the rest of the set.
        failed.push(issueNumber);
        console.error(`#${issueNumber}: ${error}`);
    }
    if (!DRY_RUN) {
        // Pace the writes — GitHub's secondary rate limit targets rapid content creation.
        await new Promise((resolve) => setTimeout(resolve, 1000));
    }
}

const describe = (list: number[]) => (list.length > 0 ? list.map((n) => `#${n}`).join(", ") : "none");
writeStepSummary([
    `### Issue version stamp (${CHANNEL})${DRY_RUN ? " — dry run" : ""}`,
    "",
    `**\`${EXPECTED_FIELD_NAME}\` = \`${VALUE}\`** over ${commitCount} commits`,
    "",
    `- Stamped: ${describe(stamped)}`,
    `- Already stamped (kept existing value): ${describe(skipped)}`,
    `- Commented: ${describe(commented)}`,
    `- Failed: ${describe(failed)}`,
]);
if (failed.length > 0) {
    process.exit(1);
}
