import assert from 'node:assert/strict'
import { inc } from 'semver'
import { generateMarkDown, getCurrentGitBranch, loadChangelogConfig, SemverBumpType } from 'changelogen'
import core from '@actions/core';
import {
    determineBumpType,
    getContributors,
    getCommitsForChangelog,
    loadManifest,
    tryBumpAndCreateBranch,
    getPr,
    createPR,
    updatePR,
} from './utils'

async function main () {
    // const releaseBranch = await getCurrentGitBranch();
    const releaseBranch = 'master';
    assert.equal(releaseBranch, 'master', `Invalid 'releaseBranch' value`);

    const manifest = await loadManifest();
    assert(manifest?.version, `Missing 'manifest.version'`);

    // const bumpType = await determineBumpType() || 'patch' || 'prerelease';
    const bumpType = 'prerelease';
    assert(bumpType, `Missing 'bumpType'`);

    const newVersion = inc(manifest.version, bumpType);
    assert(newVersion, `Missing 'newVersion'`);

    // Passing `newVersion` to ensure a proper changelog comparison link
    const config = await loadChangelogConfig(process.cwd(), { newVersion });
    const commits = await getCommitsForChangelog(config);

    await tryBumpAndCreateBranch(manifest, newVersion);

    // Get the current PR for this release, if it exists
    const [currentPR] = await getPr(newVersion);

    // On existing PR -> remove changelog and below
    // On new PR -> add timetable above changelog
    const body = currentPR?.body.replace(/## 👉 Changelog[\s\S]*$/, '') || `> ${newVersion} is the next ${bumpType} release.\n>\n> **Timetable**: to be announced.`;

    const changelog = (await generateMarkDown(commits, config))
        // Remove default title, it should not be part of the description
        .replace(/^## v.*\n/, '')
        // Remove default contributors, so we can add them back with hover tooltips
        .replace(/### ❤️ Contributors[\s\S]*$/, '');

    const contributors = (await getContributors())
        .map(c => `- ${c.name} (@${c.username})`).join('\n');

    const releaseNotes = [
        body,
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
