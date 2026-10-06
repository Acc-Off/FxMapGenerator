import { type AddedStreetName, copiedGroup, groupName, groupWhereFound, type ItemName, type NotAppliedEdit, type RoadEditsFile, type RoadLinkEdit, type RoadLinkValues, type RoadNodeEdit, type RoadNodeValues, type RoadPathsDto, type StreetName } from "../../shared/types";

// The road edits as the screen holds them: the file's shape (Docs/spec/project-format.ja.md), applied to the game's path
// data the way the server applies them (Core/RoadEdits/RoadEditSet.cs), and the operations of the tools. Every operation
// returns a new edits object (the undo list keeps the old ones).

/** Node flags of RoadPathsDto.flags. */
export const NODE = { junction: 1, highway: 2, tunnel: 4, unpaved: 8, switchedOff: 16 } as const;
/** Link flags of RoadPathsDto.linkFlags. */
export const LINK = { narrow: 1, dontUseForNavigation: 2, shortcut: 4 } as const;

export const ADDED = "added:";
export const isAdded = (key: string) => key.startsWith(ADDED);
/** A link's key: its two node keys in order (as the server pairs them, either direction). */
export const pairKey = (a: string, b: string) => (a < b ? `${a} ${b}` : `${b} ${a}`);
export const pairOf = (key: string): [string, string] => key.split(" ") as [string, string];
/** Positions that count as the same (the files keep 3 decimals). */
const SAME = 0.0005;
export const MAX_LANES = 7;
export const MAX_WIDTH = 100;

export const emptyEdits = (): RoadEditsFile => ({ format: 1, nodes: {}, links: [] });

// ------------------------------------------------------------------------------------------------ the game's data

/** Square buckets of items over the map, to find those near a point or in a tile quickly. */
export class Buckets {
  static readonly SIZE = 200;
  readonly cells = new Map<number, number[]>();

  static key(cx: number, cy: number): number {
    return (cy + 1000) * 4096 + (cx + 1000);
  }

  add(i: number, x0: number, y0: number, x1: number, y1: number) {
    const s = Buckets.SIZE;
    for (let cy = Math.floor(y0 / s); cy <= Math.floor(y1 / s); cy++)
      for (let cx = Math.floor(x0 / s); cx <= Math.floor(x1 / s); cx++) {
        const k = Buckets.key(cx, cy);
        const l = this.cells.get(k);
        if (l) l.push(i);
        else this.cells.set(k, [i]);
      }
  }

  /** The items of the buckets the rectangle touches (an item may come more than once). */
  *near(x0: number, y0: number, x1: number, y1: number): Generator<number> {
    const s = Buckets.SIZE;
    for (let cy = Math.floor(y0 / s); cy <= Math.floor(y1 / s); cy++)
      for (let cx = Math.floor(x0 / s); cx <= Math.floor(x1 / s); cx++) {
        const l = this.cells.get(Buckets.key(cx, cy));
        if (l) yield* l;
      }
  }
}

/** The game's path data as loaded, with lookups. */
export interface Base {
  dto: RoadPathsDto;
  version: string;
  /** Node key -> index. */
  index: Map<string, number>;
  /** Link key (pairKey) -> link index. */
  pairs: Map<string, number>;
  /** Per node, the links that end at it. */
  nodeLinks: number[][];
  nodeBuckets: Buckets;
  linkBuckets: Buckets;
  streets: Map<number, StreetName>;
}

export function makeBase(dto: RoadPathsDto): Base {
  const index = new Map<string, number>();
  dto.keys.forEach((k, i) => index.set(k, i));
  const nodeLinks: number[][] = dto.keys.map(() => []);
  const pairs = new Map<string, number>();
  const nodeBuckets = new Buckets();
  const linkBuckets = new Buckets();
  for (let i = 0; i < dto.keys.length; i++) nodeBuckets.add(i, dto.x[i], dto.y[i], dto.x[i], dto.y[i]);
  for (let j = 0; j < dto.linkA.length; j++) {
    const a = dto.linkA[j], b = dto.linkB[j];
    pairs.set(pairKey(dto.keys[a], dto.keys[b]), j);
    nodeLinks[a].push(j);
    nodeLinks[b].push(j);
    linkBuckets.add(j, Math.min(dto.x[a], dto.x[b]), Math.min(dto.y[a], dto.y[b]), Math.max(dto.x[a], dto.x[b]), Math.max(dto.y[a], dto.y[b]));
  }
  const streets = new Map<number, StreetName>();
  for (const s of dto.streets) streets.set(s.hash, s);
  return { dto, version: dto.version, index, pairs, nodeLinks, nodeBuckets, linkBuckets, streets };
}

/** A game node's values as the edits file writes them. */
export function baseValues(base: Base, i: number): Required<RoadNodeValues> {
  const d = base.dto, f = d.flags[i];
  return {
    x: d.x[i],
    y: d.y[i],
    z: d.z[i],
    street: d.street[i],
    highway: (f & NODE.highway) !== 0,
    tunnel: (f & NODE.tunnel) !== 0,
    unpaved: (f & NODE.unpaved) !== 0,
    switchedOff: (f & NODE.switchedOff) !== 0,
  };
}

/** A game link's lanes and narrow flag from `from` to `to` (either way round of the file's first record). */
export function baseLinkValues(base: Base, j: number, from: string): Required<RoadLinkValues> {
  const d = base.dto;
  const along = d.keys[d.linkA[j]] === from;
  return {
    lanesForward: along ? d.lanesForward[j] : d.lanesBack[j],
    lanesBack: along ? d.lanesBack[j] : d.lanesForward[j],
    narrow: (d.linkFlags[j] & LINK.narrow) !== 0,
  };
}

// ------------------------------------------------------------------------------------------------ the edits applied

export interface NodeState {
  x: number;
  y: number;
  z: number;
  street: number;
  flags: number;
  hidden: boolean;
  moved: boolean;
  changed: boolean;
}

export interface LinkState {
  lanesForward: number;
  lanesBack: number;
  narrow: boolean;
  width: number | null;
  hidden: boolean;
  changed: boolean;
}

export interface AddedLink extends LinkState {
  from: string;
  to: string;
}

