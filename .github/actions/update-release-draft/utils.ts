import { promises as fsp } from 'node:fs'
import { join } from 'node:path'
import { execSync } from 'node:child_process'
import assert from 'node:assert'
import { compare } from 'semver'
import { exec } from 'tinyexec'
import { determineSemverChange, getGitDiff, loadChangelogConfig, parseCommits, SemverBumpType, ResolvedChangelogConfig } from 'changelogen'
import convert from 'xml-js'
import { ofetch } from 'ofetch';
import core from '@actions/core';

export function createFetch() {
    return ofetch.create({
        headers: {
            'User-Agent': 'stardew-valley-dedicated-server/server',
            'Accept': 'application/vnd.github.v3+json',
            Authorization: `token ${process.env.GITHUB_TOKEN}`,
        },
        async onRequest({ request, options }) {
            const optionsCopy = { ...options };
            delete optionsCopy.onRequest;

            for(const key of Object.keys(optionsCopy)) {
                if (!optionsCopy[key]) {
                    delete optionsCopy[key];
                }
            }

            if(!Object.keys(optionsCopy.headers).length) {
                delete optionsCopy.headers;
            }

            core.info(`[fetch request] ${request} ${JSON.stringify(optionsCopy, null, 2)}`);
        }
    });
}

export const $fetch = createFetch();

export async function loadWorkspace () {
    const files = {
        'mod/JunimoServer/manifest.json': (newVersion: string, data: string) => {
            try {
                // Read
                const parsed = JSON.parse(data || '{}');

                // Update
                parsed.Version = newVersion;

                // Write
                return JSON.stringify(parsed, null, 2) + '\n';

            } catch (err) {
                console.error(err, 'Failed updating xml.');
            }

            return data;
        },

        'mod/JunimoServer/JunimoServer.csproj': (newVersion: string, data: string) => {
            try {
                // Read
                const parsed = convert.xml2js(data, { captureSpacesBetweenElements: true });
                const project = parsed.elements[0];
                const propertyGroup = project.elements.find(item => item.name === 'PropertyGroup');
                const version = propertyGroup.elements.find(item => item.name === 'Version');

                // Update
                version.elements[0].text = newVersion;

                // Write
                return convert.js2xml(parsed);

            } catch (err) {
                console.error(err, 'Failed updating xml.');
            }

            return data;
        },
    };

    let version;
    try {
        version = JSON.parse(await fsp.readFile('mod/JunimoServer/manifest.json', 'utf-8')).Version;
    } catch (err) {
        throw new Error('Failed to read version from manifest.json', { cause: err });
    }
    assert(version, `Failed to read version from manifest.json`);

    const setVersion = async (newVersion: string) => {
        for (const [path, callback] of Object.entries(files)) {
            const pathAbsolute = join(process.cwd(), path);
            const data = await fsp.readFile(pathAbsolute, 'utf-8').catch(() => '');
            fsp.writeFile(pathAbsolute, callback(newVersion, data));
        }
    };

    return {
        version,
        setVersion,
    }
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

export async function getLatestReleasedTag () {
  const latestReleasedTag = await exec('git', ['tag', '-l']).then(r => r.stdout.trim().split('\n').filter(t => /v3\.\d+\.\d+/.test(t)).sort(compare)).then(r => r.pop()!.trim())
  return latestReleasedTag
}

export async function getPreviousReleasedCommits () {
  const config = await loadChangelogConfig(process.cwd())
  const latestTag = await getLatestTag()
  const latestReleasedTag = await getLatestReleasedTag()
  const commits = parseCommits(await getGitDiff(latestTag, latestReleasedTag), config)
  return commits
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


export async function branchExists(branchName: string) {
    const data = execSync(`git ls-remote --heads origin ${branchName}`);
    console.log(data);
    console.log(data.toString().trim().length);
    console.log(data.toString().trim().length > 0);
    return data.toString().trim().length > 0;
}
