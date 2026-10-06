import L from "leaflet";
import { LEFT, TILE, TOP } from "../../shared/mapFrame";
import { type Base, type ClassName, gameLinkKind, gameNodeKind, isAdded, linkClass, linkHidden, LINK, NODE, nodeClass, nodeKindOf, nodePos, pairKey, type Shown, type View } from "./model";

// The left map of the road editor: the game's nodes and links with the edits, drawn on canvas tiles (seventy thousand
// links do not go into Leaflet one by one). Colours by kind: normal, highway, tunnel,
// unpaved, switched off; lane-change links dashed, one-way arrows, junction nodes with a dark rim. The kinds the legend
// leaves out are neither drawn nor picked.

export const COLOURS = {
  normal: "#d61f69",
  highway: "#f08c00",
  tunnel: "#1c64f2",
  unpaved: "#8d5a2b",
  switchedOff: "#8b95a1",
  added: "#16a34a",
  edited: "#06b6d4",
  selected: "#ffd400",
} as const;

/** Zooms from which nodes and one-way arrows are drawn. */
export const NODE_ZOOM = 6;
export const ARROW_ZOOM = 7;

export interface Selection {
  nodes: ReadonlySet<string>;
  links: ReadonlySet<string>;
}

/** What is under the pointer. */
export type Picked = { kind: "node"; key: string } | { kind: "link"; key: string; from: string; to: string };

export interface PathsState {
  base: Base | null;
  view: View | null;
  selection: Selection;
  hover: Picked | null;
  /** What a click would select, marked instead of the item under the pointer (the road tool: the road or the street). */
  hoverSet: Selection | null;
  /** Nodes being dragged: key -> position shown meanwhile. */
  drag: Map<string, { x: number; y: number }> | null;
  /** The chain being drawn: its last node and the pointer. */
  sketch: { from: string; x: number; y: number } | null;
  /** The kinds the left map shows. */
  shown: Shown;
}

interface Tile {
  canvas: HTMLCanvasElement;
  coords: L.Coords;
}

export interface PathsLayer extends L.GridLayer {
  state: PathsState;
  /** Draws every tile shown again (after the data, the selection or the pointer changed). */
  repaint(): void;
}

/** Metres a pixel at a zoom. */
export const metresPerPixel = (zoom: number) => TILE / 2 ** zoom;

export function createPathsLayer(state: PathsState): PathsLayer {
  const tiles = new Map<string, Tile>();
  const Layer = L.GridLayer.extend({
    createTile(coords: L.Coords) {
      const canvas = document.createElement("canvas");
      const dpr = window.devicePixelRatio || 1;
      canvas.width = 256 * dpr;
      canvas.height = 256 * dpr;
      canvas.style.width = "256px";
      canvas.style.height = "256px";
      canvas.dataset.tile = `${coords.x}:${coords.y}:${coords.z}`;
      tiles.set(`${coords.x}:${coords.y}:${coords.z}`, { canvas, coords });
      draw(canvas, coords, (this as PathsLayer).state);
      return canvas;
    },
  });
  const Made = Layer as unknown as new (options: L.GridLayerOptions) => PathsLayer;
  const layer = new Made({ tileSize: 256, noWrap: true, updateWhenZooming: false, keepBuffer: 1 });
  layer.state = state;
  layer.on("tileunload", (e: L.TileEvent) => tiles.delete(`${e.coords.x}:${e.coords.y}:${e.coords.z}`));
  layer.repaint = () => {
    for (const t of tiles.values()) draw(t.canvas, t.coords, layer.state);
  };
  return layer;
}