/** The edits applied to the game's data: what the screen draws and picks from. */
export interface View {
  edits: RoadEditsFile;
  /** Game nodes whose edits apply. */
  nodes: Map<number, NodeState>;
  /** Added nodes by key. */
  added: Map<string, NodeState>;
  /** Game links whose edits apply (hidden or changed), by link index. */
  links: Map<number, LinkState>;
  /** Game links drawn apart from the rest: edited, or an end node edited. */
  touched: Set<number>;
  /** Added links that apply. */
  addedLinks: AddedLink[];
  addedBuckets: Buckets;
  notApplied: NotAppliedEdit[];
  /**
   * Edits of the groups that apply where found (RoadEditGroup) which this project's data does not hold: left out and
   * not listed ("n:<node key>", "l:<from> <to>").
   */
  leftOut: Set<string>;
}

function flagsOf(v: Required<RoadNodeValues>, junction: boolean): number {
  return (junction ? NODE.junction : 0) | (v.highway ? NODE.highway : 0) | (v.tunnel ? NODE.tunnel : 0) | (v.unpaved ? NODE.unpaved : 0) | (v.switchedOff ? NODE.switchedOff : 0);
}

function matches(base: Base, i: number, o: RoadNodeValues | undefined): boolean {
  if (!o || o.x === undefined || o.y === undefined || o.z === undefined) return false;
  const v = baseValues(base, i);
  return Math.abs(v.x - o.x) <= SAME && Math.abs(v.y - o.y) <= SAME && Math.abs(v.z - o.z) <= SAME
    && (o.street === undefined || o.street === v.street) && (o.highway === undefined || o.highway === v.highway) && (o.tunnel === undefined || o.tunnel === v.tunnel)
    && (o.unpaved === undefined || o.unpaved === v.unpaved) && (o.switchedOff === undefined || o.switchedOff === v.switchedOff);
}

/** Applies the edits to the game's data as the server does, and lists those that do not apply. */
export function applyEdits(base: Base, edits: RoadEditsFile): View {
  const nodes = new Map<number, NodeState>();
  const added = new Map<string, NodeState>();
  const notApplied: NotAppliedEdit[] = [];
  const leftOut = new Set<string>();
  const whereFound = new Set(Object.entries(edits.groups ?? {}).filter(([, g]) => groupWhereFound(g)).map(([id]) => id));
  const listed = (group: string | undefined) => group === undefined || !whereFound.has(group);
  const found = new Map<string, boolean>();
  for (const [key, e] of Object.entries(edits.nodes)) {
    if (isAdded(key)) {
      const v = { x: e.x ?? 0, y: e.y ?? 0, z: e.z ?? 0, street: e.street ?? 0, highway: !!e.highway, tunnel: !!e.tunnel, unpaved: !!e.unpaved, switchedOff: !!e.switchedOff };
      added.set(key, { x: v.x, y: v.y, z: v.z, street: v.street, flags: flagsOf(v, false), hidden: false, moved: false, changed: false });
      continue;
    }
    const i = base.index.get(key);
    const why = i === undefined ? "nodeMissing" : matches(base, i, e.original) ? null : "nodeChanged";
    found.set(key, why === null);
    if (why !== null) {
      if (listed(e.group)) notApplied.push({ kind: "node", key, reason: why });
      else leftOut.add("n:" + key);
      continue;
    }
    const b = baseValues(base, i!);
    const v = {
      x: e.x ?? b.x,
      y: e.y ?? b.y,
      z: e.z ?? b.z,
      street: e.street ?? b.street,
      highway: e.highway ?? b.highway,
      tunnel: e.tunnel ?? b.tunnel,
      unpaved: e.unpaved ?? b.unpaved,
      switchedOff: e.switchedOff ?? b.switchedOff,
    };
    const moved = e.x !== undefined || e.y !== undefined || e.z !== undefined;
    const changed = e.street !== undefined || e.highway !== undefined || e.tunnel !== undefined || e.unpaved !== undefined || e.switchedOff !== undefined;
    nodes.set(i!, { x: v.x, y: v.y, z: v.z, street: v.street, flags: flagsOf(v, (base.dto.flags[i!] & NODE.junction) !== 0), hidden: !!e.hidden, moved, changed });
  }
  const links = new Map<number, LinkState>();
  const addedLinks: AddedLink[] = [];
  for (const e of edits.links) {
    const key = `${e.from} ${e.to}`;
    const j = base.pairs.get(pairKey(e.from, e.to));
    let why: NotAppliedEdit["reason"] | null = null;
    if (found.get(e.from) === false || found.get(e.to) === false) why = "endNotApplied";
    else if (!e.original) {
      if (j !== undefined) why = "linkExists";
    } else if (j === undefined) why = "linkMissing";
    else {
      const b = baseLinkValues(base, j, e.from);
      if (b.lanesForward !== e.original.lanesForward || b.lanesBack !== e.original.lanesBack || b.narrow !== e.original.narrow) why = "linkChanged";
    }
    if (why !== null) {
      if (listed(e.group)) notApplied.push({ kind: "link", key, reason: why });
      else leftOut.add("l:" + key);
      continue;
    }
    if (!e.original) {
      addedLinks.push({ from: e.from, to: e.to, lanesForward: e.lanesForward ?? 1, lanesBack: e.lanesBack ?? 0, narrow: !!e.narrow, width: e.width ?? null, hidden: false, changed: false });
      continue;
    }
    const b = baseLinkValues(base, j!, base.dto.keys[base.dto.linkA[j!]]);
    // the state keeps the file's first record's direction (linkA -> linkB)
    const along = base.dto.keys[base.dto.linkA[j!]] === e.from;
    const lf = e.lanesForward, lb = e.lanesBack;
    links.set(j!, {
      lanesForward: along ? (lf ?? b.lanesForward) : (lb ?? b.lanesForward),
      lanesBack: along ? (lb ?? b.lanesBack) : (lf ?? b.lanesBack),
      narrow: e.narrow ?? b.narrow,
      width: e.width ?? null,
      hidden: !!e.hidden,
      changed: !e.hidden,
    });
  }
  const touched = new Set<number>(links.keys());
  for (const i of nodes.keys()) for (const j of base.nodeLinks[i]) touched.add(j);
  const view: View = { edits, nodes, added, links, touched, addedLinks, addedBuckets: new Buckets(), notApplied, leftOut };
  addedLinks.forEach((l, k) => {
    const a = nodePos(base, view, l.from), b = nodePos(base, view, l.to);
    if (a && b) view.addedBuckets.add(k, Math.min(a.x, b.x), Math.min(a.y, b.y), Math.max(a.x, b.x), Math.max(a.y, b.y));
  });
  return view;
}

/** A node's position and values with the edits applied (null: no such node). */
export function nodePos(base: Base, view: View, key: string): NodeState | null {
  if (isAdded(key)) return view.added.get(key) ?? null;
  const i = base.index.get(key);
  if (i === undefined) return null;
  const s = view.nodes.get(i);
  if (s) return s;
  const d = base.dto;
  return { x: d.x[i], y: d.y[i], z: d.z[i], street: d.street[i], flags: d.flags[i], hidden: false, moved: false, changed: false };
}

