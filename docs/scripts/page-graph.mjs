// Builds an interactive graph of every docs page and the links between them,
// one ordered column per section. Everything is derived from the docs tree, so
// there is nothing per-page to maintain here: section ORDER comes from the nav
// in config.ts (folders not yet in the nav are appended, never dropped), and
// section colors fall back to a palette when not in the override map.
//
// Run: `make docs-graph`, or `npm run graph` from docs/.

import { execSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const docsDir = path.resolve(scriptDir, "..");
const repoRoot = path.resolve(docsDir, "..");
const configPath = path.join(docsDir, ".vitepress", "config.ts");
// Local-only artifact (not deployed). `.output/` is already gitignored by the
// repo's "Build output" rule (**/.output) and isn't wiped by `vitepress build`.
const outPath = path.join(docsDir, ".output", "page-graph.html");

const cfg = readFileSync(configPath, "utf8");
const origin = (cfg.match(/const origin\s*=\s*["']([^"']+)["']/) || [])[1]
  || "https://stardew-valley-dedicated-server.github.io";
const site = origin.replace(/\/$/, "") + "/server";
// Slice to the nav block first so sidebar links (same shape) don't leak in.
const navSlice = cfg.slice(cfg.indexOf("nav:"), cfg.indexOf("sidebar:"));
const navSections = [...new Set(
  [...navSlice.matchAll(/link:\s*["']\/([^/"']+)\//g)].map((m) => m[1]),
)];

const files = execSync('git ls-files "*.md"', { cwd: docsDir }).toString().trim().split("\n")
  // Skip `[param].md` dynamic-route templates — they expand to generated pages
  // at build, so as a single literal node they'd just be a dead placeholder.
  .filter((f) => !f.startsWith("node_modules") && f !== "README.md"
    && !f.includes("_partials") && !path.basename(f).startsWith("["));

function toId(file) {
  let p = "/" + file.replace(/\\/g, "/");
  p = p.replace(/\.md$/, "").replace(/\/index$/, "/");
  return p === "/index" ? "/" : p;
}

const pages = new Map();
for (const f of files) {
  const id = toId(f);
  const txt = readFileSync(path.join(docsDir, f), "utf8");
  const m = txt.match(/^#\s+(.+)$/m) || txt.match(/^title:\s*(.+)$/m);
  const title = m ? m[1].replace(/[`*]/g, "").trim() : id;
  const section = id === "/" ? "home" : (id.split("/")[1] || "home");
  pages.set(id, { file: f, section, title });
}

function resolveLink(srcId, link) {
  link = link.trim().split(/\s+/)[0];      // drop an optional `"title"` after the URL
  link = link.split("#")[0].split("?")[0];
  if (!link) return null;
  let target;
  if (link.startsWith("/")) target = link;
  else {
    const srcDir = srcId.endsWith("/") ? srcId : srcId.replace(/\/[^/]*$/, "/");
    target = path.posix.normalize(srcDir + link);
  }
  target = target.replace(/\.(md|html)$/, "").replace(/\/index$/, "/");
  return target !== "/" ? target.replace(/\/$/, "") : target;
}
function findPage(target) {
  if (pages.has(target)) return target;
  if (pages.has(target + "/")) return target + "/";
  if (target.endsWith("/") && pages.has(target.slice(0, -1))) return target.slice(0, -1);
  return null;
}

const edges = new Set();
function addLink(id, link) {
  // Reject only external/anchor links; bare relative links (`ci-cd.md`) are valid
  // and resolveLink handles them. Non-pages fall out via findPage returning null.
  if (/^(https?:|mailto:|#)/.test(link)) return;
  const found = findPage(resolveLink(id, link) || "");
  if (found && found !== id) edges.add(id + "|" + found);
}
const linkRe = /\]\(([^)]+)\)/g;
const fmLinkRe = /^\s*link:\s*(\S+)\s*$/gm; // hero action links live in frontmatter, not markdown
for (const [id, info] of pages) {
  const txt = readFileSync(path.join(docsDir, info.file), "utf8");
  let mm;
  while ((mm = linkRe.exec(txt))) addLink(id, mm[1].trim());
  const fm = txt.match(/^---\n([\s\S]*?)\n---/);
  if (fm) { let fmm; while ((fmm = fmLinkRe.exec(fm[1]))) addLink(id, fmm[1].trim()); }
}

const nodes = [...pages].map(([id, page]) => ({ id, section: page.section, title: page.title }));
const edgeList = [...edges].map((k) => { const [from, to] = k.split("|"); return { from, to }; });

// Nav order first, then any section folder not yet in the nav, so a new area
// still shows up instead of being silently dropped.
const present = new Set(nodes.map((n) => n.section).filter((s) => s !== "home"));
const ordered = navSections.filter((s) => present.has(s));
const sections = [...ordered, ...[...present].filter((s) => !ordered.includes(s)).sort()];

const COLOR_OVERRIDES = {
  home: "#63dbe4", players: "#7ee787", admins: "#f2a65a",
  features: "#c084fc", developers: "#58a6ff", community: "#f778ba",
};
const PALETTE = ["#7ee787", "#f2a65a", "#c084fc", "#58a6ff", "#f778ba",
  "#e3b341", "#ff7b72", "#39c5cf", "#d2a8ff", "#ffa657"];
const colors = { home: COLOR_OVERRIDES.home };
sections.forEach((s, i) => { colors[s] = COLOR_OVERRIDES[s] || PALETTE[i % PALETTE.length]; });

// Escape `<` so a value like a page title containing `</script>` can't break
// out of the inline <script> the data is injected into.
const js = (v) => JSON.stringify(v).replace(/</g, "\\u003c");

const html = render({ nodes, edges: edgeList }, colors, sections, site);
mkdirSync(path.dirname(outPath), { recursive: true });
writeFileSync(outPath, html);
console.log(`Wrote ${path.relative(repoRoot, outPath)} — ${nodes.length} pages, ${edgeList.length} links`);
console.log(`Sections: ${sections.join(", ")}`);

function render(DATA, COLORS, SECTIONS, SITE) {
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>JunimoServer Docs — Page Graph</title>
<script src="https://unpkg.com/vis-network@9.1.9/standalone/umd/vis-network.min.js"></script>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  html, body { margin: 0; height: 100%; background: #0e1116; color: #e6edf3;
    font-family: -apple-system, Segoe UI, Roboto, sans-serif; }
  #app { display: flex; flex-direction: column; height: 100%; }
  header { padding: 10px 16px; border-bottom: 1px solid #222a35; display: flex;
    align-items: baseline; gap: 14px; flex-wrap: wrap; }
  header h1 { font-size: 15px; margin: 0; font-weight: 600; }
  header .sub { font-size: 12px; color: #8b949e; }
  #net { flex: 1; min-height: 0; }
  /* Collapsible legend panel, bottom-left. */
  #legend-panel { position: absolute; left: 14px; bottom: 34px; min-width: 150px;
    background: rgba(20, 26, 34, 0.92); border: 1px solid #222a35; border-radius: 8px;
    font-size: 12px; overflow: hidden; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.4); }
  .lp-head { display: flex; align-items: center; justify-content: space-between; gap: 10px;
    padding: 7px 10px; cursor: pointer; user-select: none; font-weight: 600; color: #c9d1d9; }
  .lp-head:hover { background: #1b222c; }
  .lp-caret { color: #8b949e; transition: transform 0.15s; }
  #legend-panel.collapsed .lp-caret { transform: rotate(-90deg); }
  #legend-panel.collapsed #legend { display: none; }
  #legend { display: flex; flex-direction: column; gap: 0; padding: 4px 6px 6px; }
  #legend span { display: flex; align-items: center; gap: 7px; cursor: pointer;
    padding: 2px 7px; border-radius: 5px; user-select: none; color: #c9d1d9; line-height: 1.5; }
  #legend span:hover { background: #1b222c; }
  #legend i { width: 11px; height: 11px; border-radius: 3px; display: inline-block; }
  #hint { position: absolute; bottom: 10px; left: 50%; transform: translateX(-50%);
    max-width: 94%; text-align: center; font-size: 11px; color: #6a737d; pointer-events: none; }
</style>
</head>
<body>
<div id="app">
  <header>
    <h1>JunimoServer Documentation — Page Relationship Graph</h1>
    <span class="sub" id="stats"></span>
  </header>
  <div id="net"></div>
</div>
<div id="legend-panel">
  <div class="lp-head"><span>Sections</span><span class="lp-caret">▾</span></div>
  <div id="legend"></div>
</div>
<div id="hint">Drag to pan · scroll to zoom · hover to preview links · Shift = only outgoing, Alt = only incoming · click nodes to pin (multi-select) · empty click clears · Ctrl/Cmd+click opens the page</div>
<script>
const DATA = ${js(DATA)};
const SITE = ${js(SITE)};
const COLORS = ${js(COLORS)};
// Fixed-position columns (physics off) so nothing overlaps; pages are sorted by
// URL so a section's subpages group together.
const SECTIONS = ${js(SECTIONS)};
const COL_GAP = 480;   // horizontal distance between section columns
const ROW_GAP = 80;    // vertical distance between stacked pages
const HDR_Y = -110;    // section header row

const bySec = {};
for (const s of SECTIONS) bySec[s] = [];
for (const n of DATA.nodes) { if (bySec[n.section]) bySec[n.section].push(n); }
for (const s of SECTIONS) bySec[s].sort((a, b) => a.id.localeCompare(b.id));

const deg = {};
DATA.edges.forEach(e => { deg[e.from] = (deg[e.from] || 0) + 1; deg[e.to] = (deg[e.to] || 0) + 1; });

const nodeList = [];
// Page markers are dots (edges attach cleanly to the circle border, no gaps).
// The label renders below. A separate invisible hitbox spanning dot+label —
// computed further down — gives each node a full-size hover/click target.
const HIT = {};                         // page id -> {x,y,size,label} for hitbox calc
function pageNode(id, label, s, x, y) {
  const d = deg[id] || 0;
  const size = 8 + Math.min(d, 12) * 1.4;
  HIT[id] = { x, y, size, label };
  return {
    id, label, group: s, title: id, x, y, fixed: true,
    shape: "dot", size,
    color: { background: COLORS[s], border: "#0b0e13",
             highlight: { background: COLORS[s], border: "#ffffff" } },
    font: { color: "#c9d1d9", size: 13, face: "Segoe UI" },
  };
}

// Home sits left of the columns, at the same height as the section entry pages
// (row 0), so the flow reads from Home into each section's landing page.
nodeList.push(pageNode("/", "Home", "home", -COL_GAP, 0));

SECTIONS.forEach((s, col) => {
  const x = col * COL_GAP;
  // Column header (not a real page — not clickable).
  nodeList.push({
    id: "__hdr_" + s, label: s.toUpperCase(), group: s,
    x, y: HDR_Y, fixed: true, shape: "text",
    font: { color: COLORS[s], size: 20, face: "Segoe UI", bold: { color: COLORS[s], size: 20 } },
  });
  bySec[s].forEach((n, row) => {
    nodeList.push(pageNode(n.id, n.title, s, x, row * ROW_GAP));
  });
});

const nodes = new vis.DataSet(nodeList);

// Look up a node's section to color each edge by its source.
const secOf = {}; DATA.nodes.forEach(n => secOf[n.id] = n.section);
secOf["/"] = "home";
const baseColorOf = {};                 // edgeId -> base color hex
const edgeIdsByNode = {};               // nodeId -> [edgeId] incident edges

// Collapse reciprocal references (A->B and B->A) into ONE edge with an arrow on
// both ends; keep the first-seen direction for coloring/orientation.
const present = new Set(DATA.edges.map((e) => e.from + "|" + e.to));
const done = new Set();
const merged = [];
for (const e of DATA.edges) {
  const key = e.from + "|" + e.to, rev = e.to + "|" + e.from;
  if (done.has(key) || done.has(rev)) continue;
  const bidir = present.has(rev);
  merged.push({ from: e.from, to: e.to, bidir });
  done.add(key); if (bidir) done.add(rev);
}

const edgeData = merged.map((e, i) => {
  const id = "e" + i;
  const c = COLORS[secOf[e.from]] || "#8b949e";
  baseColorOf[id] = c;
  (edgeIdsByNode[e.from] ||= []).push(id);
  (edgeIdsByNode[e.to] ||= []).push(id);
  // Same-column edges (both endpoints share an x) would render as vertical lines
  // straight through the dots stacked between them. Bow them out to one side
  // instead — roundness normalized by length so the sideways bulge stays bounded
  // regardless of how many rows the edge spans, and CW/CCW chosen so both
  // up- and down-going edges bow to the SAME screen side.
  const a = HIT[e.from], b = HIT[e.to];
  let smooth = { type: "continuous", roundness: 0.35 };
  if (a && b && a.x === b.x) {
    const len = Math.abs(a.y - b.y) || 1;
    smooth = { type: a.y < b.y ? "curvedCW" : "curvedCCW", roundness: Math.min(0.65, 95 / len) };
  }
  return {
    id, from: e.from, to: e.to, bidir: e.bidir,
    arrows: { to: { enabled: true, scaleFactor: 0.45 },
              from: { enabled: e.bidir, scaleFactor: 0.45 } },
    color: { color: c, opacity: 0.3 },
    smooth,
  };
});
const edges = new vis.DataSet(edgeData);

const net = new vis.Network(document.getElementById("net"), { nodes, edges }, {
  physics: false,
  // hover:false — vis's built-in hover redraws every frame; we drive hover
  // ourselves from custom hitboxes below, one batched edge update per change.
  // selectable:false — we own selection state (pins); vis's built-in node/edge
  // selection would be a second, redundant focus outline on top of ours.
  interaction: { hover: false, dragNodes: false, selectable: false, navigationButtons: false },
});
net.once("afterDrawing", () => net.fit({ animation: false }));

// ── Single selection model ──────────────────────────────────────────────────
// Exactly one of three inputs drives the view, in this precedence: pinned nodes
// (persistent, click-toggled, multi-select) > hovered node (transient) > a
// legend section (isolate). Selecting one kind clears the others, so there's
// never a second overlapping focus state. Selected edges brighten but keep their
// own section color; unrelated edges fade; only pinned CIRCLES turn white.
const SEL_OP = 0.95, BASE_OP = 0.3, FADE_OP = 0.06;
const pinned = new Set();     // node ids
let section = null;           // legend-isolated section (persistent, click), or null
let hoverSection = null;      // legend chip previewed on hover (transient), or null
let hoverId = null;           // transiently hovered node id, or null
let dir = "both";             // held-modifier direction filter: "out" | "in" | "both"

function applyEdges() {
  // Directional inspect: hold Shift (outgoing) or Alt (incoming) while hovering
  // to show only the hovered node's links in that direction, ignoring pins.
  if (dir !== "both" && hoverId != null) {
    edges.update(edgeData.map((e) => {
      // A reciprocal (double-headed) edge counts as both outgoing and incoming.
      const match = e.bidir ? (e.from === hoverId || e.to === hoverId)
        : dir === "out" ? e.from === hoverId : e.to === hoverId;
      return { id: e.id, width: match ? 2 : 1,
        color: { color: baseColorOf[e.id], opacity: match ? SEL_OP : FADE_OP } };
    }));
    return;
  }
  // Hover layers on top of pins. An edge with BOTH endpoints in the selection
  // is a link fully inside it, so it goes brightest and bolder.
  const active = (pinned.size || hoverId != null) ? new Set(pinned) : null;
  if (active && hoverId != null) active.add(hoverId);
  // A hovered chip previews over a clicked isolate; Shift/Alt narrow it to edges
  // leaving / entering the section.
  const sec = active ? null : (hoverSection || section);
  edges.update(edgeData.map((e) => {
    let op, width = 1;
    if (active) {
      const fromA = active.has(e.from), toA = active.has(e.to);
      if (fromA && toA) { op = 1; width = 5; }
      else if (fromA || toA) op = SEL_OP;
      else op = FADE_OP;
    } else if (sec) {
      const inSec = (dir === "both" || e.bidir) ? (secOf[e.from] === sec || secOf[e.to] === sec)
        : dir === "out" ? secOf[e.from] === sec
        : secOf[e.to] === sec;
      op = inSec ? SEL_OP : FADE_OP;
    } else op = BASE_OP;
    return { id: e.id, width, color: { color: baseColorOf[e.id], opacity: op } };
  }));
}

// Node visuals change only on pin/section changes (not on hover), so this stays
// cheap. Pinned circles get a white border; section-isolate dims off-section.
function applyNodes() {
  const sec = pinned.size ? null : (hoverSection || section);
  nodes.update(nodeList.filter((n) => n.shape === "dot").map((n) => {
    const pin = pinned.has(n.id);
    return { id: n.id,
      opacity: sec && n.group !== sec ? 0.14 : 1,
      borderWidth: pin ? 3 : 1,
      color: { background: COLORS[n.group], border: pin ? "#ffffff" : "#0b0e13" } };
  }));
}

// Invisible parent hitbox per page: spans the dot AND the label below it, in
// canvas coordinates (zoom-independent, since node positions live in that space).
const meas = document.createElement("canvas").getContext("2d");
meas.font = "13px 'Segoe UI', Roboto, sans-serif";
const hitboxes = Object.entries(HIT).map(([id, h]) => {
  const halfW = Math.max(h.size, meas.measureText(h.label).width / 2) + 9;
  return { id, x: h.x, y: h.y,
    left: h.x - halfW, right: h.x + halfW,
    top: h.y - h.size - 6, bottom: h.y + h.size + 24 };
});
function locate(ev) {
  const rect = canvasEl.getBoundingClientRect();
  const c = net.DOMtoCanvas({ x: ev.clientX - rect.left, y: ev.clientY - rect.top });
  let best = null, bestD = Infinity;
  for (const h of hitboxes) {
    if (c.x >= h.left && c.x <= h.right && c.y >= h.top && c.y <= h.bottom) {
      const d = (c.x - h.x) ** 2 + (c.y - h.y) ** 2;
      if (d < bestD) { bestD = d; best = h; }
    }
  }
  return best;
}
const canvasEl = document.querySelector("#net canvas");
let downAt = null, lastPointer = null;
const openMode = (ev) => ev.ctrlKey || ev.metaKey;

// Single hover evaluator, driven by BOTH mouse moves and modifier key changes,
// using the last known pointer position. So pressing/releasing Ctrl/Cmd (open),
// Shift (outgoing) or Alt (incoming) re-evaluates the node under the cursor
// live — no need to move the mouse off and back on.
function updateHover(ev) {
  const h = lastPointer ? locate(lastPointer) : null;
  const id = h ? h.id : null;
  canvasEl.style.cursor = id ? (openMode(ev) ? "alias" : "pointer") : "default";
  const want = openMode(ev) ? null : id;      // no transient preview while in open mode
  const nd = ev.shiftKey ? "out" : ev.altKey ? "in" : "both";
  if (want !== hoverId || nd !== dir) { hoverId = want; dir = nd; applyEdges(); }
}
canvasEl.addEventListener("mousemove", (ev) => {
  lastPointer = { clientX: ev.clientX, clientY: ev.clientY };
  updateHover(ev);
});
window.addEventListener("keydown", updateHover);
window.addEventListener("keyup", updateHover);
canvasEl.addEventListener("mouseleave", () => {
  lastPointer = null;
  if (hoverId != null) { hoverId = null; applyEdges(); }
  canvasEl.style.cursor = "default";
});
canvasEl.addEventListener("mousedown", (ev) => { downAt = { x: ev.clientX, y: ev.clientY }; });
canvasEl.addEventListener("click", (ev) => {
  if (downAt && Math.hypot(ev.clientX - downAt.x, ev.clientY - downAt.y) > 4) return; // pan-drag
  const h = locate(ev);
  if (openMode(ev)) {                          // Ctrl/Cmd+click opens, never selects
    if (h) window.open(SITE + (h.id === "/" ? "/" : h.id), "_blank");
    return;
  }
  if (!h) { clearSelection(); return; }        // empty click clears everything
  if (section) setSection(null);               // pinning takes over from isolate
  pinned.has(h.id) ? pinned.delete(h.id) : pinned.add(h.id);
  applyNodes(); applyEdges();
});

// ── Legend: isolate a section (mutually exclusive with pins) ─────────────────
const legendPanel = document.getElementById("legend-panel");
legendPanel.querySelector(".lp-head").onclick = () => legendPanel.classList.toggle("collapsed");
const legend = document.getElementById("legend");
const counts = {};
DATA.nodes.forEach(n => counts[n.section] = (counts[n.section] || 0) + 1);
const chips = {};
function paintChips() {
  for (const [s, el] of Object.entries(chips)) el.style.background = s === section ? "#1b222c" : "";
}
function setSection(s) {
  if (section === s) return;
  section = s;
  paintChips(); applyNodes(); applyEdges();
}
function clearSelection() {
  if (!pinned.size && !section) return;
  pinned.clear(); section = null;
  paintChips(); applyNodes(); applyEdges();
}
["home", ...SECTIONS].forEach((s) => {
  if (!counts[s]) return;
  const el = document.createElement("span");
  el.innerHTML = '<i style="background:' + COLORS[s] + '"></i>' + s + ' (' + counts[s] + ')';
  el.onclick = () => {
    if (pinned.size) pinned.clear();           // isolate takes over from pins
    setSection(section === s ? null : s);
  };
  // Hover a chip to transiently preview its section (Shift/Alt narrow to
  // leaving/entering edges); mouse-out reverts to whatever was committed.
  el.onmouseenter = () => {
    if (!pinned.size && hoverSection !== s) { hoverSection = s; applyNodes(); applyEdges(); }
  };
  el.onmouseleave = () => {
    if (hoverSection !== null) { hoverSection = null; applyNodes(); applyEdges(); }
  };
  chips[s] = el;
  legend.appendChild(el);
});

document.getElementById("stats").textContent =
  DATA.nodes.length + " pages · " + DATA.edges.length + " links";
</script>
</body>
</html>`;
}