function draw(canvas: HTMLCanvasElement, coords: L.Coords, s: PathsState) {
  const ctx = canvas.getContext("2d")!;
  const dpr = canvas.width / 256;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, 256, 256);
  const { base, view, shown } = s;
  if (!base || !view) return;
  const d = base.dto;
  const z = coords.z;
  const ppm = 1 / metresPerPixel(z);
  const x0 = LEFT + (coords.x * 256) / ppm, y0 = TOP - (coords.y * 256) / ppm;
  const px = (x: number) => (x - x0) * ppm, py = (y: number) => (y0 - y) * ppm;
  const pad = 12 / ppm;
  const w = { x0: x0 - pad, y0: y0 - 256 / ppm - pad, x1: x0 + 256 / ppm + pad, y1: y0 + pad };
  const wide = z >= 5;
  const lineWidth = wide ? 2.5 : z >= 3 ? 1.6 : 1.1;
  const pos = (key: string) => {
    const dragged = s.drag?.get(key);
    if (dragged) return dragged;
    return nodePos(base, view, key);
  };
  const seen = new Set<number>();

  // game links not touched by the edits, by class
  ctx.lineCap = "round";
  const byClass = new Map<string, number[]>();
  for (const j of base.linkBuckets.near(w.x0, w.y0, w.x1, w.y1)) {
    if (seen.has(j)) continue;
    seen.add(j);
    if (view.touched.has(j) || (s.drag && (s.drag.has(d.keys[d.linkA[j]]) || s.drag.has(d.keys[d.linkB[j]])))) continue;
    const a = d.linkA[j], b = d.linkB[j];
    const name = linkClass(d.flags[a], d.flags[b]);
    if (!shown[name]) continue;
    const cls = name + ((d.linkFlags[j] & LINK.shortcut) !== 0 ? ":sc" : "");
    let l = byClass.get(cls);
    if (!l) byClass.set(cls, (l = []));
    l.push(j);
  }
  for (const [cls, list] of byClass) {
    const [name, lc] = cls.split(":");
    ctx.strokeStyle = COLOURS[name as ClassName];
    ctx.lineWidth = lc ? Math.max(1, lineWidth * 0.6) : lineWidth;
    ctx.setLineDash(lc ? [4, 4] : []);
    ctx.globalAlpha = 0.95;
    ctx.beginPath();
    for (const j of list) {
      const a = d.linkA[j], b = d.linkB[j];
      ctx.moveTo(px(d.x[a]), py(d.y[a]));
      ctx.lineTo(px(d.x[b]), py(d.y[b]));
    }
    ctx.stroke();
  }
  ctx.setLineDash([]);

  // the touched game links and the added links, with the edits
  const segment = (a: { x: number; y: number }, b: { x: number; y: number }) => {
    ctx.beginPath();
    ctx.moveTo(px(a.x), py(a.y));
    ctx.lineTo(px(b.x), py(b.y));
    ctx.stroke();
  };
  const inWindow = (a: { x: number; y: number }, b: { x: number; y: number }) =>
    Math.max(a.x, b.x) >= w.x0 && Math.min(a.x, b.x) <= w.x1 && Math.max(a.y, b.y) >= w.y0 && Math.min(a.y, b.y) <= w.y1;
  const touched = new Set(view.touched);
  if (s.drag)
    for (const key of s.drag.keys()) {
      const i = base.index.get(key);
      if (i !== undefined) for (const j of base.nodeLinks[i]) touched.add(j);
    }
  for (const j of touched) {
    const ka = d.keys[d.linkA[j]], kb = d.keys[d.linkB[j]];
    const a = pos(ka), b = pos(kb);
    if (!a || !b || !inWindow(a, b)) continue;
    const hidden = linkHidden(base, view, j);
    const edited = view.links.has(j) || !!view.nodes.get(d.linkA[j])?.moved || !!view.nodes.get(d.linkB[j])?.moved || !!s.drag?.has(ka) || !!s.drag?.has(kb);
    const cls = linkClass(d.flags[d.linkA[j]], d.flags[d.linkB[j]]);
    if (!shown[hidden ? "hidden" : edited ? "edited" : cls]) continue;
    ctx.strokeStyle = hidden ? COLOURS[cls] : edited ? COLOURS.edited : COLOURS[cls];
    ctx.globalAlpha = hidden ? 0.35 : 1;
    ctx.lineWidth = lineWidth;
    ctx.setLineDash(hidden ? [3, 4] : []);
    segment(a, b);
  }
  ctx.setLineDash([]);
  ctx.globalAlpha = 1;
  ctx.strokeStyle = COLOURS.added;
  ctx.lineWidth = lineWidth + 0.5;
  if (shown.added)
    for (const l of view.addedLinks) {
      const a = pos(l.from), b = pos(l.to);
      if (a && b && inWindow(a, b)) segment(a, b);
    }
  if (s.sketch) {
    const a = pos(s.sketch.from);
    if (a) {
      ctx.setLineDash([6, 5]);
      ctx.strokeStyle = COLOURS.added;
      segment(a, s.sketch);
      ctx.setLineDash([]);
    }
  }

  // nodes and arrows when close enough
  if (z >= NODE_ZOOM) {
    const dot = (p: { x: number; y: number }, fill: string, junction: boolean, alpha: number) => {
      ctx.globalAlpha = alpha;
      ctx.beginPath();
      ctx.arc(px(p.x), py(p.y), junction ? 4.5 : 3, 0, Math.PI * 2);
      ctx.fillStyle = fill;
      ctx.fill();
      ctx.lineWidth = junction ? 1.5 : 1;
      ctx.strokeStyle = junction ? "#111111" : "#ffffff";
      ctx.stroke();
    };
    const seenNodes = new Set<number>();
    for (const i of base.nodeBuckets.near(w.x0, w.y0, w.x1, w.y1)) {
      if (seenNodes.has(i)) continue;
      seenNodes.add(i);
      const key = d.keys[i];
      if (view.nodes.has(i) || s.drag?.has(key)) continue;
      const cls = nodeClass(d.flags[i]);
      if (shown[cls]) dot({ x: d.x[i], y: d.y[i] }, COLOURS[cls], (d.flags[i] & NODE.junction) !== 0, 1);
    }
    for (const [i, n] of view.nodes) {
      const key = d.keys[i];
      const p = s.drag?.get(key) ?? n;
      if (p.x < w.x0 || p.x > w.x1 || p.y < w.y0 || p.y > w.y1) continue;
      const edited = n.moved || n.changed || !!s.drag?.has(key);
      if (!shown[n.hidden ? "hidden" : edited ? "edited" : nodeClass(n.flags)]) continue;
      dot(p, n.hidden ? COLOURS[nodeClass(n.flags)] : edited ? COLOURS.edited : COLOURS[nodeClass(n.flags)], (n.flags & NODE.junction) !== 0, n.hidden ? 0.35 : 1);
    }
    if (shown.added)
      for (const [key, n] of view.added) {
        const p = s.drag?.get(key) ?? n;
        if (p.x < w.x0 || p.x > w.x1 || p.y < w.y0 || p.y > w.y1) continue;
        dot(p, COLOURS.added, false, 1);
      }
    ctx.globalAlpha = 1;
    if (z >= ARROW_ZOOM) drawArrows(ctx, base, view, shown, w, px, py, pos);
  }

  // the selection (also what the left map does not show: chosen from the list of edits) and the item under the pointer
  const mark = (p: Picked, colour: string, width: number) => {
    ctx.strokeStyle = colour;
    ctx.lineWidth = width;
    ctx.globalAlpha = 0.9;
    if (p.kind === "node") {
      const n = pos(p.key);
      if (!n) return;
      ctx.beginPath();
      ctx.arc(px(n.x), py(n.y), 8, 0, Math.PI * 2);
      ctx.stroke();
    } else {
      const a = pos(p.from), b = pos(p.to);
      if (!a || !b || !inWindow(a, b)) return;
      ctx.globalAlpha = 0.6;
      ctx.lineWidth = lineWidth + width + 3;
      segment(a, b);
    }
  };
  for (const key of s.selection.links) {
    const [from, to] = key.split(" ");
    mark({ kind: "link", key, from, to }, COLOURS.selected, 3);
  }
  for (const key of s.selection.nodes) mark({ kind: "node", key }, COLOURS.selected, 3);
  if (s.hoverSet) {
    for (const key of s.hoverSet.links) {
      const [from, to] = key.split(" ");
      mark({ kind: "link", key, from, to }, "#ffffff", 2);
    }
    for (const key of s.hoverSet.nodes) mark({ kind: "node", key }, "#ffffff", 2);
  } else if (s.hover) mark(s.hover, "#ffffff", 2);
  ctx.globalAlpha = 1;
}