/** A game link is hidden: itself, or one of its end nodes. */
export function linkHidden(base: Base, view: View, j: number): boolean {
  if (view.links.get(j)?.hidden) return true;
  const d = base.dto;
  return !!view.nodes.get(d.linkA[j])?.hidden || !!view.nodes.get(d.linkB[j])?.hidden;
}

// ------------------------------------------------------------------------------------------------ what the left map shows

/** The game's classes of nodes and links (by their flags), as the left map colours them. */
export const GAME_KINDS = ["normal", "highway", "tunnel", "unpaved", "switchedOff"] as const;
export type ClassName = (typeof GAME_KINDS)[number];
/** What the left map can show or leave out (the legend's check boxes): the game's classes and the edits by kind. */
export type ShownKind = ClassName | "added" | "edited" | "hidden";
export type Shown = Record<ShownKind, boolean>;
export const ALL_SHOWN: Shown = { normal: true, highway: true, tunnel: true, unpaved: true, switchedOff: true, added: true, edited: true, hidden: true };

export function nodeClass(f: number): ClassName {
  if (f & NODE.tunnel) return "tunnel";
  if (f & NODE.highway) return "highway";
  if (f & NODE.unpaved) return "unpaved";
  if (f & NODE.switchedOff) return "switchedOff";
  return "normal";
}

export function linkClass(a: number, b: number): ClassName {
  if ((a | b) & NODE.tunnel) return "tunnel";
  if (a & b & NODE.highway) return "highway";
  if ((a | b) & NODE.unpaved) return "unpaved";
  if (a & b & NODE.switchedOff) return "switchedOff";
  return "normal";
}

/** A game node's kind: hidden, edited (moved or a value changed), else its class. */
export function gameNodeKind(base: Base, view: View, i: number): ShownKind {
  const n = view.nodes.get(i);
  if (!n) return nodeClass(base.dto.flags[i]);
  return n.hidden ? "hidden" : n.moved || n.changed ? "edited" : nodeClass(n.flags);
}

/** A game link's kind: hidden (itself or an end), edited (its values, or an end moved), else the class of its ends. */
export function gameLinkKind(base: Base, view: View, j: number): ShownKind {
  const d = base.dto;
  const cls = linkClass(d.flags[d.linkA[j]], d.flags[d.linkB[j]]);
  if (!view.touched.has(j)) return cls;
  if (linkHidden(base, view, j)) return "hidden";
  return view.links.has(j) || !!view.nodes.get(d.linkA[j])?.moved || !!view.nodes.get(d.linkB[j])?.moved ? "edited" : cls;
}

/** A node's kind by its key (null: no such node). */
export function nodeKindOf(base: Base, view: View, key: string): ShownKind | null {
  if (isAdded(key)) return view.added.has(key) ? "added" : null;
  const i = base.index.get(key);
  return i === undefined ? null : gameNodeKind(base, view, i);
}

/** A link's kind by its key (null: no such link). */
export function linkKindOf(base: Base, view: View, key: string): ShownKind | null {
  const j = base.pairs.get(key);
  if (j !== undefined) return gameLinkKind(base, view, j);
  return view.addedLinks.some((l) => pairKey(l.from, l.to) === key) ? "added" : null;
}

/** The selection without what the left map does not show (nor items that are gone). */
export function shownOnly(base: Base, view: View, sel: { nodes: string[]; links: string[] }, shown: Shown): { nodes: string[]; links: string[] } {
  const keep = (kind: ShownKind | null) => kind !== null && shown[kind];
  return { nodes: sel.nodes.filter((k) => keep(nodeKindOf(base, view, k))), links: sel.links.filter((k) => keep(linkKindOf(base, view, k))) };
}

// ------------------------------------------------------------------------------------------------ operations

function copy(edits: RoadEditsFile): RoadEditsFile {
  return {
    format: 1,
    ...(edits.groups ? { groups: Object.fromEntries(Object.entries(edits.groups).map(([id, g]) => [id, typeof g === "string" ? g : { ...g }])) } : {}),
    ...(edits.streets ? { streets: Object.fromEntries(Object.entries(edits.streets).map(([h, s]) => [h, { ...s }])) } : {}),
    nodes: Object.fromEntries(Object.entries(edits.nodes).map(([k, v]) => [k, { ...v, original: v.original ? { ...v.original } : undefined }])),
    links: edits.links.map((l) => ({ ...l, original: l.original ? { ...l.original } : undefined })),
  };
}

/** Drops undefined fields (the file writes only the given ones). */
function tidy(e: RoadNodeEdit | RoadLinkEdit) {
  for (const k of Object.keys(e) as (keyof typeof e)[]) if (e[k] === undefined) delete e[k];
  if ("original" in e && e.original) for (const k of Object.keys(e.original) as (keyof typeof e.original)[]) if (e.original[k] === undefined) delete e.original[k];
}

/** The next number of an added node (never one the edits used). */
export function nextAdded(edits: RoadEditsFile, least: number): number {
  let n = least;
  for (const k of Object.keys(edits.nodes)) if (isAdded(k)) n = Math.max(n, Number(k.slice(ADDED.length)) + 1);
  return n;
}

/** A game node's entry, made with its original position when it has none. */
function gameEntry(base: Base, e: RoadEditsFile, key: string): RoadNodeEdit {
  let n = e.nodes[key];
  if (!n) {
    const v = baseValues(base, base.index.get(key)!);
    n = e.nodes[key] = { original: { x: v.x, y: v.y, z: v.z } };
  }
  return n;
}

const NODE_FIELDS = ["x", "y", "z", "street", "highway", "tunnel", "unpaved", "switchedOff"] as const;

/**
 * A game node's entry that says nothing any more (only its original, not hidden, no link edit ends at it) goes, and so
 * do a street name the edits add that no node has and a group no node or link belongs to.
 */
