import { execSync } from 'node:child_process'
import assert from 'node:assert/strict'
import { inc } from 'semver'
import { generateMarkDown, getCurrentGitBranch, loadChangelogConfig } from 'changelogen'
import { $fetch, determineBumpType, getContributors, getCommitsForChangelog, loadWorkspace, branchExists } from './utils'
import core from '@actions/core';


// mod/JunimoServer/manifest.json


const GIT_MAIL = 'julian@vallee-design.de';
const GIT_NAME = 'Julian Vallée';
const GIT_REPO = 'stardew-valley-dedicated-server/server';
const GIT_HEAD = 'stardew-valley-dedicated-server';

async function main () {
  // TODO: Hardcoded for development
//   const releaseBranch = await getCurrentGitBranch();
  const releaseBranch = 'master';
  const workspace = await loadWorkspace();

  const bumpType = await determineBumpType() || 'patch';
  const newVersion = inc(workspace.version, bumpType);
  assert(newVersion, `Missing 'newVersion'`);

  const config = await loadChangelogConfig(process.cwd(), { newVersion });
  const commits = await getCommitsForChangelog(config);

  core.info(`Checking if branch 'v${newVersion}' exists...`);

  // Create and push a branch with bumped versions if it has not already been created
  if (await branchExists(`v${newVersion}`)) {
    core.info(`Branch 'v${newVersion}' already exists. Skipping creation...`);
  } else {
    execSync(`git config --global user.email "${GIT_MAIL}"`);
    execSync(`git config --global user.name "${GIT_NAME}"`);
    execSync(`git checkout -b v${newVersion}`);

    await workspace.setVersion(newVersion!);

    execSync(`git commit -am v${newVersion}`);
    execSync(`git push -u origin v${newVersion}`);
    core.info(`Branch 'v${newVersion}' pushed to origin.`);
  }

  // Get the current PR for this release, if it exists
  const [currentPR] = await $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls?head=${GIT_HEAD}:v${newVersion}`)
  const contributors = await getContributors()
  const changelog = await generateMarkDown(commits, config)


  const releaseNotes = [
    // Remove changelog and add PR body if exists, otherwise add timetable when the PR is new
    currentPR?.body.replace(/## 👉 Changelog[\s\S]*$/, '') || `> ${newVersion} is the next ${bumpType} release.\n>\n> **Timetable**: to be announced.`,
    '## 👉 Changelog',
    changelog
        // Remove default title
        .replace(/^## v.*\n/, '')
        // Adjust default "compare changes" link
        // .replace(`...${releaseBranch}`, `...v${newVersion}`)
        // Override default contributors to enable hover tooltip on names
        .replace(/### ❤️ Contributors[\s\S]*$/, '')
        .replace(/[\n\r]+/g, '\n'),
    '### ❤️ Contributors',
    contributors.map(c => `- ${c.name} (@${c.username})`).join('\n'),
  ].join('\n')

  assert(releaseNotes, `Missing 'releaseNotes'`);

  // Create a PR with release notes if none exists
  if (!currentPR) {
    assert.equal(releaseBranch, 'master', `Invalid 'releaseBranch' value`);

    return await $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls`, {
      method: 'POST',
      body: {
        title: `v${newVersion}`,
        head: `v${newVersion}`,
        base: releaseBranch,
        body: releaseNotes,
        draft: true,
      },
    }).catch((err) => {
      console.error(err.output.stderr.toString());
      process.exit(1);
    })
  }

  // Update release notes if the pull request does exist
  await $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls/${currentPR.number}`, {
    method: 'PATCH',
    body: {
      body: releaseNotes,
    },
  })
}

main().catch((err) => {
  console.error(err?.output?.stderr?.toString() || err);
  process.exit(1);
})
