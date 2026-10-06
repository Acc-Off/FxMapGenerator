import { itemName, type PoiEditFolder, type PoiEditGroup, type PoiEditPoint, type PoiEditSet, type PoiEditStyle, type PoiLabel, type PoiShow } from "../../shared/types";

// The POI screen's data as the files hold it (the server writes it back file by file), and the rules the maps follow
// to settle a point's values (Core PoiData.Resolve): the style and the maps from the point, else its group, else its
// folder, else the folders above; a hidden or locked group or folder hides or locks everything in it.

/** What a point shows on when neither it, its group nor a folder says: the atlas maps only. */
export const DEFAULT_SHOW: PoiShow = { atlas: true, roadmap: false };
/** As the maps draw them (Core PoiLayout): the circle behind an icon, the label beside a dot or icon, the gap before it. */
export const ICON_BADGE = 1.5;
export const LABEL_SCALE = 0.6;
export const LABEL_GAP = 0.3;

/** The style new groups take when nothing above them names one. */
export const FALLBACK_STYLE = "facility";

export const parentOf = (path: string) => (path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : "");
export const lastOf = (path: string) => path.slice(path.lastIndexOf("/") + 1);
export const joinPath = (folder: string, name: string) => (folder ? `${folder}/${name}` : name);
/** True when a path is the folder itself or in it. */
export const within = (path: string, folder: string) => folder === "" || path === folder || path.startsWith(folder + "/");

/** A point's key on the screen: its group's path and its id. */
export const pointKey = (group: string, id: string) => `${group}\u0001${id}`;
export function splitKey(key: string): [string, string] {
  const i = key.lastIndexOf("\u0001");
  return [key.slice(0, i), key.slice(i + 1)];
}

/** A label in a language (as the maps draw it): that language's, else the English one, else the fallback. */
export function labelIn(label: PoiLabel | null | undefined, lang: string, fallback = ""): string {
  if (!label) return fallback;
  const own = label[lang];
  return (own && own.length > 0 ? own : label.en) || fallback;
}

/** A folder's or group's name on the screen: its name (a bundled one's in the screen's language), else its name on the disk. */
export const folderName = (f: PoiEditFolder, lang: "en" | "ja") => itemName(f.name, lang) || lastOf(f.path);
export const groupName = (g: PoiEditGroup, lang: "en" | "ja") => itemName(g.name, lang) || lastOf(g.path);
/** A point's name in the lists: its name, else its label (in the labels' language shown), else the fallback. */
export const pointName = (p: PoiEditPoint, labelLang: string, fallback = "") => p.name || labelIn(p.label, labelLang, fallback);

export function styleById(set: PoiEditSet, bundled: readonly PoiEditStyle[], id: string | null | undefined): PoiEditStyle | undefined {
  if (!id) return undefined;
  return bundled.find((s) => s.id === id) ?? set.styles.find((s) => s.id === id);
}

// ---------------------------------------------------------------- the tree

/** An entry of a folder: a group or a folder, by order, then name, a group before a folder of the same (as the maps walk them). */
export type Entry = { kind: "group"; group: PoiEditGroup } | { kind: "folder"; folder: PoiEditFolder };

export function entriesOf(set: PoiEditSet, folder: string): Entry[] {
  const e: (Entry & { order: number; name: string; k: number })[] = [
    ...set.groups.filter((g) => parentOf(g.path) === folder).map((g) => ({ kind: "group" as const, group: g, order: g.order, name: lastOf(g.path), k: 0 })),
    ...set.folders.filter((f) => f.path !== "" && parentOf(f.path) === folder).map((f) => ({ kind: "folder" as const, folder: f, order: f.order, name: lastOf(f.path), k: 1 })),
  ];
  e.sort((a, b) => a.order - b.order || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0) || a.k - b.k);
  return e;
}

/** The groups in the order the maps draw them. */
export function orderedGroups(set: PoiEditSet): PoiEditGroup[] {
  const out: PoiEditGroup[] = [];
  const walk = (folder: string) => {
    for (const e of entriesOf(set, folder)) {
      if (e.kind === "group") out.push(e.group);
      else walk(e.folder.path);
    }
  };
  walk("");
  return out;
}