function prune(e: RoadEditsFile) {
  const used = new Set<string>();
  for (const l of e.links) {
    used.add(l.from);
    used.add(l.to);
  }
  for (const [k, n] of Object.entries(e.nodes))
    if (!isAdded(k) && !n.hidden && NODE_FIELDS.every((f) => n[f] === undefined) && !used.has(k)) delete e.nodes[k];
  if (e.streets) {
    const named = new Set<number>();
    for (const n of Object.values(e.nodes)) if (n.street !== undefined) named.add(n.street);
    for (const h of Object.keys(e.streets)) if (!named.has(Number(h))) delete e.streets[h];
    if (Object.keys(e.streets).length === 0) delete e.streets;
  }
  if (e.groups) {
    const used = new Set<string>();
    for (const n of Object.values(e.nodes)) if (n.group !== undefined) used.add(n.group);
    for (const l of e.links) if (l.group !== undefined) used.add(l.group);
    for (const id of Object.keys(e.groups)) if (!used.has(id)) delete e.groups[id];
    if (Object.keys(e.groups).length === 0) delete e.groups;
  }
}

export interface NewNode {
  key: string;
  x: number;
  y: number;
  z: number;
}

export interface NewLink {
  from: string;
  to: string;
  lanesForward: number;
  lanesBack: number;
  narrow: boolean;
  width: number | null;
}

/**
 * Adds nodes (no street, no flags) and links (a game node at an end gets its entry with the original position). A link
 * from a node to itself, or between two nodes the edits or the game already link, is not added (the file allows one
 * link a pair of nodes).
 */
export function addItems(base: Base, edits: RoadEditsFile, nodes: NewNode[], links: NewLink[]): RoadEditsFile {
  const e = copy(edits);
  for (const n of nodes)
    e.nodes[n.key] = { x: round3(n.x), y: round3(n.y), z: round3(n.z), street: 0, highway: false, tunnel: false, unpaved: false, switchedOff: false };
  const linked = new Set(e.links.map((l) => pairKey(l.from, l.to)));
  for (const l of links) {
    const key = pairKey(l.from, l.to);
    if (l.from === l.to || linked.has(key) || base.pairs.has(key)) continue;
    linked.add(key);
    for (const end of [l.from, l.to]) if (!isAdded(end)) gameEntry(base, e, end);
    e.links.push({ from: l.from, to: l.to, lanesForward: l.lanesForward, lanesBack: l.lanesBack, narrow: l.narrow, width: l.width });
  }
  prune(e);
  return e;
}

export const round3 = (v: number) => Math.round(v * 1000) / 1000;

/** Removes added nodes (with their links) and added links. */
export function removeAdded(edits: RoadEditsFile, nodes: string[], links: string[]): RoadEditsFile {
  const e = copy(edits);
  const gone = new Set(nodes.filter(isAdded));
  const goneLinks = new Set(links);
  for (const k of gone) delete e.nodes[k];
  e.links = e.links.filter((l) => !(l.original === undefined && (gone.has(l.from) || gone.has(l.to) || goneLinks.has(pairKey(l.from, l.to)))));
  prune(e);
  return e;
}

/**
 * Hides game nodes and links. A hidden node changes nothing else (a move is dropped), and the edits of links ending
 * at it go (an added link cannot end at a hidden node; its game links are hidden with it). An item of a group stays in it.
 */
export function hide(base: Base, edits: RoadEditsFile, nodes: string[], links: string[]): RoadEditsFile {
  const e = copy(edits);
  const hidden = new Set(nodes.filter((k) => !isAdded(k) && base.index.has(k)));
  for (const k of hidden) {
    const v = baseValues(base, base.index.get(k)!);
    const old = e.nodes[k];
    e.nodes[k] = { hidden: true, original: { x: old?.original?.x ?? v.x, y: old?.original?.y ?? v.y, z: old?.original?.z ?? v.z }, ...(old?.group !== undefined ? { group: old.group } : {}) };
  }
  e.links = e.links.filter((l) => !hidden.has(l.from) && !hidden.has(l.to));
  for (const key of links) {
    const [a, b] = pairOf(key);
    const j = base.pairs.get(key);
    if (j === undefined || hidden.has(a) || hidden.has(b)) continue;
    const from = base.dto.keys[base.dto.linkA[j]], to = base.dto.keys[base.dto.linkB[j]];
    const group = e.links.find((l) => pairKey(l.from, l.to) === key)?.group;
    e.links = e.links.filter((l) => pairKey(l.from, l.to) !== key);
    gameEntry(base, e, from);
    gameEntry(base, e, to);
    e.links.push({ from, to, hidden: true, original: baseLinkValues(base, j, from), ...(group !== undefined ? { group } : {}) });
  }
  prune(e);
  return e;
}

/** Shows hidden game nodes and links again. */
export function unhide(edits: RoadEditsFile, nodes: string[], links: string[]): RoadEditsFile {
  const e = copy(edits);
  for (const k of nodes) {
    const n = e.nodes[k];
    if (n?.hidden) delete n.hidden;
  }
  const keys = new Set(links);
  e.links = e.links.filter((l) => !(l.hidden && keys.has(pairKey(l.from, l.to))));
  prune(e);
  return e;
}

/** Moves nodes (a game node back to its original position loses the move). */
export function moveNodes(base: Base, edits: RoadEditsFile, moves: { key: string; x: number; y: number; z?: number }[]): RoadEditsFile {
  const e = copy(edits);
  for (const m of moves) {
    if (isAdded(m.key)) {
      const n = e.nodes[m.key];
      if (!n) continue;
      n.x = round3(m.x);
      n.y = round3(m.y);
      if (m.z !== undefined) n.z = round3(m.z);
      continue;
    }
    const i = base.index.get(m.key);
    if (i === undefined) continue;
    const n = gameEntry(base, e, m.key);
    if (n.hidden) continue;
    const o = n.original!;
    const x = round3(m.x), y = round3(m.y);
    const back = Math.abs(x - o.x!) <= SAME && Math.abs(y - o.y!) <= SAME;
    n.x = back ? undefined : x;
    n.y = back ? undefined : y;
    if (m.z !== undefined) n.z = Math.abs(m.z - o.z!) <= SAME ? undefined : round3(m.z);
    tidy(n);
  }
  prune(e);
  return e;
}

export type NodeChange = Partial<Pick<Required<RoadNodeValues>, "z" | "street" | "highway" | "tunnel" | "unpaved" | "switchedOff">>;

/** Changes values of nodes (a game node keeps the original of each value it changes; back to it = unchanged). */
export function setNodeValues(base: Base, edits: RoadEditsFile, keys: string[], change: NodeChange): RoadEditsFile {
  const e = copy(edits);
  for (const key of keys) {
    if (isAdded(key)) {
      const n = e.nodes[key];
      if (!n) continue;
      Object.assign(n, change.z !== undefined ? { ...change, z: round3(change.z) } : change);
      continue;
    }
    const i = base.index.get(key);
    if (i === undefined) continue;
    const n = gameEntry(base, e, key);
    if (n.hidden) continue;
    const b = baseValues(base, i);
    const o = n.original!;
    for (const f of Object.keys(change) as (keyof NodeChange)[]) {
      const v = f === "z" ? round3(change.z!) : change[f];
      if (f === "z") {
        n.z = Math.abs((v as number) - o.z!) <= SAME ? undefined : (v as number);
        continue;
      }
      if (v === b[f]) {
        (n as Record<string, unknown>)[f] = undefined;
        (o as Record<string, unknown>)[f] = undefined;
      } else {
        (n as Record<string, unknown>)[f] = v;
        (o as Record<string, unknown>)[f] = b[f];
      }
    }
    tidy(n);
  }
  prune(e);
  return e;
}

