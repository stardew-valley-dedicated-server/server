// Builds an interactive graph of every docs page and the links between them, one column per
// section. Everything is derived from the docs tree and the nav in config.ts; nothing is
// maintained per page. Run: `make docs-graph`, or `bun run graph` from docs/.
import { execSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

interface Page {
    id: string;
    file: string;
    section: string;
    title: string;
    content: string;
}

interface GraphNode {
    id: string;
    section: string;
    title: string;
}

interface Edge {
    from: string;
    to: string;
}

interface GraphData {
    nodes: GraphNode[];
    edges: Edge[];
}

const docsDir = path.resolve(import.meta.dirname, "..");
const repoRoot = path.resolve(docsDir, "..");
const configPath = path.join(docsDir, ".vitepress", "config.ts");
// Local-only artifact; `.output/` is gitignored.
const outPath = path.join(docsDir, ".output", "page-graph.html");

const DEFAULT_ORIGIN = "https://docs.junimoserver.com";
const HOME_SECTION = "home";

const SECTION_COLORS: Record<string, string> = {
    home: "#63dbe4",
    players: "#7ee787",
    admins: "#f2a65a",
    features: "#c084fc",
    developers: "#58a6ff",
    community: "#f778ba",
};
const PALETTE = [
    "#7ee787",
    "#f2a65a",
    "#c084fc",
    "#58a6ff",
    "#f778ba",
    "#e3b341",
    "#ff7b72",
    "#39c5cf",
    "#d2a8ff",
    "#ffa657",
];

// ── Docs tree → graph data ──────────────────────────────────────────────────

/** Public site URL and the nav's section order, both read from the VitePress config. */
function readSiteConfig(): { site: string; navSections: string[] } {
    const source = readFileSync(configPath, "utf8");
    const origin = source.match(/const origin\s*=\s*["']([^"']+)["']/)?.[1] ?? DEFAULT_ORIGIN;
    // Slice to the nav block first so sidebar links (same shape) don't leak in.
    const navBlock = source.slice(source.indexOf("nav:"), source.indexOf("sidebar:"));
    const navSections = [...navBlock.matchAll(/link:\s*["']\/([^/"']+)\//g)].map((m) => m[1]);
    return { site: origin.replace(/\/$/, ""), navSections: [...new Set(navSections)] };
}

/** Route id for a markdown file: `players/index.md` → `/players/`, `index.md` → `/`. */
function pageId(file: string): string {
    const route = `/${file.replace(/\\/g, "/")}`.replace(/\.md$/, "").replace(/\/index$/, "/");
    return route === "/index" ? "/" : route;
}

function collectPages(): Map<string, Page> {
    const files = execSync('git ls-files "*.md"', { cwd: docsDir })
        .toString()
        .trim()
        .split("\n")
        // `[param].md` dynamic-route templates expand at build time and have no page of their own.
        .filter(
            (f) =>
                !f.startsWith("node_modules") &&
                f !== "README.md" &&
                !f.includes("_partials") &&
                !path.basename(f).startsWith("["),
        );

    const pages = new Map<string, Page>();
    for (const file of files) {
        const id = pageId(file);
        const content = readFileSync(path.join(docsDir, file), "utf8");
        const heading = content.match(/^#\s+(.+)$/m) ?? content.match(/^title:\s*(.+)$/m);
        const title = heading ? heading[1].replace(/[`*]/g, "").trim() : id;
        const section = id === "/" ? HOME_SECTION : (id.split("/")[1] ?? HOME_SECTION);
        pages.set(id, { id, file, section, title, content });
    }
    return pages;
}

/** Normalizes a markdown link target into a route id relative to the linking page. */
function resolveLink(sourceId: string, rawLink: string): string | null {
    const link = rawLink.trim().split(/\s+/)[0].split("#")[0].split("?")[0]; // drop `"title"`, anchor, query
    if (!link) {
        return null;
    }
    const sourceDir = sourceId.endsWith("/") ? sourceId : sourceId.replace(/\/[^/]*$/, "/");
    const target = (link.startsWith("/") ? link : path.posix.normalize(sourceDir + link))
        .replace(/\.(md|html)$/, "")
        .replace(/\/index$/, "/");
    return target === "/" ? target : target.replace(/\/$/, "");
}

/** Matches a resolved route against the page set, tolerating a trailing-slash mismatch. */
function findPage(pages: Map<string, Page>, target: string): string | null {
    for (const candidate of [target, `${target}/`, target.replace(/\/$/, "")]) {
        if (pages.has(candidate)) {
            return candidate;
        }
    }
    return null;
}

const MARKDOWN_LINK = /\]\(([^)]+)\)/g;
const FRONTMATTER_LINK = /^\s*link:\s*(\S+)\s*$/gm; // hero action links live in frontmatter, not markdown
const FRONTMATTER_BLOCK = /^---\n([\s\S]*?)\n---/;
const EXTERNAL_LINK = /^(https?:|mailto:|#)/;

function collectEdges(pages: Map<string, Page>): Edge[] {
    const edges = new Map<string, Edge>();
    for (const page of pages.values()) {
        const frontmatter = page.content.match(FRONTMATTER_BLOCK)?.[1] ?? "";
        const links = [
            ...[...page.content.matchAll(MARKDOWN_LINK)].map((m) => m[1]),
            ...[...frontmatter.matchAll(FRONTMATTER_LINK)].map((m) => m[1]),
        ];
        for (const link of links) {
            if (EXTERNAL_LINK.test(link)) {
                continue;
            }
            const resolved = resolveLink(page.id, link);
            const to = resolved === null ? null : findPage(pages, resolved);
            if (to && to !== page.id) {
                edges.set(`${page.id}|${to}`, { from: page.id, to });
            }
        }
    }
    return [...edges.values()];
}

/** Nav order first, then any section folder not in the nav (sorted), so a new area still shows up. */
function orderSections(pages: Map<string, Page>, navSections: string[]): string[] {
    const present = new Set([...pages.values()].map((p) => p.section).filter((s) => s !== HOME_SECTION));
    const fromNav = navSections.filter((s) => present.has(s));
    const rest = [...present].filter((s) => !fromNav.includes(s)).sort();
    return [...fromNav, ...rest];
}

function sectionColors(sections: string[]): Record<string, string> {
    const colors: Record<string, string> = { [HOME_SECTION]: SECTION_COLORS[HOME_SECTION] };
    sections.forEach((s, i) => {
        colors[s] = SECTION_COLORS[s] ?? PALETTE[i % PALETTE.length];
    });
    return colors;
}

// ── Browser side ────────────────────────────────────────────────────────────
// `graphApp` is serialized into the page via `Function.prototype.toString()` (Bun emits transpiled
// JS), so it is type-checked here but must not reference module-scope values.

/** Subset of vis-network the page uses; the library is loaded from a CDN. */
interface VisDataSet<T> {
    update(items: Array<Partial<T> & { id: string }>): void;
}
interface VisNetwork {
    once(event: "afterDrawing", handler: () => void): void;
    fit(options: { animation: boolean }): void;
    DOMtoCanvas(point: { x: number; y: number }): { x: number; y: number };
}
declare const vis: {
    DataSet: new <T>(items: T[]) => VisDataSet<T>;
    Network: new (
        container: HTMLElement,
        data: { nodes: VisDataSet<VisNode>; edges: VisDataSet<VisEdge> },
        options: object,
    ) => VisNetwork;
};
interface VisNode {
    id: string;
    label: string;
    group: string;
    x: number;
    y: number;
    fixed: true;
    shape: "dot" | "text";
    title?: string;
    size?: number;
    opacity?: number;
    borderWidth?: number;
    color?: { background: string; border: string; highlight?: { background: string; border: string } };
    font: object;
}
interface VisEdge {
    id: string;
    from: string;
    to: string;
    /** Reciprocal link (A→B and B→A) drawn as one double-headed edge. */
    bidir: boolean;
    baseColor: string;
    arrows: object;
    color: { color: string; opacity: number };
    smooth: object;
    width?: number;
}

function graphApp(data: GraphData, colors: Record<string, string>, sections: string[], site: string): void {
    const COL_GAP = 480; // horizontal distance between section columns
    const ROW_GAP = 80; // vertical distance between stacked pages
    const HEADER_Y = -110; // section header row
    const SEL_OP = 0.95;
    const BASE_OP = 0.3;
    const FADE_OP = 0.06;
    const FONT = "Segoe UI";
    const NODE_BORDER = "#0b0e13";
    const CHIP_ACTIVE_BG = "#1b222c";

    const sectionOf = new Map(data.nodes.map((n) => [n.id, n.section]));
    const degree = new Map<string, number>();
    for (const e of data.edges) {
        degree.set(e.from, (degree.get(e.from) ?? 0) + 1);
        degree.set(e.to, (degree.get(e.to) ?? 0) + 1);
    }

    // ── Layout: fixed columns (physics off), pages sorted by URL so subpages group together.
    const pagesBySection = new Map<string, GraphNode[]>(sections.map((s) => [s, []]));
    for (const n of data.nodes) {
        pagesBySection.get(n.section)?.push(n);
    }
    for (const list of pagesBySection.values()) {
        list.sort((a, b) => a.id.localeCompare(b.id));
    }

    // Pages are dots with the label below; anchors feed the hitboxes that make dot + label clickable.
    interface Anchor {
        x: number;
        y: number;
        size: number;
        label: string;
    }
    const anchors = new Map<string, Anchor>();
    const visNodes: VisNode[] = [];

    function pageNode(id: string, label: string, section: string, x: number, y: number): VisNode {
        const size = 8 + Math.min(degree.get(id) ?? 0, 12) * 1.4;
        anchors.set(id, { x, y, size, label });
        return {
            id,
            label,
            group: section,
            title: id,
            x,
            y,
            fixed: true,
            shape: "dot",
            size,
            color: {
                background: colors[section],
                border: NODE_BORDER,
                highlight: { background: colors[section], border: "#ffffff" },
            },
            font: { color: "#c9d1d9", size: 13, face: FONT },
        };
    }

    // Home sits left of the columns, level with each section's landing page.
    visNodes.push(pageNode("/", "Home", "home", -COL_GAP, 0));
    sections.forEach((s, col) => {
        const x = col * COL_GAP;
        visNodes.push({
            id: `__hdr_${s}`,
            label: s.toUpperCase(),
            group: s,
            x,
            y: HEADER_Y,
            fixed: true,
            shape: "text",
            font: { color: colors[s], size: 20, face: FONT, bold: { color: colors[s], size: 20 } },
        });
        pagesBySection.get(s)?.forEach((n, row) => {
            visNodes.push(pageNode(n.id, n.title, s, x, row * ROW_GAP));
        });
    });

    // ── Edges: A→B plus B→A collapse into one double-headed edge, colored by the first-seen source.
    const directed = new Set(data.edges.map((e) => `${e.from}|${e.to}`));
    const merged = new Set<string>();
    const visEdges: VisEdge[] = [];
    for (const e of data.edges) {
        const key = `${e.from}|${e.to}`;
        const reverse = `${e.to}|${e.from}`;
        if (merged.has(key) || merged.has(reverse)) {
            continue;
        }
        const bidir = directed.has(reverse);
        merged.add(key);
        if (bidir) {
            merged.add(reverse);
        }

        // Same-column edges would cut straight through the dots between them: bow them to one side,
        // with roundness scaled by length so the bulge stays bounded, and CW/CCW picked so up- and
        // down-going edges bow the same way.
        const a = anchors.get(e.from);
        const b = anchors.get(e.to);
        let smooth: object = { type: "continuous", roundness: 0.35 };
        if (a && b && a.x === b.x) {
            const length = Math.abs(a.y - b.y) || 1;
            smooth = { type: a.y < b.y ? "curvedCW" : "curvedCCW", roundness: Math.min(0.65, 95 / length) };
        }

        const baseColor = colors[sectionOf.get(e.from) ?? ""] ?? "#8b949e";
        visEdges.push({
            id: `e${visEdges.length}`,
            from: e.from,
            to: e.to,
            bidir,
            baseColor,
            arrows: {
                to: { enabled: true, scaleFactor: 0.45 },
                from: { enabled: bidir, scaleFactor: 0.45 },
            },
            color: { color: baseColor, opacity: BASE_OP },
            smooth,
        });
    }

    const nodes = new vis.DataSet(visNodes);
    const edges = new vis.DataSet(visEdges);
    const container = document.getElementById("net") as HTMLElement;
    const net = new vis.Network(
        container,
        { nodes, edges },
        {
            physics: false,
            // Hover and selection are handled below via hitboxes; vis's built-ins would redraw every
            // frame and draw a second focus outline.
            interaction: { hover: false, dragNodes: false, selectable: false, navigationButtons: false },
        },
    );
    net.once("afterDrawing", () => net.fit({ animation: false }));

    // ── Selection model: pinned nodes > hovered node > isolated legend section. Selecting one kind
    // clears the others, so there is never an overlapping focus state.
    type Direction = "out" | "in" | "both";
    const pinned = new Set<string>();
    let activeSection: string | null = null; // legend-isolated section (persistent, click)
    let hoverSection: string | null = null; // legend chip previewed on hover (transient)
    let hoverId: string | null = null; // transiently hovered node id
    let direction: Direction = "both"; // held-modifier filter: Shift = out, Alt = in

    function applyEdges(): void {
        // Shift/Alt while hovering: only that node's outgoing/incoming links, ignoring pins.
        if (direction !== "both" && hoverId !== null) {
            const id = hoverId;
            edges.update(
                visEdges.map((e) => {
                    const match = e.bidir
                        ? e.from === id || e.to === id
                        : direction === "out"
                          ? e.from === id
                          : e.to === id;
                    return {
                        id: e.id,
                        width: match ? 2 : 1,
                        color: { color: e.baseColor, opacity: match ? SEL_OP : FADE_OP },
                    };
                }),
            );
            return;
        }
        // Hover layers on top of pins; an edge with both endpoints selected is brightest and bolder.
        const active = pinned.size || hoverId !== null ? new Set(pinned) : null;
        if (active && hoverId !== null) {
            active.add(hoverId);
        }
        const isolated = active ? null : (hoverSection ?? activeSection);
        edges.update(
            visEdges.map((e) => {
                let opacity = BASE_OP;
                let width = 1;
                if (active) {
                    const fromActive = active.has(e.from);
                    const toActive = active.has(e.to);
                    if (fromActive && toActive) {
                        opacity = 1;
                        width = 5;
                    } else {
                        opacity = fromActive || toActive ? SEL_OP : FADE_OP;
                    }
                } else if (isolated) {
                    const fromSection = sectionOf.get(e.from) === isolated;
                    const toSection = sectionOf.get(e.to) === isolated;
                    const inSection =
                        direction === "both" || e.bidir
                            ? fromSection || toSection
                            : direction === "out"
                              ? fromSection
                              : toSection;
                    opacity = inSection ? SEL_OP : FADE_OP;
                }
                return { id: e.id, width, color: { color: e.baseColor, opacity } };
            }),
        );
    }

    // Pinned circles get a white border; isolating a section dims the rest. Not called on hover.
    function applyNodes(): void {
        const isolated = pinned.size ? null : (hoverSection ?? activeSection);
        nodes.update(
            visNodes
                .filter((n) => n.shape === "dot")
                .map((n) => {
                    const pin = pinned.has(n.id);
                    return {
                        id: n.id,
                        opacity: isolated && n.group !== isolated ? 0.14 : 1,
                        borderWidth: pin ? 3 : 1,
                        color: { background: colors[n.group], border: pin ? "#ffffff" : NODE_BORDER },
                    };
                }),
        );
    }

    function setSection(next: string | null): void {
        if (activeSection === next) {
            return;
        }
        activeSection = next;
        paintChips();
        applyNodes();
        applyEdges();
    }

    function clearSelection(): void {
        if (!pinned.size && !activeSection) {
            return;
        }
        pinned.clear();
        activeSection = null;
        paintChips();
        applyNodes();
        applyEdges();
    }

    // ── Pointer handling: one hitbox per page spanning dot + label, in zoom-independent canvas space.
    const measure = document.createElement("canvas").getContext("2d") as CanvasRenderingContext2D;
    measure.font = `13px '${FONT}', Roboto, sans-serif`;
    const hitboxes = [...anchors].map(([id, h]) => {
        const halfWidth = Math.max(h.size, measure.measureText(h.label).width / 2) + 9;
        return {
            id,
            x: h.x,
            y: h.y,
            left: h.x - halfWidth,
            right: h.x + halfWidth,
            top: h.y - h.size - 6,
            bottom: h.y + h.size + 24,
        };
    });
    const canvas = container.querySelector("canvas") as HTMLCanvasElement;

    /** The hitbox under a client-space point, nearest anchor winning where boxes overlap. */
    function locate(point: { clientX: number; clientY: number }): (typeof hitboxes)[number] | null {
        const rect = canvas.getBoundingClientRect();
        const c = net.DOMtoCanvas({ x: point.clientX - rect.left, y: point.clientY - rect.top });
        let best: (typeof hitboxes)[number] | null = null;
        let bestDistance = Number.POSITIVE_INFINITY;
        for (const h of hitboxes) {
            if (c.x < h.left || c.x > h.right || c.y < h.top || c.y > h.bottom) {
                continue;
            }
            const distance = (c.x - h.x) ** 2 + (c.y - h.y) ** 2;
            if (distance < bestDistance) {
                bestDistance = distance;
                best = h;
            }
        }
        return best;
    }

    const isOpenMode = (ev: KeyboardEvent | MouseEvent): boolean => ev.ctrlKey || ev.metaKey;
    let lastPointer: { clientX: number; clientY: number } | null = null;
    let mouseDownAt: { x: number; y: number } | null = null;

    // Driven by mouse moves AND modifier key changes, so pressing Ctrl/Shift/Alt re-evaluates the
    // node under the cursor without moving the mouse.
    function updateHover(ev: KeyboardEvent | MouseEvent): void {
        const hit = lastPointer ? locate(lastPointer) : null;
        const id = hit ? hit.id : null;
        canvas.style.cursor = id ? (isOpenMode(ev) ? "alias" : "pointer") : "default";
        const wanted = isOpenMode(ev) ? null : id; // no transient preview while in open mode
        const nextDirection: Direction = ev.shiftKey ? "out" : ev.altKey ? "in" : "both";
        if (wanted !== hoverId || nextDirection !== direction) {
            hoverId = wanted;
            direction = nextDirection;
            applyEdges();
        }
    }
    canvas.addEventListener("mousemove", (ev) => {
        lastPointer = { clientX: ev.clientX, clientY: ev.clientY };
        updateHover(ev);
    });
    window.addEventListener("keydown", updateHover);
    window.addEventListener("keyup", updateHover);
    canvas.addEventListener("mouseleave", () => {
        lastPointer = null;
        canvas.style.cursor = "default";
        if (hoverId !== null) {
            hoverId = null;
            applyEdges();
        }
    });
    canvas.addEventListener("mousedown", (ev) => {
        mouseDownAt = { x: ev.clientX, y: ev.clientY };
    });
    canvas.addEventListener("click", (ev) => {
        if (mouseDownAt && Math.hypot(ev.clientX - mouseDownAt.x, ev.clientY - mouseDownAt.y) > 4) {
            return; // pan-drag, not a click
        }
        const hit = locate(ev);
        if (isOpenMode(ev)) {
            if (hit) {
                window.open(site + (hit.id === "/" ? "/" : hit.id), "_blank");
            }
            return;
        }
        if (!hit) {
            clearSelection(); // empty click clears everything
            return;
        }
        if (activeSection) {
            setSection(null); // pinning takes over from isolate
        }
        if (pinned.has(hit.id)) {
            pinned.delete(hit.id);
        } else {
            pinned.add(hit.id);
        }
        applyNodes();
        applyEdges();
    });

    // ── Legend: isolate a section (mutually exclusive with pins) ────────────
    const legendPanel = document.getElementById("legend-panel") as HTMLElement;
    (legendPanel.querySelector(".lp-head") as HTMLElement).onclick = () => legendPanel.classList.toggle("collapsed");
    const legend = document.getElementById("legend") as HTMLElement;
    const pageCounts = new Map<string, number>();
    for (const n of data.nodes) {
        pageCounts.set(n.section, (pageCounts.get(n.section) ?? 0) + 1);
    }
    const chips = new Map<string, HTMLElement>();

    function paintChips(): void {
        for (const [s, chip] of chips) {
            chip.style.background = s === activeSection ? CHIP_ACTIVE_BG : "";
        }
    }

    for (const s of ["home", ...sections]) {
        const count = pageCounts.get(s);
        if (!count) {
            continue;
        }
        const chip = document.createElement("span");
        const swatch = document.createElement("i");
        swatch.style.background = colors[s];
        chip.append(swatch, `${s} (${count})`);
        chip.onclick = () => {
            pinned.clear(); // isolate takes over from pins
            setSection(activeSection === s ? null : s);
        };
        chip.onmouseenter = () => {
            if (!pinned.size && hoverSection !== s) {
                hoverSection = s;
                applyNodes();
                applyEdges();
            }
        };
        chip.onmouseleave = () => {
            if (hoverSection !== null) {
                hoverSection = null;
                applyNodes();
                applyEdges();
            }
        };
        chips.set(s, chip);
        legend.appendChild(chip);
    }

    (document.getElementById("stats") as HTMLElement).textContent =
        `${data.nodes.length} pages · ${data.edges.length} links`;
}

// ── HTML shell ──────────────────────────────────────────────────────────────

/** JSON safe inside an inline `<script>`: `<` is escaped so a `</script>` in a title can't break out. */
function scriptJson(value: unknown): string {
    return JSON.stringify(value).replace(/</g, "\\u003c");
}

function renderHtml(data: GraphData, colors: Record<string, string>, sections: string[], site: string): string {
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
(${graphApp.toString()})(${scriptJson(data)}, ${scriptJson(colors)}, ${scriptJson(sections)}, ${scriptJson(site)});
</script>
</body>
</html>`;
}

// ── Main ────────────────────────────────────────────────────────────────────

const config = readSiteConfig();
const pageMap = collectPages();
const sectionOrder = orderSections(pageMap, config.navSections);
const graph: GraphData = {
    nodes: [...pageMap.values()].map(({ id, section, title }) => ({ id, section, title })),
    edges: collectEdges(pageMap),
};

mkdirSync(path.dirname(outPath), { recursive: true });
writeFileSync(outPath, renderHtml(graph, sectionColors(sectionOrder), sectionOrder, config.site));
console.log(`Wrote ${path.relative(repoRoot, outPath)} — ${graph.nodes.length} pages, ${graph.edges.length} links`);
console.log(`Sections: ${sectionOrder.join(", ")}`);