/** The folder and the folders above it, nearest first. */
export function chainOf(set: PoiEditSet, folder: string): PoiEditFolder[] {
  const byPath = new Map(set.folders.map((f) => [f.path, f]));
  const out: PoiEditFolder[] = [];
  for (let f = folder; ; f = parentOf(f)) {
    const x = byPath.get(f);
    if (x) out.push(x);
    if (f === "") break;
  }
  return out;
}

/** The points in a folder and the folders in it. */
export function pointCount(set: PoiEditSet, folder: string): number {
  return set.groups.filter((g) => within(parentOf(g.path), folder)).reduce((n, g) => n + g.points.length, 0);
}

// ---------------------------------------------------------------- settled values

export interface Resolved {
  key: string;
  group: PoiEditGroup;
  point: PoiEditPoint;
  style: PoiEditStyle | undefined;
  show: PoiShow;
  color: string;
  size: number;
  visible: boolean;
  locked: boolean;
  labelSize: number;
}

/** Every point with its settled values, group by group in the maps' order. */
export function resolveAll(set: PoiEditSet, bundled: readonly PoiEditStyle[]): Resolved[] {
  const out: Resolved[] = [];
  for (const g of orderedGroups(set)) {
    const chain = chainOf(set, parentOf(g.path));
    for (const p of g.points) out.push(resolveOne(set, bundled, g, p, chain));
  }
  return out;
}

export function resolveOne(set: PoiEditSet, bundled: readonly PoiEditStyle[], g: PoiEditGroup, p: PoiEditPoint, chain = chainOf(set, parentOf(g.path))): Resolved {
  const styleId = p.style ?? g.style ?? chain.find((f) => f.style)?.style ?? null;
  const style = styleById(set, bundled, styleId);
  const show = p.show ?? g.show ?? chain.find((f) => f.show)?.show ?? DEFAULT_SHOW;
  const size = p.size ?? style?.size ?? 10;
  return {
    key: pointKey(g.path, p.id),
    group: g,
    point: p,
    style,
    show,
    color: p.color ?? style?.color ?? "#000000",
    size,
    visible: (p.visible ?? true) && g.visible && chain.every((f) => f.visible),
    locked: (p.locked ?? false) || g.locked || chain.some((f) => f.locked),
    labelSize: style?.labelSize ?? LABEL_SCALE * size,
  };
}

/** Where a value of a folder, group or point comes from when it has none of its own. */
export type Source = { kind: "folder"; path: string } | { kind: "group"; path: string } | { kind: "default" };

export type Target = { kind: "folder"; path: string } | { kind: "group"; path: string } | { kind: "point"; key: string };

/** The value a target takes from above (its own not counted) of a setting (style or show), and where it comes from. */
export function inherited<K extends "style" | "show">(set: PoiEditSet, target: Target, what: K): { value: PoiEditFolder[K]; from: Source } {
  let folder: string;
  if (target.kind === "point") {
    const g = set.groups.find((x) => x.path === splitKey(target.key)[0]);
    if (g && g[what] !== null) return { value: g[what] as PoiEditFolder[K], from: { kind: "group", path: g.path } };
    folder = g ? parentOf(g.path) : "";
  } else if (target.kind === "group") folder = parentOf(target.path);
  else {
    if (target.path === "") return { value: null as PoiEditFolder[K], from: { kind: "default" } };
    folder = parentOf(target.path);
  }
  for (const f of chainOf(set, folder)) if (f[what] !== null) return { value: f[what], from: { kind: "folder", path: f.path } };
  return { value: null as PoiEditFolder[K], from: { kind: "default" } };
}

/** How many folders, groups and points name a style themselves. */
export function styleUse(set: PoiEditSet, id: string): { folders: number; groups: number; points: number } {
  return {
    folders: set.folders.filter((f) => f.style === id).length,
    groups: set.groups.filter((g) => g.style === id).length,
    points: set.groups.reduce((n, g) => n + g.points.filter((p) => p.style === id).length, 0),
  };
}

// ---------------------------------------------------------------- names for the disk (Core PoiFiles.SafeName)