export type LinkChange = Partial<{ lanesForward: number; lanesBack: number; narrow: boolean; width: number | null }>;

/**
 * Changes values of links; lanes go the way `from` to `to` of each link as the screen shows it (a game link: the way
 * of the file's first record). A game link back to its original values (and no width) loses its edit.
 */
export function setLinkValues(base: Base, edits: RoadEditsFile, keys: string[], change: LinkChange): RoadEditsFile {
  const e = copy(edits);
  for (const key of keys) {
    const addedLink = e.links.find((l) => !l.original && pairKey(l.from, l.to) === key);
    if (addedLink) {
      // no lanes either way is not a link: such a change leaves the link as it is
      if ((change.lanesForward ?? addedLink.lanesForward ?? 0) + (change.lanesBack ?? addedLink.lanesBack ?? 0) === 0) continue;
      Object.assign(addedLink, change);
      continue;
    }
    const j = base.pairs.get(key);
    if (j === undefined) continue;
    const from = base.dto.keys[base.dto.linkA[j]], to = base.dto.keys[base.dto.linkB[j]];
    let l = e.links.find((x) => x.original && pairKey(x.from, x.to) === key);
    if (l?.hidden) continue;
    const orig = baseLinkValues(base, j, from);
    if (!l) {
      gameEntry(base, e, from);
      gameEntry(base, e, to);
      l = { from, to, original: orig };
      e.links.push(l);
    }
    // the edit's own direction may be the other way round (a file written elsewhere)
    const along = l.from === from;
    const cur = {
      lanesForward: along ? (l.lanesForward ?? orig.lanesForward) : (l.lanesBack ?? orig.lanesForward),
      lanesBack: along ? (l.lanesBack ?? orig.lanesBack) : (l.lanesForward ?? orig.lanesBack),
      narrow: l.narrow ?? orig.narrow,
      width: l.width ?? null,
    };
    const next = { ...cur, ...change };
    if (next.lanesForward + next.lanesBack === 0) {
      // no lanes either way is not a link: the link stays as it was (an edit made just now for it goes again)
      if (!l.lanesForward && !l.lanesBack && l.narrow === undefined && l.width === undefined) e.links = e.links.filter((x) => x !== l);
      continue;
    }
    const o = baseLinkValues(base, j, l.from);
    const lf = along ? next.lanesForward : next.lanesBack, lb = along ? next.lanesBack : next.lanesForward;
    l.lanesForward = lf === o.lanesForward ? undefined : lf;
    l.lanesBack = lb === o.lanesBack ? undefined : lb;
    l.narrow = next.narrow === o.narrow ? undefined : next.narrow;
    l.width = next.width ?? undefined;
    l.original = o;
    tidy(l);
    if (l.lanesForward === undefined && l.lanesBack === undefined && l.narrow === undefined && l.width === undefined) e.links = e.links.filter((x) => x !== l);
  }
  prune(e);
  return e;
}

/**
 * Splits a link at a new node: a game link is hidden and two added links take its place through the node (with its
 * lanes, narrow flag and width); an added link is replaced by two. The node and the two links go into the group of the
 * link's edit, if it has one.
 */
export function splitLink(base: Base, view: View, edits: RoadEditsFile, key: string, node: NewNode): RoadEditsFile {
  const [a, b] = pairOf(key);
  const addedLink = edits.links.find((l) => !l.original && pairKey(l.from, l.to) === key);
  if (addedLink) {
    const e = removeAdded(edits, [], [key]);
    const v = { lanesForward: addedLink.lanesForward ?? 1, lanesBack: addedLink.lanesBack ?? 0, narrow: !!addedLink.narrow, width: addedLink.width ?? null };
    return inGroup(addItems(base, e, [node], [{ from: addedLink.from, to: node.key, ...v }, { from: node.key, to: addedLink.to, ...v }]), edits, addedLink.group, node.key,
      [pairKey(addedLink.from, node.key), pairKey(node.key, addedLink.to)]);
  }
  const j = base.pairs.get(key);
  if (j === undefined || !base.index.has(a) || !base.index.has(b)) return edits;
  const from = base.dto.keys[base.dto.linkA[j]], to = base.dto.keys[base.dto.linkB[j]];
  const s = view.links.get(j);
  const o = baseLinkValues(base, j, from);
  const v = { lanesForward: s?.lanesForward ?? o.lanesForward, lanesBack: s?.lanesBack ?? o.lanesBack, narrow: s?.narrow ?? o.narrow, width: s?.width ?? null };
  const group = edits.links.find((l) => l.original && pairKey(l.from, l.to) === key)?.group;
  const e = hide(base, edits, [], [key]);
  return inGroup(addItems(base, e, [node], [{ from, to: node.key, ...v }, { from: node.key, to, ...v }]), edits, group, node.key, [pairKey(from, node.key), pairKey(node.key, to)]);
}

/** Puts an added node and added links (their keys) into a group of `before` (none: the edits as they are). */
function inGroup(e: RoadEditsFile, before: RoadEditsFile, group: string | undefined, node: string, links: string[]): RoadEditsFile {
  if (group === undefined) return e;
  const name = before.groups?.[group];
  if (name && !e.groups?.[group]) e.groups = { ...(e.groups ?? {}), [group]: typeof name === "string" ? name : { ...name } };
  const n = e.nodes[node];
  if (n) n.group = group;
  const keys = new Set(links);
  for (const l of e.links) if (!l.original && keys.has(pairKey(l.from, l.to))) l.group = group;
  return e;
}

// ------------------------------------------------------------------------------------------------ street names

/** The game's hash of a text (Jenkins one-at-a-time over its UTF-8 bytes in lower case). */
export function joaat(text: string): number {
  let h = 0;
  for (const c of new TextEncoder().encode(text.toLowerCase())) {
    h = (h + c) >>> 0;
    h = (h + (h << 10)) >>> 0;
    h = (h ^ (h >>> 6)) >>> 0;
  }
  h = (h + (h << 3)) >>> 0;
  h = (h ^ (h >>> 11)) >>> 0;
  return (h + (h << 15)) >>> 0;
}

