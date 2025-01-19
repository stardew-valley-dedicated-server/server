import assert from 'node:assert/strict'
import { inc } from 'semver'
import { generateMarkDown, getCurrentGitBranch, loadChangelogConfig } from 'changelogen'
import core from '@actions/core';
import {
    $fetch,
    determineBumpType,
    getContributors,
    getCommitsForChangelog,
    loadManifest,
    tryBumpAndCreateBranch,
    createPR,
    updatePR,
    GIT_REPO,
    GIT_HEAD
} from './utils'

async function main () {
    // const releaseBranch = await getCurrentGitBranch();
    const releaseBranch = 'master';
    assert.equal(releaseBranch, 'master', `Invalid 'releaseBranch' value`);

    const manifest = await loadManifest();

    const bumpType = await determineBumpType() || 'patch';
    assert(bumpType, `Missing 'bumpType'`);

    const newVersion = inc(manifest.version, bumpType);
    assert(newVersion, `Missing 'newVersion'`);

    // Pass `newVersion`` to ensure the changelog comparison link uses the correct versions
    const config = await loadChangelogConfig(process.cwd(), { newVersion });
    const commits = await getCommitsForChangelog(config);

    await tryBumpAndCreateBranch(manifest, newVersion);

    // Get the current PR for this release, if it exists
    const [currentPR] = await $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls?head=${GIT_HEAD}:v${newVersion}`);

    const changelog = (await generateMarkDown(commits, config))
        // Remove default title
        .replace(/^## v.*\n/, '')
        // Adjust default "compare changes" link
        // .replace(`...${releaseBranch}`, `...v${newVersion}`)
        // Remove default contributors, we add them ourself to enable hover tooltip on names
        .replace(/### ❤️ Contributors[\s\S]*$/, '');
        // Convert CRLF to LF
        // .replace(/[\n\r]+/g, '\n');

    const contributors = (await getContributors())
        .map(c => `- ${c.name} (@${c.username})`).join('\n');

    const releaseNotes = [
        // If PR exists -> remove everything beginning from changelog
        // If PR is new -> add timetable above changelog
        currentPR?.body.replace(/## 👉 Changelog[\s\S]*$/, '') || `> ${newVersion} is the next ${bumpType} release.\n>\n> **Timetable**: to be announced.`,
        '## 👉 Changelog',
        changelog,
        '### ❤️ Contributors',
        contributors,
    ].join('\n')

    if (!currentPR) {
        return createPR(releaseBranch, releaseNotes, newVersion);
    }

    return updatePR(currentPR.number, releaseNotes);
}

main().catch((err) => {
    core.error(err?.output?.stderr?.toString() || err);
    process.exit(core.ExitCode.Failure);
});