const RESERVED = ["CON", "PRN", "AUX", "NUL", ...[1, 2, 3, 4, 5, 6, 7, 8, 9].flatMap((n) => [`COM${n}`, `LPT${n}`])];

export function safeName(name: string): string {
  let s = [...name].map((c) => (c.charCodeAt(0) < 32 || '<>:"/\\|?*'.includes(c) ? "_" : c)).join("").trim().replace(/[. ]+$/, "");
  if (s.length > 120) s = s.slice(0, 120).replace(/[. ]+$/, "");
  if (!s || s === "_") return "_1";
  return RESERVED.includes(s.split(".")[0].toUpperCase()) ? s + "_" : s;
}

/** A name for the disk in a folder, free among the folders (or the groups) there: the name typed, else with " (2)", " (3)"... */
export function freeName(set: PoiEditSet, folder: string, name: string, kind: "folder" | "group", except?: string): string {
  const taken = new Set(
    (kind === "folder" ? set.folders.map((f) => f.path) : set.groups.map((g) => g.path))
      .filter((p) => p !== except && p !== "" && parentOf(p) === folder)
      .map((p) => lastOf(p).toLowerCase()),
  );
  const stem = safeName(name);
  if (!taken.has(stem.toLowerCase())) return stem;
  for (let n = 2; ; n++) if (!taken.has(`${stem} (${n})`.toLowerCase())) return `${stem} (${n})`;
}

// ---------------------------------------------------------------- changes (each returns a new set)

/** The next free id of a point in a group: the numbers after the largest one. */
export function nextId(g: PoiEditGroup, taken: Set<string> = new Set(g.points.map((p) => p.id))): string {
  let n = 0;
  for (const id of taken) if (/^\d+$/.test(id)) n = Math.max(n, Number(id));
  let k = n + 1;
  while (taken.has(String(k))) k++;
  return String(k);
}

export function updateGroup(set: PoiEditSet, path: string, f: (g: PoiEditGroup) => PoiEditGroup): PoiEditSet {
  return { ...set, groups: set.groups.map((g) => (g.path === path ? f(g) : g)) };
}

export function updateFolder(set: PoiEditSet, path: string, f: (x: PoiEditFolder) => PoiEditFolder): PoiEditSet {
  return { ...set, folders: set.folders.map((x) => (x.path === path ? f(x) : x)) };
}

/** Changes the points of these keys. */
export function updatePoints(set: PoiEditSet, keys: readonly string[], f: (p: PoiEditPoint, g: PoiEditGroup) => PoiEditPoint): PoiEditSet {
  const byGroup = new Map<string, Set<string>>();
  for (const k of keys) {
    const [g, id] = splitKey(k);
    let s = byGroup.get(g);
    if (!s) byGroup.set(g, (s = new Set()));
    s.add(id);
  }
  return { ...set, groups: set.groups.map((g) => (byGroup.has(g.path) ? { ...g, points: g.points.map((p) => (byGroup.get(g.path)!.has(p.id) ? f(p, g) : p)) } : g)) };
}

/** A point placed: no name of its own (the lists show its label), the label given. */
export function addPoint(set: PoiEditSet, group: string, x: number, y: number, label: PoiLabel): { set: PoiEditSet; key: string } {
  const g = set.groups.find((x2) => x2.path === group)!;
  const id = nextId(g);
  const point: PoiEditPoint = { id, name: "", label, x: round(x), y: round(y), style: null, show: null, color: null, size: null, visible: null, locked: null };
  return { set: updateGroup(set, group, (x2) => ({ ...x2, points: [...x2.points, point] })), key: pointKey(group, id) };
}

/** A position to the centimetre (what the files keep). */
export const round = (v: number) => Math.round(v * 100) / 100;

export function removePoints(set: PoiEditSet, keys: readonly string[]): PoiEditSet {
  const gone = new Set(keys);
  return { ...set, groups: set.groups.map((g) => (g.points.some((p) => gone.has(pointKey(g.path, p.id))) ? { ...g, points: g.points.filter((p) => !gone.has(pointKey(g.path, p.id))) } : g)) };
}