/** The street hashes in use: the game's (named or on a node) and those of the names the edits add. */
export function streetHashes(base: Base, edits: RoadEditsFile): Set<number> {
  const s = new Set<number>(base.dto.street);
  for (const n of base.dto.streets) s.add(n.hash);
  for (const h of Object.keys(edits.streets ?? {})) s.add(Number(h));
  return s;
}

/** The hash of a new street name: from its English name, else the next one no street uses. */
export function newStreetHash(en: string, taken: ReadonlySet<number>): number {
  let h = joaat(en.trim());
  while (h === 0 || taken.has(h)) h = (h + 1) >>> 0;
  return h;
}

/**
 * Puts a street name into the edits (a new one, or new words for one they add; the Japanese name left out when empty).
 * A new name no node has goes with the next change: give it to nodes in the same change.
 */
export function putStreet(edits: RoadEditsFile, hash: number, name: AddedStreetName): RoadEditsFile {
  const e = copy(edits);
  const ja = name.ja?.trim();
  e.streets = { ...(e.streets ?? {}), [String(hash)]: ja ? { en: name.en.trim(), ja } : { en: name.en.trim() } };
  return e;
}

// ------------------------------------------------------------------------------------------------ the bundled edits

/** Added nodes this close (m, each of x, y and z) count as the same. */
const SAME_PLACE = 0.01;

/** A group of the bundled edits as the window to take them in shows it. */
export interface BundledGroup {
  id: string;
  /** Its name in English and Japanese (the window shows it in the screen's language). */
  name: ItemName;
  /** The edits hold it already (an item of theirs belongs to it). */
  taken: boolean;
  /** What taking it in would add: nodes (added, and edits of game nodes) and links (added, and edits of game links). */
  nodes: number;
  links: number;
  /** Items left out: the project's edits change the same game node or link already (or its node is hidden). */
  edited: number;
  /** Items left out: the game's data of this project does not hold what they change (the node, the link, their values). */
  mismatch: number;
}

/** What taking in groups of the bundled edits gives: the edits, and the counts of BundledGroup summed. */
export interface TakeIn {
  edits: RoadEditsFile;
  nodes: number;
  links: number;
  edited: number;
  mismatch: number;
}

/** The groups of the bundled edits, each counted against the edits as they are. */
export function bundledGroups(base: Base, edits: RoadEditsFile, bundled: RoadEditsFile): BundledGroup[] {
  return Object.entries(bundled.groups ?? {}).map(([id, group]) => {
    const name = groupName(group);
    const taken = Object.values(edits.nodes).some((n) => n.group === id) || edits.links.some((l) => l.group === id);
    const r = takeIn(base, edits, bundled, [id], 1);
    return { id, name, taken, nodes: r.nodes, links: r.links, edited: r.edited, mismatch: r.mismatch };
  });
}

/**
 * Adds groups of the bundled edits to the edits, one after the other, each item with its group:
 * - an added node takes the next number of the edits (from `least`), unless an added node of the edits is at the same
 *   place (then the links use that one and nothing is added);
 * - an edit of a game node or link is left out when the edits change the same one already (the project's edit stays;
 *   a node hidden by the bundle stays shown when a link edit of the project ends at it), and when the project's game
 *   data does not hold it as the bundle found it (no such node or link, another position or value);
 * - a link is left out when a node it ends at is; an added link is not added again between two nodes the edits link
 *   already, nor where the game has a link;
 * - a street name the bundle adds is matched by its English name (the edits', else the game's), else added;
 * - a group that applies where found is taken in whole: its edits the project's game data does not hold are kept too
 *   (not counted; they apply once a wider frame or another setting reads their nodes), unless the edits hold an entry
 *   under the same key;
 * - a group taken in is named in `lang`, the screen's language (one name from then on).
 */
