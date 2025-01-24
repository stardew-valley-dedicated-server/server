import { promises as fsp, existsSync } from 'fs'
import { join } from 'path'
import { execSync } from 'child_process'
import assert from 'assert'
import { exec } from 'tinyexec'
import { generateMarkDown, determineSemverChange, getGitDiff, loadChangelogConfig, parseCommits, type ResolvedChangelogConfig } from 'changelogen'
import { ofetch } from 'ofetch';
import convert from 'xml-js'
import core from '@actions/core';
import pc from 'picocolors';

const GIT_MAIL = 'julian@vallee-design.de';
const GIT_NAME = 'Julian Vallée';
const GIT_REPO = 'stardew-valley-dedicated-server/server';
const GIT_HEAD = 'stardew-valley-dedicated-server';

const $fetch = ofetch.create({
    headers: {
        'User-Agent': 'stardew-valley-dedicated-server/server',
        'Accept': 'application/vnd.github.v3+json',
        Authorization: `token ${process.env.GITHUB_TOKEN}`,
    },
    async onRequest({ request, options }) {
        const optionsCopy = { ...options };
        delete optionsCopy.onRequest;

        // Remove possibly empty props
        for(const key of Object.keys(optionsCopy)) {
            if (!optionsCopy[key]) {
                delete optionsCopy[key];
            }
        }

        // Remove possibly empty header object
        if(!Object.keys(optionsCopy.headers).length) {
            delete (optionsCopy as any).headers;
        }

        if(Object.keys(optionsCopy).length) {
            core.info(`[fetch] ${request} ${JSON.stringify(optionsCopy, null, 2)}`);
        } else {
            console.log(pc.gray(`[fetch] GET`), pc.blue(request.toString()));
        }
    }
});

export function prepareErrorForLogging(error: any): Error {
    // Remove unecessary props from execSync SystemError
    if('spawnargs' in error) {
        delete error.path;
        delete error.syscall;
        delete error.errno;
        delete error.code;
        delete error.spawnargs;
        delete error.signal;
        delete error.status;
        delete error.output;
        delete error.stdout;
        delete error.stderr;
        delete error.error;
    }

    return error;
}

export function isDryRun() {
    return process.env.DRY_RUN?.toLowerCase() === 'true';
}

export function dryRunStatusText() {
    return isDryRun() ? ' (dry-run)' : '';
}

export async function loadManifest () {
    const files = {
        'mod/JunimoServer/manifest.json': (newVersion: string, data: string) => {
            const parsed = JSON.parse(data || '{}');
            parsed.Version = newVersion;
            return JSON.stringify(parsed, null, 2) + '\n';
        },
        'mod/JunimoServer/JunimoServer.csproj': (newVersion: string, data: string) => {
            const parsed = convert.xml2js(data, { captureSpacesBetweenElements: true });
            const project = parsed.elements[0];
            const propertyGroup = project.elements.find((item: any) => item.name === 'PropertyGroup');
            const version = propertyGroup.elements.find((item: any) => item.name === 'Version');
            version.elements[0].text = newVersion;
            return convert.js2xml(parsed);
        },
    };

    let version = await readVersionFromManifest();

    const setVersion = async (newVersion: string) => {
        console.log(`Updating bump files...`);

        for (const [filePath, callback] of Object.entries(files)) {
            const pathAbsolute = join(process.cwd(), filePath);

            try {
                const data = await fsp.readFile(pathAbsolute, 'utf-8');
                fsp.writeFile(pathAbsolute, callback(newVersion, data));
            } catch (err) {
                throw new Error('Failed updating file with version', { cause: err });
            }
        }
    };

    return {
        version,
        setVersion,
    }
}

async function readVersionFromManifest() {
    let version: string;

    try {
        version = JSON.parse(await fsp.readFile('mod/JunimoServer/manifest.json', 'utf-8')).Version;
    } catch (err) {
        throw new Error('Failed to read version from manifest.json', { cause: err });
    }

    assert(version, 'Failed to read version from manifest.json');

    return version;
}

export async function determineBumpType () {
    // TODO: Temporarily overriding with prerelease, until we decide to leave it
    return 'prerelease';

    const config = await loadChangelogConfig(process.cwd())
    const commits = await getLatestCommits()

    return determineSemverChange(commits, config) || 'patch';
}

export async function getLatestTag () {
  const { stdout: latestTag } = await exec('git', ['describe', '--tags', '--abbrev=0']);
  return latestTag.trim();
}

export async function getLatestCommits () {
  const config = await loadChangelogConfig(process.cwd());
  const latestTag = await getLatestTag();

  return parseCommits(await getGitDiff(latestTag), config);
}

export async function getCommitsForChangelog (config: ResolvedChangelogConfig) {
    return getLatestCommits().then(commits => commits.filter(
        c => config.types[c.type] && !(c.type === 'chore' && c.scope === 'deps'),
      ));
}

