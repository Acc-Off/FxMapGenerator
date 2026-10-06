import type { HelpKey } from "../shared/controls";
import type { MessageKey, Params } from "../shared/i18n";

type T = (key: MessageKey, params?: Params) => string;

/** Job-list rows (Core/Planning/Planner.cs ids): the label and the hover help. */
const ROWS: Record<string, readonly [MessageKey, HelpKey]> = {
  gameSetup: ["row.gameSetup", "help.row.gameSetup"],
  precheck: ["row.precheck", "help.row.precheck"],
  visit: ["row.visit", "help.row.visit"],
  "visit.prepare": ["row.visit.prepare", "help.row.visit.prepare"],
  "visit.shot": ["row.visit.shot", "help.row.visit.shot"],
  "visit.height": ["row.visit.height", "help.row.visit.height"],
  "visit.scanGround": ["row.visit.scanGround", "help.row.visit.scanGround"],
  "visit.scanRoads": ["row.visit.scanRoads", "help.row.visit.scanRoads"],
  "visit.scanCanopy": ["row.visit.scanCanopy", "help.row.visit.scanCanopy"],
  "visit.cleanup": ["row.visit.cleanup", "help.row.visit.cleanup"],
  ortho: ["row.ortho", "help.row.ortho"],
  gameFiles: ["row.gameFiles", "help.row.gameFiles"],
  "gameFiles.paths": ["row.gameFiles.paths", "help.row.gameFiles.paths"],
  "gameFiles.names": ["row.gameFiles.names", "help.row.gameFiles.names"],
  mapData: ["row.mapData", "help.row.mapData"],
  "mapData.roadGraph": ["row.mapData.roadGraph", "help.row.mapData.roadGraph"],
  "mapData.landcover": ["row.mapData.landcover", "help.row.mapData.landcover"],
  "mapData.regions": ["row.mapData.regions", "help.row.mapData.regions"],
  "mapData.labels": ["row.mapData.labels", "help.row.mapData.labels"],
  "mapData.roads": ["row.mapData.roads", "help.row.mapData.roads"],
  cells: ["row.cells", "help.row.cells"],
  "cells.prep": ["row.cells.prep", "help.row.cells.prep"],
  lowZoom: ["row.lowZoom", "help.row.lowZoom"],
  ytd: ["row.ytd", "help.row.ytd"],
  export: ["row.export", "help.row.export"],
};

/** Built-in atlas styles by preset id (Core/Projects/MapSet.cs AtlasPresets). */
const PRESETS: Record<string, MessageKey> = {
  postalcodemap: "preset.postalcodemap",
  regional: "preset.regional",
};

const LANGUAGES: Record<string, MessageKey> = {
  en: "lang.en",
  ja: "lang.ja",
};

export const PRESET_IDS = Object.keys(PRESETS);

/** The open project's own styles' names by id (set when the project comes in). */
let ownStyles: ReadonlyMap<string, string> = new Map();

export function setOwnStyles(list: readonly { id: string; name: string }[]) {
  ownStyles = new Map(list.map((s) => [s.id, s.name]));
}
export const LANGUAGE_IDS = Object.keys(LANGUAGES);

/** A style's name: a bundled style's in the screen's language, the project's own style's as written. */
export function presetName(t: T, id: string): string {
  const key = PRESETS[id];
  if (key) return t(key);
  return ownStyles.get(id) ?? id;
}

export function languageName(t: T, id: string): string {
  const key = LANGUAGES[id];
  return key ? t(key) : id;
}

/** A map by its id: satellite, roadmap, atlas-<preset>-<language> (an English atlas map without its language, the others with theirs). */
export function mapName(t: T, id: string): string {
  if (id === "satellite") return t("map.satellite");
  if (id === "roadmap") return t("map.roadmap");
  const m = /^atlas-(\w+)-(\w+)$/.exec(id);
  if (!m) return id;
  return m[2] === "en" ? t("map.atlasStyle", { style: presetName(t, m[1]) }) : t("map.atlasStyle.lang", { style: presetName(t, m[1]), lang: languageName(t, m[2]) });
}