export function takeIn(base: Base, edits: RoadEditsFile, bundled: RoadEditsFile, ids: string[], least: number, lang: "en" | "ja" = "en"): TakeIn {
  const e = copy(edits);
  let nodes = 0, links = 0, edited = 0, mismatch = 0;
  let next = nextAdded(e, least);
  const says = (k: string, n: RoadNodeEdit) => isAdded(k) || !onlyEnd(k, n);
  // a bundled street name: the hash of the same English name in the edits or the game, else a new one
  const streetOf = (h: number | undefined): number | undefined => {
    const s = h === undefined ? undefined : bundled.streets?.[String(h)];
    if (!s) return h;
    const en = s.en.trim().toLowerCase();
    const mine = Object.entries(e.streets ?? {}).find(([, x]) => x.en.trim().toLowerCase() === en);
    if (mine) return Number(mine[0]);
    const game = base.dto.streets.find((x) => x.en.trim().toLowerCase() === en);
    if (game) return game.hash;
    const hash = newStreetHash(s.en, streetHashes(base, e));
    e.streets = { ...(e.streets ?? {}), [String(hash)]: { ...s } };
    return hash;
  };
  for (const id of ids) {
    const name = bundled.groups?.[id];
    if (!name) continue;
    const whole = groupWhereFound(name);
    /** Bundled node key -> the key of the edits a link may end at; nodes left out: why. */
    const keys = new Map<string, string>();
    const left = new Map<string, "edited" | "mismatch">();
    for (const [k, n] of Object.entries(bundled.nodes)) {
      if (n.group !== id) continue;
      if (isAdded(k)) {
        const same = Object.entries(e.nodes).find(([key, m]) => isAdded(key)
          && Math.abs((m.x ?? 0) - (n.x ?? 0)) <= SAME_PLACE && Math.abs((m.y ?? 0) - (n.y ?? 0)) <= SAME_PLACE && Math.abs((m.z ?? 0) - (n.z ?? 0)) <= SAME_PLACE);
        if (same) {
          keys.set(k, same[0]);
          continue;
        }
        const key = ADDED + next++;
        e.nodes[key] = { ...n, street: streetOf(n.street), group: id };
        keys.set(k, key);
        nodes++;
        continue;
      }
      const i = base.index.get(k);
      if (i === undefined || !matches(base, i, n.original)) {
        if (whole && !e.nodes[k]) {
          e.nodes[k] = { ...n, original: n.original ? { ...n.original } : undefined, group: id };
          left.set(k, "mismatch");
          continue;
        }
        left.set(k, "mismatch");
        if (says(k, n)) mismatch++;
        continue;
      }
      const mine = e.nodes[k];
      if (mine && says(k, n) && (says(k, mine) || n.hidden)) {
        // the project edits the node already, or a link edit of the project ends at a node the bundle hides
        edited++;
        if (mine.hidden) left.set(k, "edited");
        else keys.set(k, k);
        continue;
      }
      if (mine && !says(k, n)) {
        // only an end of the group's links: the edits' entry serves, unless they hide the node (the links are left out)
        if (mine.hidden) left.set(k, "edited");
        else keys.set(k, k);
        continue;
      }
      e.nodes[k] = { ...n, original: n.original ? { ...n.original } : undefined, street: streetOf(n.street), group: id };
      tidy(e.nodes[k]);
      if (!n.hidden) keys.set(k, k);
      if (says(k, n)) nodes++;
    }
    // a link's end the group does not hold (the entry of a game node another group's links end at too): the game's node
    const end = (k: string): string | undefined => {
      const got = keys.get(k);
      if (got !== undefined || left.has(k) || isAdded(k)) return got;
      const o = bundled.nodes[k]?.original, i = base.index.get(k);
      if (i === undefined || !matches(base, i, o)) {
        left.set(k, "mismatch");
        return undefined;
      }
      if (e.nodes[k]?.hidden) {
        left.set(k, "edited");
        return undefined;
      }
      e.nodes[k] ??= { original: { x: o!.x, y: o!.y, z: o!.z } };
      keys.set(k, k);
      return k;
    };
    const linked = new Set(e.links.map((l) => pairKey(l.from, l.to)));
    for (const l of bundled.links) {
      if (l.group !== id) continue;
      const a = end(l.from), b = end(l.to);
      if (a === undefined || b === undefined) {
        if ((left.get(l.from) ?? left.get(l.to)) === "edited") edited++;
        else mismatch++;
        continue;
      }
      if (!l.original) {
        const key = pairKey(a, b);
        if (a === b || linked.has(key)) continue;
        if (base.pairs.has(key)) {
          mismatch++;
          continue;
        }
        linked.add(key);
        e.links.push({ ...l, from: a, to: b, group: id });
        links++;
        continue;
      }
      const key = pairKey(l.from, l.to);
      const j = base.pairs.get(key);
      const o = j === undefined ? null : baseLinkValues(base, j, l.from);
      if (!o || o.lanesForward !== l.original.lanesForward || o.lanesBack !== l.original.lanesBack || o.narrow !== l.original.narrow) {
        mismatch++;
        continue;
      }
      if (linked.has(key)) {
        edited++;
        continue;
      }
      linked.add(key);
      e.links.push({ ...l, original: { ...l.original }, group: id });
      links++;
    }
    if (Object.values(e.nodes).some((n) => n.group === id) || e.links.some((l) => l.group === id)) e.groups = { ...(e.groups ?? {}), [id]: copiedGroup(name, lang) };
  }
  prune(e);
  return { edits: e, nodes, links, edited, mismatch };
}

// ------------------------------------------------------------------------------------------------ the list of edits

export type RowKind = "group" | "added" | "hidden" | "moved" | "changedNode" | "changedLink";

/** Edits shown as one row of the list: the items of a group of edits, or connected items of one kind. */
export interface EditRow {
  id: string;
  kind: RowKind;
  /** The group's id (kind "group"). */
  group?: string;
  nodes: string[];
  /** Link keys (pairKey). */
  links: string[];
  /** West, south, east, north (m). */
  bounds: [number, number, number, number];
}

/** A game node's entry that only says where it was: an end of a link edit, not an edit of its own. */
function onlyEnd(key: string, n: RoadNodeEdit): boolean {
  return !isAdded(key) && !n.hidden && NODE_FIELDS.every((f) => n[f] === undefined);
}

class Union {
  readonly parent = new Map<string, string>();
  find(a: string): string {
    let p = this.parent.get(a) ?? a;
    if (p !== a) {
      p = this.find(p);
      this.parent.set(a, p);
    }
    return p;
  }
  join(a: string, b: string) {
    const ra = this.find(a), rb = this.find(b);
    if (ra !== rb) this.parent.set(ra, rb);
  }
}

/**
 * The rows of the edits list: a row per group of edits (its items, the ends of its link edits aside), then the other
 * items, connected ones together (added, hidden, moved, changed nodes, changed links). Edits that do not apply are left
 * out (listed apart; those of a group that applies where found are not listed at all).
 */