function drawArrows(
  ctx: CanvasRenderingContext2D,
  base: Base,
  view: View,
  shown: Shown,
  w: { x0: number; y0: number; x1: number; y1: number },
  px: (x: number) => number,
  py: (y: number) => number,
  pos: (key: string) => { x: number; y: number } | null,
) {
  const d = base.dto;
  const size = 6;
  ctx.strokeStyle = "#111111";
  ctx.lineWidth = 1.8;
  ctx.globalAlpha = 0.9;
  const arrow = (a: { x: number; y: number }, b: { x: number; y: number }) => {
    const ax = px(a.x), ay = py(a.y), bx = px(b.x), by = py(b.y);
    const dx = bx - ax, dy = by - ay, len = Math.hypot(dx, dy);
    if (len < 16) return;
    const ux = dx / len, uy = dy / len;
    const tx = (ax + bx) / 2 + (ux * size) / 2, ty = (ay + by) / 2 + (uy * size) / 2;
    const cx = tx - ux * size, cy = ty - uy * size, nx = -uy * size * 0.6, ny = ux * size * 0.6;
    ctx.beginPath();
    ctx.moveTo(cx + nx, cy + ny);
    ctx.lineTo(tx, ty);
    ctx.lineTo(cx - nx, cy - ny);
    ctx.stroke();
  };
  const oneWay = (from: string, to: string, lf: number, lb: number) => {
    if ((lf > 0) === (lb > 0)) return;
    const a = pos(from), b = pos(to);
    if (!a || !b) return;
    if (Math.max(a.x, b.x) < w.x0 || Math.min(a.x, b.x) > w.x1 || Math.max(a.y, b.y) < w.y0 || Math.min(a.y, b.y) > w.y1) return;
    if (lf > 0) arrow(a, b);
    else arrow(b, a);
  };
  const seen = new Set<number>();
  for (const j of base.linkBuckets.near(w.x0, w.y0, w.x1, w.y1)) {
    if (seen.has(j)) continue;
    seen.add(j);
    if (linkHidden(base, view, j) || !shown[gameLinkKind(base, view, j)]) continue;
    const st = view.links.get(j);
    oneWay(d.keys[d.linkA[j]], d.keys[d.linkB[j]], st?.lanesForward ?? d.lanesForward[j], st?.lanesBack ?? d.lanesBack[j]);
  }
  if (shown.added) for (const l of view.addedLinks) oneWay(l.from, l.to, l.lanesForward, l.lanesBack);
}

