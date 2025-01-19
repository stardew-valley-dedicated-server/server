import { promises as fsp } from 'node:fs'
import { join } from 'node:path'
import { execSync } from 'node:child_process'
import assert from 'node:assert'
import { exec } from 'tinyexec'
import { determineSemverChange, getGitDiff, loadChangelogConfig, parseCommits, ResolvedChangelogConfig } from 'changelogen'
import convert from 'xml-js'
import { ofetch } from 'ofetch';
import core from '@actions/core';

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
    // async onRequest({ request, options }) {
    //     const optionsCopy = { ...options };
    //     delete optionsCopy.onRequest;

    //     // Remove possibly empty props
    //     for(const key of Object.keys(optionsCopy)) {
    //         if (!optionsCopy[key]) {
    //             delete optionsCopy[key];
    //         }
    //     }

    //     // Remove possibly empty headers object
    //     if(!Object.keys(optionsCopy.headers).length) {
    //         delete (optionsCopy as any).headers;
    //     }

    //     core.info(`[fetch request] ${request} ${JSON.stringify(optionsCopy, null, 2)}`);
    // }
});

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

    assert(version, `Failed to read version from manifest.json`);

    return version;
}

export async function determineBumpType () {
  const config = await loadChangelogConfig(process.cwd())
  const commits = await getLatestCommits()

  return determineSemverChange(commits, config)
}

export async function getLatestTag () {
  const { stdout: latestTag } = await exec('git', ['describe', '--tags', '--abbrev=0'])
  return latestTag.trim()
}

export async function getLatestCommits () {
  const config = await loadChangelogConfig(process.cwd())
  const latestTag = await getLatestTag()

  return parseCommits(await getGitDiff(latestTag), config)
}

export async function getCommitsForChangelog (config: ResolvedChangelogConfig) {
    return getLatestCommits().then(commits => commits.filter(
        c => config.types[c.type] && !(c.type === 'chore' && c.scope === 'deps'),
      ));
}

export async function getContributors () {
  const contributors = [] as Array<{ name: string, username: string }>
  const emails = new Set<string>()
  const latestTag = await getLatestTag()
  const rawCommits = await getGitDiff(latestTag)

  for (const commit of rawCommits) {
    if (commit.shortHash === 'dcce6e4') {
        continue
    }

    if (emails.has(commit.author.email) || commit.author.name === 'renovate[bot]') {
        continue
    }
    const { author } = await $fetch<{ author: { login: string, email: string } }>(`https://api.github.com/repos/stardew-valley-dedicated-server/server/commits/${commit.shortHash}`)

    if (!contributors.some(c => c.username === author.login)) {
      contributors.push({ name: commit.author.name, username: author.login })
    }

    emails.add(author.email)
  }

  return contributors
}

export async function setGithubToken() {
    return execSync(`git remote set-url origin https://${process.env.GITHUB_TOKEN}@github.com/${GIT_REPO}.git/`);
}

export async function branchExists(branchName: string) {
    return execSync(`git ls-remote --heads origin ${branchName}`).toString().trim().length > 0;
}

export async function getPr(newVersion: string) {
    return $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls?head=${GIT_HEAD}:v${newVersion}`)
}

export async function createPR(releaseBranch: string, releaseNotes: string, newVersion: string) {
    return $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls`, {
        method: 'POST',
        body: {
            title: `v${newVersion}`,
            head: `v${newVersion}`,
            base: releaseBranch,
            body: releaseNotes,
            draft: true,
        },
    });
}

export async function updatePR(pullRequestId: string, releaseNotes: string) {
    return $fetch(`https://api.github.com/repos/${GIT_REPO}/pulls/${pullRequestId}`, {
        method: 'PATCH',
        body: {
            body: releaseNotes,
        },
    });
}

export async function tryBumpAndCreateBranch(manifest: any, newVersion: string) {
    const branch = `v${newVersion}`;

    core.info(`Checking if branch '${branch}' exists...`);

    if (await branchExists(branch)) {
        core.info(`Branch '${newVersion}' already exists. Skipping creation...`);
    } else {
        execSync(`git config --global user.email "${GIT_MAIL}"`);
        execSync(`git config --global user.name "${GIT_NAME}"`);
        execSync(`git checkout -b ${branch}`);

        await manifest.setVersion(newVersion!);

        execSync(`git commit -am ${branch}`);
        execSync(`git push -u origin ${branch}`);
        core.info(`Branch '${branch}' created and pushed.`);
    }
}
