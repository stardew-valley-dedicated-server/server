import assert from 'assert/strict'
import core from '@actions/core';
import { inc } from 'semver'
import { generateMarkDown, loadChangelogConfig } from 'changelogen'
import {
    loadManifest,
    generateMarkDownBody,
    generateMarkDownContributors,
    getCommitsForChangelog,
    syncReleaseBranch,
    getReleasePR,
    syncReleasePR,
    updateChangelogFile,
    prepareErrorForLogging,
    dryRunStatusText,
    getLatestTag,
    determineBumpType,
} from './utils'

async function main () {
    // For now 'master' is our one and only release branch.
    // Hardcoding this instead of getting the current checked out branch allows to test this locally from any branch.
    const releaseBranch = 'master';

    const currentVersion = await getLatestTag();
    const bumpType = await determineBumpType();

    const newVersion = inc(currentVersion, bumpType);
    assert(newVersion, `Missing 'newVersion'`);

    core.info(`Starting release draft update for 'v${newVersion}'...${dryRunStatusText()}`);

    // Passing `newVersion` to ensure correct comparison links, would use 'releaseBranch' otherwise
    const config = await loadChangelogConfig(process.cwd(), { newVersion });
    const commits = await getCommitsForChangelog(config);
    const currentPR = await getReleasePR(newVersion);

    // Generate content
    const body = await generateMarkDownBody(currentPR, newVersion, bumpType);
    const changelog = await generateMarkDown(commits, config);
    const contributors = await generateMarkDownContributors();

    // Start syncing things
    await syncReleaseBranch(newVersion, async (branchExists) => {
        if (!branchExists) {
            const manifest = await loadManifest();
            await manifest.setVersion(newVersion!);
        }

        await updateChangelogFile(changelog);
    });

    const changelogForPR = changelog
        // Remove default title, it's included in the PR/Release title
        .replace(/^## v.*\n/, '')
        // Remove default contributors, we add them back with hover tooltips (CHANGELOG.md should keep regular links)
        .replace(/### ❤️ Contributors[\s\S]*$/, '')
        // Replace multiple trailing newlines with a single one
        .replace(/[\n\r]+$/, '\n');

    const releaseNotes = [
        body,
        '## 👉 Changelog',
        changelogForPR,
        '### ❤️ Contributors',
        contributors,
    ].join('\n');

    await syncReleasePR(currentPR?.number, releaseBranch, releaseNotes, newVersion);

    core.info(`Done!\n`);
    core.info(`${releaseNotes}\n`);
}

main().catch((error) => {
    console.error('Caught unhandled error:\n');
    console.log(prepareErrorForLogging(error));
    process.exit(core.ExitCode.Failure);
});