/**
 * The node (within `nodePx`) or else the link (within `linkPx`) nearest to a point, of the kinds the left map shows
 * (hidden ones included when it shows them); nodes only from the zoom they are drawn at.
 */
export function pick(base: Base, view: View, x: number, y: number, zoom: number, shown: Shown, nodePx = 9, linkPx = 7): Picked | null {
  const mpp = metresPerPixel(zoom);
  const d = base.dto;
  if (zoom >= NODE_ZOOM) {
    const r = nodePx * mpp;
    let best: string | null = null, bestD = r;
    const consider = (key: string, nx: number, ny: number) => {
      const dd = Math.hypot(nx - x, ny - y);
      if (dd <= bestD) {
        bestD = dd;
        best = key;
      }
    };
    for (const i of base.nodeBuckets.near(x - r, y - r, x + r, y + r))
      if (!view.nodes.has(i) && shown[nodeClass(d.flags[i])]) consider(d.keys[i], d.x[i], d.y[i]);
    for (const [i, n] of view.nodes) if (shown[gameNodeKind(base, view, i)]) consider(d.keys[i], n.x, n.y);
    if (shown.added) for (const [key, n] of view.added) consider(key, n.x, n.y);
    if (best) return { kind: "node", key: best };
  }
  const r = linkPx * mpp;
  let best: Picked | null = null, bestD = r;
  const consider = (from: string, to: string) => {
    const a = nodePos(base, view, from), b = nodePos(base, view, to);
    if (!a || !b) return;
    const dd = distanceToSegment(x, y, a.x, a.y, b.x, b.y);
    if (dd <= bestD) {
      bestD = dd;
      best = { kind: "link", key: pairKey(from, to), from, to };
    }
  };
  for (const j of base.linkBuckets.near(x - r, y - r, x + r, y + r))
    if (!view.touched.has(j) && shown[linkClass(d.flags[d.linkA[j]], d.flags[d.linkB[j]])]) consider(d.keys[d.linkA[j]], d.keys[d.linkB[j]]);
  for (const j of view.touched) if (shown[gameLinkKind(base, view, j)]) consider(d.keys[d.linkA[j]], d.keys[d.linkB[j]]);
  if (shown.added)
    for (const k of view.addedBuckets.near(x - r, y - r, x + r, y + r)) {
      const l = view.addedLinks[k];
      consider(l.from, l.to);
    }
  return best;
}

export function distanceToSegment(x: number, y: number, ax: number, ay: number, bx: number, by: number): number {
  const dx = bx - ax, dy = by - ay;
  const len2 = dx * dx + dy * dy;
  const t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
  return Math.hypot(x - (ax + t * dx), y - (ay + t * dy));
}

/**
 * The nodes of the kinds the left map shows inside an area (its bounds west, south, east, north, and whether a point is
 * in it), and the links it shows with both ends inside.
 */