/** The row's label; per-map child rows (cells.atlas-…, lowZoom.satellite) are named after their map. */
export function rowText(t: T, id: string): string {
  const row = ROWS[id];
  if (row) return t(row[0]);
  const dot = id.indexOf(".");
  return dot > 0 ? mapName(t, id.slice(dot + 1)) : id;
}

export function rowHelp(id: string): HelpKey {
  const row = ROWS[id];
  if (row) return row[1];
  if (id.startsWith("cells.")) return "help.row.cells.set";
  if (id.startsWith("lowZoom.")) return "help.row.lowZoom.set";
  return "help.row.other";
}

/** A step of a run by its job-list row, e.g. "Low zoom tiles: satellite map". */
export function stageText(t: T, row: string): string {
  const dot = row.indexOf(".");
  if (ROWS[row] || dot < 0) return rowText(t, row);
  return t("status.stepOfMap", { stage: rowText(t, row.slice(0, dot)), map: mapName(t, row.slice(dot + 1)) });
}

/** Steps inside a unit (Stage.Run reports them) and their words. */
const PHASES: Record<string, MessageKey> = {
  load: "phase.load",
  read: "phase.load",
  ortho: "phase.ortho",
  tiles: "phase.tiles",
  sea: "phase.sea",
  compose: "phase.compose",
  dxt5: "phase.dxt5",
  dxt1: "phase.dxt1",
  write: "phase.write",
  copy: "phase.copy",
  zip: "phase.zip",
  resource: "phase.resource",
  tile: "phase.tile",
  shot: "phase.shot",
  height: "phase.height",
  save: "phase.save",
  parents: "phase.parents",
  "lower zooms": "phase.parents",
  ground: "phase.ground",
  roads: "phase.roads",
  scans: "phase.scans",
  land: "phase.land",
  field: "phase.field",
  "postal codes": "phase.postals",
  place: "phase.place",
  landcover: "phase.landcover",
  graph: "phase.graph",
  paths: "phase.paths",
  links: "phase.links",
  levels: "phase.levels",
  names: "phase.names",
  shapes: "phase.shapes",
  tunnels: "phase.tunnels",
  grids: "phase.grids",
  heights: "phase.heights",
  shade: "phase.shade",
  layers: "phase.layers",
  outlines: "phase.outlines",
  draw: "phase.draw",
};

/** A step inside a unit (Stage.Run reports it). */
export function phaseText(t: T, phase: string | null): string {
  if (!phase) return "";
  const key = PHASES[phase];
  if (key) return t(key);
  const z = /^z(\d)$/.exec(phase);
  return z ? t("phase.zoom", { z: z[1] }) : phase;
}

/** Units of the job list (TodoRow.Unit) with a count. */
export function unitText(t: T, unit: string, n: number): string {
  switch (unit) {
    case "block":
      return t("unit.block", { n });
    case "cell":
      return t("unit.cell", { n });
    case "set":
      return t("unit.set", { n });
    case "language":
      return t("unit.language", { n });
    case "sheet":
      return t("unit.sheet", { n });
    case "world":
      return t("unit.world");
    case "step":
      return t("unit.step", { n });
    case "part":
      return t("unit.part", { n });
    default:
      return t("unit.once", { n });
  }
}

/** Tick marks of the workers slider: a quarter, half, three quarters and all of the processors. */
export function workerMarks(processors: number): number[] {
  return [...new Set([0.25, 0.5, 0.75, 1].map((f) => Math.max(1, Math.round(processors * f))))];
}

/** Input keys (Core/Jobs/StageInputs.cs) that the screens edit. */
export const INPUTS = {
  maps: "maps",
  range: "range",
  frame: "frame",
  minimap: "minimap",
  server: "server",
  console: "console",
  gameFiles: "gameFiles",
  cayoPerico: "cayoPerico",
  postals: "postals",
  roadEdits: "roadEdits",
  poi: "poi",
} as const;
