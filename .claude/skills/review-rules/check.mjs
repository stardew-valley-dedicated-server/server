// Mechanical checks for .claude rules and skills: word budget, frontmatter, relative links, index sync.
// Caps are read from the Budget table in standard.md.
// Usage (repo root): node .claude/skills/review-rules/check.mjs — exits 1 when any problem is found.
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const skillDir = dirname(fileURLToPath(import.meta.url));
const root = resolve(skillDir, "../../..");
const rulesDir = join(root, ".claude/rules");
const universalDir = join(rulesDir, "universal");
const skillsDir = join(root, ".claude/skills");
const readme = join(rulesDir, "README.md");
const claudeMd = join(root, "CLAUDE.md");

const rel = (p) => relative(root, p).replaceAll("\\", "/");
const read = (p) => readFileSync(p, "utf8");
const words = (text) => text.split(/\s+/).filter(Boolean).length;
const mdFiles = (dir) =>
  existsSync(dir)
    ? readdirSync(dir, { recursive: true })
        .filter((f) => f.endsWith(".md"))
        .map((f) => join(dir, f))
    : [];
const frontmatter = (text) => /^---\r?\n([\s\S]*?)\r?\n---/.exec(text)?.[1] ?? null;
// Globs from the `paths:` list form that standard.md prescribes; empty, `null`, and `~` items don't count.
const pathGlobs = (fm) => {
  const block = /^paths:[ \t]*(?:#.*)?((?:\r?\n[ \t]+-.*)+)/m.exec(fm)?.[1] ?? "";
  return [...block.matchAll(/-[ \t]*(.*)/g)]
    .map((m) => m[1].replace(/\s+#.*$/, "").trim().replace(/^(["'])(.*)\1$/, "$2"))
    .filter((g) => g && g !== "null" && g !== "~");
};
const decode = (target) => {
  try {
    return decodeURIComponent(target);
  } catch {
    return target;
  }
};

const standard = read(join(skillDir, "standard.md"));
const cap = (row) => {
  const m = new RegExp(`^\\|\\s*${row}[^|]*\\|\\s*([\\d,]+) words`, "m").exec(standard);
  if (!m) throw new Error(`standard.md Budget table has no "${row}" row`);
  return Number(m[1].replaceAll(",", ""));
};
const ALWAYS_LOADED_CAP = cap("Always loaded");
const UNIVERSAL_CAP = cap("One universal rule");
const PATH_SCOPED_CAP = cap("One path-scoped rule");

const universal = mdFiles(universalDir);
const pathScoped = mdFiles(rulesDir).filter((p) => p !== readme && !p.startsWith(universalDir + sep));
const skills = existsSync(skillsDir)
  ? readdirSync(skillsDir, { withFileTypes: true })
      .filter((d) => d.isDirectory())
      .map((d) => join(skillsDir, d.name, "SKILL.md"))
      .filter(existsSync)
  : [];

const problems = [];

// Budget
const alwaysLoaded = [claudeMd, readme, ...universal].filter(existsSync);
const alwaysTotal = alwaysLoaded.reduce((sum, p) => sum + words(read(p)), 0);
if (alwaysTotal > ALWAYS_LOADED_CAP)
  problems.push(`budget: always-loaded ${alwaysTotal} words > ${ALWAYS_LOADED_CAP}`);
const overCap = (files, limit, label) =>
  files
    .map((p) => [p, words(read(p))])
    .filter(([, n]) => n > limit)
    .sort((a, b) => b[1] - a[1])
    .forEach(([p, n]) => problems.push(`budget: ${rel(p)} ${n} words > ${limit} (${label})`));
overCap(universal, UNIVERSAL_CAP, "universal");
overCap(pathScoped, PATH_SCOPED_CAP, "path-scoped");

// Frontmatter
for (const p of universal)
  if (frontmatter(read(p)) !== null) problems.push(`frontmatter: ${rel(p)} is universal but has frontmatter`);
for (const p of pathScoped)
  if (pathGlobs(frontmatter(read(p)) ?? "").length === 0) problems.push(`frontmatter: ${rel(p)} has no paths: glob`);
for (const p of skills) {
  const fm = frontmatter(read(p)) ?? "";
  const dirName = dirname(p).split(/[\\/]/).pop();
  if (!new RegExp(`^name:\\s*(["']?)${dirName}\\1\\s*$`, "m").test(fm))
    problems.push(`frontmatter: ${rel(p)} name: must be ${dirName}`);
  if (!/^description:\s*["']?[^\s"']/m.test(fm)) problems.push(`frontmatter: ${rel(p)} lacks description:`);
}

// Relative links (fenced code blocks excluded)
const skillDocs = skills.flatMap((p) => mdFiles(dirname(p)));
for (const p of [claudeMd, readme, ...universal, ...pathScoped, ...skillDocs].filter(existsSync)) {
  const text = read(p).replace(/```[\s\S]*?```/g, "");
  for (const [, angled, bare] of text.matchAll(/\]\((?:<([^>]+)>|([^)\s]+))\)/g)) {
    const target = angled ?? bare;
    if (/^(https?:|mailto:|#)/.test(target)) continue;
    if (!existsSync(resolve(dirname(p), decode(target.split("#")[0]))))
      problems.push(`link: ${rel(p)} -> ${target} does not exist`);
  }
}

// Index sync
if (existsSync(readme)) {
  const indexed = new Set([...read(readme).matchAll(/^\|\s*`([^`]+\.md)`/gm)].map((m) => m[1]));
  const present = new Set(pathScoped.map((p) => relative(rulesDir, p).replaceAll("\\", "/")));
  for (const f of present) if (!indexed.has(f)) problems.push(`index: ${f} has no README.md row`);
  for (const f of indexed) if (!present.has(f)) problems.push(`index: README.md row ${f} has no file`);
}

console.log(`always-loaded ${alwaysTotal}/${ALWAYS_LOADED_CAP} words (${alwaysLoaded.length} files)`);
console.log(`universal ${universal.length} files, path-scoped ${pathScoped.length} files, skills ${skills.length}`);
console.log(problems.length ? `\n${problems.length} problems:\n${problems.join("\n")}` : "\nno problems");
process.exitCode = problems.length ? 1 : 0;