function inArea(base: Base, view: View, shown: Shown, x0: number, y0: number, x1: number, y1: number, inside: (x: number, y: number) => boolean) {
  const d = base.dto;
  const nodes = new Set<string>();
  for (const i of base.nodeBuckets.near(x0, y0, x1, y1))
    if (!view.nodes.has(i) && shown[nodeClass(d.flags[i])] && inside(d.x[i], d.y[i])) nodes.add(d.keys[i]);
  for (const [i, n] of view.nodes) if (shown[gameNodeKind(base, view, i)] && inside(n.x, n.y)) nodes.add(d.keys[i]);
  if (shown.added) for (const [key, n] of view.added) if (inside(n.x, n.y)) nodes.add(key);
  // a link by where its ends are, shown or not
  const within = (key: string) => {
    const p = nodePos(base, view, key);
    return !!p && inside(p.x, p.y);
  };
  const links = new Set<string>();
  for (const j of base.linkBuckets.near(x0, y0, x1, y1)) {
    if (view.touched.has(j)) continue;
    const a = d.linkA[j], b = d.linkB[j];
    if (shown[linkClass(d.flags[a], d.flags[b])] && inside(d.x[a], d.y[a]) && inside(d.x[b], d.y[b])) links.add(pairKey(d.keys[a], d.keys[b]));
  }
  for (const j of view.touched) {
    const a = d.keys[d.linkA[j]], b = d.keys[d.linkB[j]];
    if (shown[gameLinkKind(base, view, j)] && within(a) && within(b)) links.add(pairKey(a, b));
  }
  if (shown.added) for (const l of view.addedLinks) if (within(l.from) && within(l.to)) links.add(pairKey(l.from, l.to));
  return { nodes: [...nodes], links: [...links] };
}

/** The nodes inside a rectangle (west, south, east, north) and the links with both ends inside, of the kinds shown. */
export function inRectangle(base: Base, view: View, shown: Shown, x0: number, y0: number, x1: number, y1: number): { nodes: string[]; links: string[] } {
  return inArea(base, view, shown, x0, y0, x1, y1, (x, y) => x >= x0 && x <= x1 && y >= y0 && y <= y1);
}