export async function getContributors () {
  const contributors = [] as Array<{ name: string, username: string }>;
  const emails = new Set<string>();
  const latestTag = await getLatestTag();
  const rawCommits = await getGitDiff(latestTag, 'master');

  console.log(`Fetching contributors for commits between '${latestTag}' and 'master'...`);

  for (const commit of rawCommits) {
    if (emails.has(commit.author.email) || commit.author.name === 'renovate[bot]') {
        continue;
    }

    try {
        const { author } = await $fetch<{ author: { login: string, email: string } }>(`https://api.github.com/repos/${GIT_REPO}/commits/${commit.shortHash}`);

        if (!contributors.some(c => c.username === author.login)) {
            contributors.push({
                name: commit.author.name,
                username: author.login
            });
        }

        emails.add(author.email);
    } catch(error) {
        throw new Error('Failed fetching contributors.', { cause: error });
    }
  }

  return contributors;
}

export async function branchExists(branchName: string) {
    return execSync(`git ls-remote --heads origin ${branchName}`).toString().trim().length > 0;
}

export function getReleaseTypeText(bumpType: string) {
    // Appending the word "release" only for bumpTypes which don't include the word itself already
    return bumpType === 'prerelease' ? 'prerelease' : `${bumpType} release`;
}

export async function getReleasePR(newVersion: string) {
    core.info(`Fetching release PR...`);
    return $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls?head=${GIT_HEAD}:v${newVersion}`).then((res: any) => res[0]);
}

export async function createReleasePR(target: string, body: string, newVersion: string) {
    core.info(`Creating release PR...`);
    return $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls`, {
        method: 'POST',
        body: {
            title: `v${newVersion}`,
            head: `v${newVersion}`,
            base: target,
            body,
            draft: true,
        },
    });
}

export async function updateReleasePR(pullRequestId: string, releaseNotes: string) {
    core.info(`Updating release PR...`)
    return $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls/${pullRequestId}`, {
        method: 'PATCH',
        body: {
            body: releaseNotes,
        },
    });
}

export async function syncReleasePR(pullRequestId: string, releaseBranch: string, releaseNotes: string, newVersion: string) {
    if (isDryRun()) {
        return;
    }

    console.log(`Syncing release PR for 'v${newVersion}'...`);

    if (!pullRequestId) {
        return createReleasePR(releaseBranch, releaseNotes, newVersion);
    }

    return updateReleasePR(pullRequestId, releaseNotes);
}

export async function syncReleaseBranch(newVersion: string, cb: (branchExists: boolean) => Promise<void>) {
    const branch = `v${newVersion}`;

    if (isDryRun()) {
        await cb(false);
        return;
    }

    execSync(`git config --global user.email "${GIT_MAIL}"`);
    execSync(`git config --global user.name "${GIT_NAME}"`);

    if (await branchExists(branch)) {
        execSync(`git checkout ${branch}`);

        await cb(true);

        core.info(`Syncing release branch...`);
        execSync(`git commit -am ${branch} --amend`);
        execSync(`git push --force`);
        core.info(`Branch '${branch}' updated with 'CHANGELOG.md' changes.`);
    } else {
        execSync(`git checkout -b ${branch}`);

        await cb(false);

        core.info(`Syncing release branch...`);
        execSync(`git commit -am ${branch}`);
        execSync(`git push -u origin ${branch}`);
        core.info(`Branch '${branch}' created and pushed.`);
    }
}

export async function generateMarkDownBody(currentPR: any, newVersion: string, bumpType: string) {
    const nextReleaseType = getReleaseTypeText(bumpType);

    // When amending existing PR -> remove changelog and below
    // When creating new PR -> add timetable above changelog
    return currentPR?.body.replace(/## 👉 Changelog[\s\S]*$/, '') || `> v${newVersion} is the next ${nextReleaseType}.\n>\n> **Timetable**: to be announced.\n`;
}

export async function generateMarkDownChangelog(commits: any, config: ResolvedChangelogConfig) {
    return (await generateMarkDown(commits, config))
        // Remove default title, it's included in each github release
        .replace(/^## v.*\n/, '')
        // Remove default contributors, so we can add them back with hover tooltips
        .replace(/### ❤️ Contributors[\s\S]*$/, '');
}

export async function generateMarkDownContributors() {
    return (await getContributors())
        .map(c => `- ${c.name} (@${c.username})`).join('\n');
}

export async function updateChangelogFile(markdown: string, output = 'CHANGELOG.md') {
    core.info('Updating changelog file...');

    try {
        let changelogMD;
        if (existsSync(output)) {
            changelogMD = await fsp.readFile(output, 'utf8');
        } else {
            changelogMD = '# Changelog\n\n';
        }

        const lastEntry = changelogMD.match(/^###?\s+.*$/m);
        if (lastEntry) {
            changelogMD = changelogMD.slice(0, lastEntry.index) + markdown + '\n\n' + changelogMD.slice(lastEntry.index);
        } else {
            changelogMD += '\n' + markdown + '\n\n';
        }

        await fsp.writeFile(output, changelogMD);
    } catch (err) {
        throw new Error('Failed syncing changelog file', { cause: err });
    }
}