/** Moves points into a group (at its end), with new ids where theirs are taken; returns their new keys. */
export function movePointsTo(set: PoiEditSet, keys: readonly string[], group: string): { set: PoiEditSet; keys: string[] } {
  const target = set.groups.find((g) => g.path === group);
  if (!target) return { set, keys: [...keys] };
  const moving: PoiEditPoint[] = [];
  for (const k of keys) {
    const [gp, id] = splitKey(k);
    if (gp === group) continue;
    const p = set.groups.find((g) => g.path === gp)?.points.find((x) => x.id === id);
    if (p) moving.push(p);
  }
  let next = removePoints(set, keys.filter((k) => splitKey(k)[0] !== group));
  const taken = new Set(target.points.map((p) => p.id));
  const added = moving.map((p) => {
    const id = taken.has(p.id) ? nextId(target, taken) : p.id;
    taken.add(id);
    return { ...p, id };
  });
  next = updateGroup(next, group, (g) => ({ ...g, points: [...g.points, ...added] }));
  return { set: next, keys: [...keys.filter((k) => splitKey(k)[0] === group), ...added.map((p) => pointKey(group, p.id))] };
}

/** A folder added: its name on the disk made from its name. */
export function addFolder(set: PoiEditSet, parent: string, name: string): { set: PoiEditSet; path: string } {
  const path = joinPath(parent, freeName(set, parent, name || "folder", "folder"));
  const order = Math.max(-1, ...entriesOf(set, parent).map((e) => (e.kind === "group" ? e.group.order : e.folder.order))) + 1;
  return { set: { ...set, folders: [...set.folders, { path, name, order, style: null, show: null, visible: true, locked: false }] }, path };
}

/** A group added: its file's name made from its name. */
export function addGroup(set: PoiEditSet, folder: string, name: string, style: string | null, points: PoiEditPoint[] = []): { set: PoiEditSet; path: string } {
  const path = joinPath(folder, freeName(set, folder, name || "group", "group"));
  const order = Math.max(-1, ...entriesOf(set, folder).map((e) => (e.kind === "group" ? e.group.order : e.folder.order))) + 1;
  // a group whose folders name no style takes one, so that its points can be drawn
  const needs = !chainOf(set, folder).some((f) => f.style);
  const group: PoiEditGroup = { path, points, name, order, style: style ?? (needs ? FALLBACK_STYLE : null), show: null, visible: true, locked: false, credit: null };
  return { set: { ...set, groups: [...set.groups, group] }, path };
}

/** A path moved: the folder or group itself and everything in it. */
function repath(path: string, from: string, to: string): string {
  return path === from ? to : path.startsWith(from + "/") ? to + path.slice(from.length) : path;
}

/** Renames a folder (its name on the screen and on the disk); returns its new path. */
export function renameFolder(set: PoiEditSet, path: string, name: string): { set: PoiEditSet; path: string } {
  const to = joinPath(parentOf(path), freeName(set, parentOf(path), name || lastOf(path), "folder", path));
  const moved = movePaths(set, path, to);
  return { set: updateFolder(moved, to, (f) => ({ ...f, name })), path: to };
}

export function renameGroup(set: PoiEditSet, path: string, name: string): { set: PoiEditSet; path: string } {
  const to = joinPath(parentOf(path), freeName(set, parentOf(path), name || lastOf(path), "group", path));
  return { set: updateGroup({ ...set, groups: set.groups.map((g) => (g.path === path ? { ...g, path: to } : g)) }, to, (g) => ({ ...g, name })), path: to };
}

/** Moves a folder's path (and everything in it) to another. */
function movePaths(set: PoiEditSet, from: string, to: string): PoiEditSet {
  if (from === to) return set;
  return {
    ...set,
    folders: set.folders.map((f) => ({ ...f, path: repath(f.path, from, to) })),
    groups: set.groups.map((g) => ({ ...g, path: repath(g.path, from, to) })),
  };
}

/** Maps a key or path of the selection after a folder or group moved. */
export function remap(path: string, from: string, to: string): string {
  return repath(path, from, to);
}

export function remapKey(key: string, from: string, to: string): string {
  const [g, id] = splitKey(key);
  return pointKey(repath(g, from, to), id);
}