/** Whether a point is inside a polygon (its corners in order; the last joins the first). */
export function insidePolygon(corners: readonly { x: number; y: number }[], x: number, y: number): boolean {
  let inside = false;
  for (let i = 0, j = corners.length - 1; i < corners.length; j = i++) {
    const a = corners[i], b = corners[j];
    if (a.y > y !== b.y > y && x < ((b.x - a.x) * (y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
  }
  return inside;
}

/** The nodes inside a traced outline and the links with both ends inside, of the kinds shown. */
export function inPolygon(base: Base, view: View, shown: Shown, corners: readonly { x: number; y: number }[]): { nodes: string[]; links: string[] } {
  if (corners.length < 3) return { nodes: [], links: [] };
  const xs = corners.map((c) => c.x), ys = corners.map((c) => c.y);
  return inArea(base, view, shown, Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys), (x, y) => insidePolygon(corners, x, y));
}

// ------------------------------------------------------------------------------------------------ the road tool

/** A link a road runs on: not hidden, not a shortcut, of a kind the left map shows. */
interface RoadLink {
  from: string;
  to: string;
  key: string;
}

/** Added links by their end nodes, per view (made when first asked). */
const addedEnds = new WeakMap<View, Map<string, number[]>>();

function addedAt(view: View, key: string): number[] {
  let m = addedEnds.get(view);
  if (!m) {
    m = new Map();
    view.addedLinks.forEach((l, k) => {
      for (const end of [l.from, l.to]) {
        const list = m!.get(end);
        if (list) list.push(k);
        else m!.set(end, [k]);
      }
    });
    addedEnds.set(view, m);
  }
  return m.get(key) ?? [];
}

/** The links a road runs on at a node (none at a hidden node). */
function roadLinksAt(base: Base, view: View, shown: Shown, key: string): RoadLink[] {
  const out: RoadLink[] = [];
  const d = base.dto;
  const i = isAdded(key) ? undefined : base.index.get(key);
  if (i !== undefined) {
    if (view.nodes.get(i)?.hidden) return out;
    for (const j of base.nodeLinks[i]) {
      if ((d.linkFlags[j] & LINK.shortcut) !== 0 || linkHidden(base, view, j) || !shown[gameLinkKind(base, view, j)]) continue;
      const a = d.keys[d.linkA[j]], b = d.keys[d.linkB[j]];
      out.push({ from: a, to: b, key: pairKey(a, b) });
    }
  }
  if (shown.added)
    for (const k of addedAt(view, key)) {
      const l = view.addedLinks[k];
      out.push({ from: l.from, to: l.to, key: pairKey(l.from, l.to) });
    }
  return out;
}

/** A node's street after the edits (0: none). */
function streetOfNode(base: Base, view: View, key: string): number {
  return nodePos(base, view, key)?.street ?? 0;
}

/**
 * What the road tool selects for the item under the pointer: the road it is on, both ways up to the next branch (a node
 * where three or more roads meet) or dead end, going on through the nodes where exactly two meet: its links, the nodes
 * on the way and the dead ends, not the branches (they are other roads' nodes too). Lane-change links, hidden items
 * and kinds the left map leaves out are not roads here. A branch node, or an item that is no road, is taken alone.
 * `sameStreet` (Ctrl): every node of the item's street name and the links with both ends of it (an unnamed item: its road).
 */
export function roadOf(base: Base, view: View, shown: Shown, p: Picked, sameStreet: boolean): { nodes: string[]; links: string[] } {
  if (sameStreet) {
    const a = p.kind === "node" ? streetOfNode(base, view, p.key) : streetOfNode(base, view, p.from);
    const b = p.kind === "node" ? a : streetOfNode(base, view, p.to);
    const street = a === b ? a : a || b;
    if (street !== 0) return streetItems(base, view, shown, street);
  }
  const shownNode = (key: string) => {
    const kind = nodeKindOf(base, view, key);
    return kind !== null && shown[kind];
  };
  const nodes = new Set<string>(), links = new Set<string>();
  // from a node reached along a link: on while exactly two roads meet there
  const extend = (end: string, came: RoadLink) => {
    for (;;) {
      const at = roadLinksAt(base, view, shown, end);
      if (at.length === 1) {
        if (shownNode(end)) nodes.add(end);
        return;
      }
      if (at.length !== 2) return;
      if (shownNode(end)) nodes.add(end);
      const next = at[0].key === came.key ? at[1] : at[0];
      if (links.has(next.key)) return;
      links.add(next.key);
      end = next.from === end ? next.to : next.from;
      came = next;
    }
  };
  if (p.kind === "link") {
    const start = roadLinksAt(base, view, shown, p.from).find((l) => l.key === p.key);
    if (!start) return { nodes: [], links: [p.key] };
    links.add(start.key);
    extend(start.from, start);
    extend(start.to, start);
  } else {
    const at = roadLinksAt(base, view, shown, p.key);
    if (at.length === 0 || at.length > 2) return { nodes: shownNode(p.key) ? [p.key] : [], links: [] };
    if (shownNode(p.key)) nodes.add(p.key);
    for (const l of at) {
      if (links.has(l.key)) continue;
      links.add(l.key);
      extend(l.from === p.key ? l.to : l.from, l);
    }
  }
  return { nodes: [...nodes], links: [...links] };
}

/** Every node of a street name the left map shows, and the links a road runs on with both ends of it. */
function streetItems(base: Base, view: View, shown: Shown, street: number): { nodes: string[]; links: string[] } {
  const d = base.dto;
  const streetAt = (i: number) => view.nodes.get(i)?.street ?? d.street[i];
  const nodes: string[] = [];
  for (let i = 0; i < d.keys.length; i++)
    if (streetAt(i) === street && !view.nodes.get(i)?.hidden && shown[gameNodeKind(base, view, i)]) nodes.push(d.keys[i]);
  if (shown.added) for (const [key, n] of view.added) if (n.street === street) nodes.push(key);
  const links: string[] = [];
  for (let j = 0; j < d.linkA.length; j++) {
    const a = d.linkA[j], b = d.linkB[j];
    if (streetAt(a) !== street || streetAt(b) !== street) continue;
    if ((d.linkFlags[j] & LINK.shortcut) !== 0 || linkHidden(base, view, j) || !shown[gameLinkKind(base, view, j)]) continue;
    links.push(pairKey(d.keys[a], d.keys[b]));
  }
  if (shown.added)
    for (const l of view.addedLinks) if (streetOfNode(base, view, l.from) === street && streetOfNode(base, view, l.to) === street) links.push(pairKey(l.from, l.to));
  return { nodes, links };
}

export { isAdded };