export function editRows(base: Base, view: View): EditRow[] {
  const e = view.edits;
  const groups: EditRow[] = [];
  const bounds = (nodes: string[], links: string[]): [number, number, number, number] => {
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    const put = (k: string) => {
      const p = nodePos(base, view, k) ?? (() => {
        const o = e.nodes[k]?.original;
        return o?.x !== undefined ? { x: o.x, y: o.y! } : null;
      })();
      if (!p) return;
      x0 = Math.min(x0, p.x); y0 = Math.min(y0, p.y); x1 = Math.max(x1, p.x); y1 = Math.max(y1, p.y);
    };
    nodes.forEach(put);
    for (const l of links) pairOf(l).forEach(put);
    return [x0, y0, x1, y1];
  };
  /** `through`: the node keys a link joins its row through (an added link: only added nodes). */
  const collect = (kind: RowKind, nodeKeys: string[], linkKeys: string[], joinNodes: (u: Union) => void, through: (key: string) => boolean = () => true) => {
    const u = new Union();
    joinNodes(u);
    for (const l of linkKeys)
      for (const end of pairOf(l)) if (through(end)) u.join("l:" + l, "n:" + end);
    const byRoot = new Map<string, { nodes: string[]; links: string[] }>();
    const put = (id: string, add: (g: { nodes: string[]; links: string[] }) => void) => {
      const r = u.find(id);
      let g = byRoot.get(r);
      if (!g) byRoot.set(r, (g = { nodes: [], links: [] }));
      add(g);
    };
    for (const n of nodeKeys) put("n:" + n, (g) => g.nodes.push(n));
    for (const l of linkKeys) put("l:" + l, (g) => g.links.push(l));
    for (const g of byRoot.values()) groups.push({ id: `${kind}:${g.nodes[0] ?? g.links[0]}`, kind, nodes: g.nodes, links: g.links, bounds: bounds(g.nodes, g.links) });
  };
  const notApplied = new Set([...view.notApplied.map((n) => (n.kind === "node" ? "n:" : "l:") + n.key), ...view.leftOut]);
  const applied = Object.entries(e.nodes).filter(([k]) => !notApplied.has("n:" + k));
  const appliedLinks = e.links.filter((l) => !notApplied.has(`l:${l.from} ${l.to}`));
  // the groups, in the order of their names in the file
  for (const id of Object.keys(e.groups ?? {})) {
    const nodes = applied.filter(([k, n]) => n.group === id && !onlyEnd(k, n)).map(([k]) => k);
    const links = appliedLinks.filter((l) => l.group === id).map((l) => pairKey(l.from, l.to));
    if (nodes.length + links.length > 0) groups.push({ id: `group:${id}`, kind: "group", group: id, nodes, links, bounds: bounds(nodes, links) });
  }
  const nodeEntries = applied.filter(([, n]) => n.group === undefined);
  const linkEntries = appliedLinks.filter((l) => l.group === undefined);
  // added: nodes joined by the added links between them; an added link between two game nodes is a row of its own
  const addedNodes = nodeEntries.filter(([k]) => isAdded(k)).map(([k]) => k);
  const addedLinks = linkEntries.filter((l) => !l.original).map((l) => pairKey(l.from, l.to));
  collect("added", addedNodes, addedLinks, () => undefined, isAdded);
  // hidden: hidden nodes joined by the game's links between them, hidden links with them
  const hiddenNodes = nodeEntries.filter(([k, n]) => !isAdded(k) && n.hidden).map(([k]) => k);
  const hiddenSet = new Set(hiddenNodes);
  const hiddenLinks = linkEntries.filter((l) => l.hidden).map((l) => pairKey(l.from, l.to));
  collect("hidden", hiddenNodes, hiddenLinks, (u) => {
    for (const k of hiddenNodes) {
      const i = base.index.get(k);
      if (i === undefined) continue;
      for (const j of base.nodeLinks[i]) {
        const other = base.dto.keys[base.dto.linkA[j] === i ? base.dto.linkB[j] : base.dto.linkA[j]];
        if (hiddenSet.has(other)) u.join("n:" + k, "n:" + other);
      }
    }
  });
  // moved and changed game nodes, each joined through the game's links between them
  const joinByLinks = (keys: string[]) => (u: Union) => {
    const set = new Set(keys);
    for (const k of keys) {
      const i = base.index.get(k);
      if (i === undefined) continue;
      for (const j of base.nodeLinks[i]) {
        const other = base.dto.keys[base.dto.linkA[j] === i ? base.dto.linkB[j] : base.dto.linkA[j]];
        if (set.has(other)) u.join("n:" + k, "n:" + other);
      }
    }
  };
  const moved = nodeEntries.filter(([k, n]) => !isAdded(k) && !n.hidden && (n.x !== undefined || n.y !== undefined || n.z !== undefined)).map(([k]) => k);
  collect("moved", moved, [], joinByLinks(moved));
  const changed = nodeEntries.filter(([k, n]) => !isAdded(k) && !n.hidden && (n.street !== undefined || n.highway !== undefined || n.tunnel !== undefined || n.unpaved !== undefined || n.switchedOff !== undefined)).map(([k]) => k);
  collect("changedNode", changed, [], joinByLinks(changed));
  const changedLinks = linkEntries.filter((l) => l.original && !l.hidden).map((l) => pairKey(l.from, l.to));
  collect("changedLink", [], changedLinks, () => undefined);
  return groups;
}

/** Takes the edits of a row back. */
export function revertRow(edits: RoadEditsFile, g: EditRow): RoadEditsFile {
  switch (g.kind) {
    case "group":
      return revertGroup(edits, g.group!);
    case "added":
      return removeAdded(edits, g.nodes, g.links);
    case "hidden":
      return unhide(edits, g.nodes, g.links);
    case "moved": {
      const e = copy(edits);
      for (const k of g.nodes) {
        const n = e.nodes[k];
        if (n) {
          delete n.x;
          delete n.y;
          delete n.z;
        }
      }
      prune(e);
      return e;
    }
    case "changedNode": {
      const e = copy(edits);
      for (const k of g.nodes) {
        const n = e.nodes[k];
        if (!n) continue;
        for (const f of ["street", "highway", "tunnel", "unpaved", "switchedOff"] as const) {
          delete n[f];
          if (n.original) delete n.original[f];
        }
      }
      prune(e);
      return e;
    }
    case "changedLink": {
      const e = copy(edits);
      const keys = new Set(g.links);
      e.links = e.links.filter((l) => !(l.original && !l.hidden && keys.has(pairKey(l.from, l.to))));
      prune(e);
      return e;
    }
  }
}

/**
 * Takes a group of edits back, everything of it: its added nodes go (with every added link ending at them), its link
 * edits go (added links, the edits of game links), and its game nodes are the game's again (an entry stays only where
 * another link edit ends).
 */
export function revertGroup(edits: RoadEditsFile, group: string): RoadEditsFile {
  const e = copy(edits);
  const gone = new Set<string>();
  for (const [k, n] of Object.entries(e.nodes)) {
    if (n.group !== group) continue;
    if (isAdded(k)) {
      gone.add(k);
      delete e.nodes[k];
      continue;
    }
    e.nodes[k] = { original: { x: n.original?.x, y: n.original?.y, z: n.original?.z } };
  }
  e.links = e.links.filter((l) => l.group !== group && !(l.original === undefined && (gone.has(l.from) || gone.has(l.to))));
  prune(e);
  return e;
}

/** Takes an edit that does not apply out of the edits (a node's edit with the link edits ending at it). */
export function dropNotApplied(edits: RoadEditsFile, n: NotAppliedEdit): RoadEditsFile {
  const e = copy(edits);
  if (n.kind === "node") {
    delete e.nodes[n.key];
    e.links = e.links.filter((l) => l.from !== n.key && l.to !== n.key);
  } else e.links = e.links.filter((l) => `${l.from} ${l.to}` !== n.key);
  prune(e);
  return e;
}

/** The same edits (for "nothing to save"): the same street names, nodes and links, field order and absent fields aside. */
export function sameEdits(a: RoadEditsFile, b: RoadEditsFile): boolean {
  return canonical(a) === canonical(b);
}

function canonical(e: RoadEditsFile): string {
  const sort = (v: unknown): unknown => {
    if (Array.isArray(v)) return v.map(sort);
    if (v && typeof v === "object")
      return Object.fromEntries(Object.entries(v as Record<string, unknown>).filter(([, x]) => x !== undefined).sort(([p], [q]) => (p < q ? -1 : p > q ? 1 : 0)).map(([k, x]) => [k, sort(x)]));
    return v;
  };
  return JSON.stringify(sort({ groups: e.groups ?? {}, streets: e.streets ?? {}, nodes: e.nodes, links: e.links }));
}