/**
 * Puts a folder or group into a folder, before an entry of it (or at its end): its path and name on the disk change
 * with the folder, the entries of the folder are numbered anew in their order. Returns its new path, or null when it
 * cannot go there (a folder into itself).
 */
export function place(set: PoiEditSet, kind: "folder" | "group", path: string, into: string, before: Entry | null): { set: PoiEditSet; path: string } | null {
  if (kind === "folder" && within(into, path)) return null;
  const name = lastOf(path);
  const to = parentOf(path) === into ? path : joinPath(into, freeName(set, into, name, kind));
  let next = kind === "folder" ? movePaths(set, path, to) : { ...set, groups: set.groups.map((g) => (g.path === path ? { ...g, path: to } : g)) };
  // the folder's entries in order, this one taken out and put back before the entry asked for
  const entries = entriesOf(next, into).filter((e) => !(e.kind === kind && (e.kind === "group" ? e.group.path : e.folder.path) === to));
  const self = entriesOf(next, into).find((e) => e.kind === kind && (e.kind === "group" ? e.group.path : e.folder.path) === to)!;
  const at = before ? entries.findIndex((e) => e.kind === before.kind && pathOf(e) === pathOf(before)) : -1;
  entries.splice(at < 0 ? entries.length : at, 0, self);
  const orderOf = new Map(entries.map((e, i) => [`${e.kind}:${pathOf(e)}`, i]));
  next = {
    ...next,
    folders: next.folders.map((f) => (orderOf.has(`folder:${f.path}`) ? { ...f, order: orderOf.get(`folder:${f.path}`)! } : f)),
    groups: next.groups.map((g) => (orderOf.has(`group:${g.path}`) ? { ...g, order: orderOf.get(`group:${g.path}`)! } : g)),
  };
  return { set: next, path: to };
}

export const pathOf = (e: Entry) => (e.kind === "group" ? e.group.path : e.folder.path);

export function removeFolder(set: PoiEditSet, path: string): PoiEditSet {
  return { ...set, folders: set.folders.filter((f) => f.path === "" || !within(f.path, path)), groups: set.groups.filter((g) => !within(g.path, path)) };
}

export function removeGroup(set: PoiEditSet, path: string): PoiEditSet {
  return { ...set, groups: set.groups.filter((g) => g.path !== path) };
}

/** A copy of a set that compares by value (the save sends it; undo keeps it). */
export function sameSet(a: PoiEditSet | null, b: PoiEditSet | null): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

// ---------------------------------------------------------------- styles

/** An id free among the POI styles, made from a name: its letters and digits in small letters with hyphens, a number added while taken. */
export function freeStyleId(name: string, taken: ReadonlySet<string>): string {
  let stem = name.toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40);
  if (!stem || !/^[a-z]/.test(stem)) stem = "style" + (stem ? "-" + stem : "");
  if (!taken.has(stem)) return stem;
  for (let n = 2; ; n++) if (!taken.has(`${stem}-${n}`)) return `${stem}-${n}`;
}

export function newStyle(id: string, name: string): PoiEditStyle {
  return { id, name, look: "icon", color: "#d02020", size: 30, weight: "bold", outline: "#ffffff", outlineWidth: 3, badgeColor: null, showLabel: true, icon: "map-marker", image: null, labelSize: null };
}

/**
 * A style's look changed: a badge's text is white on the former colour (the badge's circle takes the colour, its text
 * the style's colour), and what the new look does not use is taken away.
 */
export function withLook(s: PoiEditStyle, look: PoiEditStyle["look"]): PoiEditStyle {
  let next: PoiEditStyle = { ...s, look };
  if (look === "badge" && s.look !== "badge") next = { ...next, badgeColor: s.color, color: "#ffffff" };
  if (look !== "icon") next = { ...next, icon: null, image: null };
  if (look === "icon" && !s.icon && !s.image) next = { ...next, icon: "map-marker" };
  if (look === "text" || look === "badge") next = { ...next, showLabel: false, labelSize: null };
  if (look === "dot") next = { ...next, badgeColor: null };
  return next;
}
