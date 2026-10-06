// Mirrors of the server's DTOs (src/FxMapGenerator.App/Web/*.cs, Projects/ProjectSession.cs, Core/Jobs/JobSnapshot.cs,
// Core/Planning/TodoTable.cs).

export interface ApiErrorBody {
  code: string;
  message: string;
}

export interface StatusDto {
  version: string;
}

export type LanguageSetting = "auto" | "ja" | "en";
export type ThemeSetting = "system" | "light" | "dark";

export interface AppSettings {
  recentProjects: string[];
  language: LanguageSetting;
  theme: ThemeSetting;
  /** The project screen's right-hand panel (job list, prerequisites), CSS pixels. */
  jobListWidth: number;
  /** The GTA V folder of this PC; null = found in the registry. */
  gtaFolder?: string | null;
  /** The RPF key folder of this PC; null = the key search order. */
  keysFolder?: string | null;
  /** The answers about the screens' guides, by guide id; a screen without one asks when it is first opened. */
  guides: Record<string, GuideAnswer>;
}

/** seen: shown (to its end or closed on the way); never: not to be asked about again. */
export type GuideAnswer = "seen" | "never";

/** A key folder that was tried and the key files it lacks. */
export interface KeysTried {
  folder: string;
  source: string;
  missing: string[];
}

/** Where the game files come from with the app's settings (GET /api/settings/gamefiles). */
export interface GameFilesStatus {
  gta: string | null;
  gtaSource: string | null;
  gtaFound: boolean;
  /** notFound: none given and none in the registry; notGta: no GTA5.exe there. */
  gtaProblem: "notFound" | "notGta" | null;
  gtaFromCommandLine: boolean;
  keys: string | null;
  keysSource: string | null;
  keysFound: boolean;
  keysTried: KeysTried[];
  keysFromCommandLine: boolean;
}

/** What the scans and the landcover say at a point (GET /api/project/point). */
export interface PointDto {
  block: string | null;
  scanned: boolean | null;
  /** The game's material name; "" = the ray hit nothing. */
  material: string | null;
  materialClass: string | null;
  height: number | null;
  water: number | null;
  zone: string | null;
  zoneName: string | null;
  zoneJa: string | null;
  /** English street name; "" = no street. */
  street: string | null;
  streetJa: string | null;
  onRoad: boolean | null;
  landcover: string | null;
  building: boolean | null;
}

export interface LibraryInfo {
  name: string;
  version: string;
}

export interface DiagnosticsDto {
  version: string;
  dataDirectory: string;
  settingsPath: string;
  logPath: string;
  url: string;
  libraries: LibraryInfo[];
}

// ---- projects

export type ServerPreset = "qbox" | "qbcore";

export type MinimapOutside = "map" | "transparent";

export interface ProjectFile {
  format: number;
  name: string;
  workFolder: string;
  /** The atlas maps: every style of the list (style ids) in every language of the list (English always, Japanese when listed). */
  maps: { satellite: boolean; atlas: { enabled: boolean; styles: string[]; languages: string[] }; roadmap: boolean };
  /** outside: the blocks outside the range painted as the map has them, or left transparent. */
  minimap: { map: string | null; outside: MinimapOutside };
  /** extraCells: cells (2250 m) added above, below, left and right of the standard map; null = none. */
  range: { base: string; add: string[]; remove: string[]; extraCells: Sides | null };
  server: { preset: ServerPreset; stopResources: string[] | null };
  console: { host: string; port: number };
  gameFiles: { serverResources: string[] };
  /** Where the postal codes come from: an http(s) address or a file; null = nearest-postal's table. */
  postals: string | null;
  /** The height quality; null = the default for the maps (see heightQualityOf). */
  heightQuality: HeightQuality | null;
  /** The road edits file (relative to the project file); null = no edits. */
  roadEdits: string | null;
  /** Default workers of new runs. */
  parallel: number;
  /** The game files step reads the Cayo Perico island's road files (the server runs the island). */
  cayoPerico: boolean;
}

/** Cells (2250 m) on each side of the standard map. */
export interface Sides {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

export interface ProjectDto {
  path: string;
  name: string;
  workFolder: string;
  file: ProjectFile;
  maps: string[];
  rangeBlocks: number;
  rangeLand: number;
  rangeWater: number;
  cells: number;
  /** The resources the visit stops: the project's list, or its preset's. */
  stopResources: string[];
  /** The preset's list (what "back to the preset" gives). */
  presetStopResources: string[];
  presets: { id: ServerPreset; name: string }[];
  /** The project's own styles the maps can use (id and name). */
  ownStyles: { id: string; name: string }[];
  /** The project's map frame (the standard frame with the cells it adds). */
  frame: MapFrame;
  /** The fewest cells on each side of the standard map that keep the range's blocks inside the frame. */
  frameNeeded: Sides;
  /** Blocks of the range outside the standard map. */
  rangeOutside: number;
  /** The range presets the plan map offers. */
  rangePresets: RangePresetDto[];
}

/**
 * A range preset (RangePresetDto in ProjectSession.cs): its blocks by class; those not in the range yet, by their class
 * once added, and of them the ones outside the project's frame; the cells the frame needs on each side of the standard
 * map to hold them all; the edges of its blocks (game metres).
 */
export interface RangePresetDto {
  id: RangePresetId;
  land: number;
  water: number;
  missingLand: number;
  missingWater: number;
  missingOutside: number;
  needs: Sides;
  west: number;
  north: number;
  east: number;
  south: number;
}

/** The bundled range presets (data/range-presets.json). */
export type RangePresetId = "cayoPerico" | "roxwood";

/**
 * A project's map frame (MapFrameDto in ProjectSession.cs): its first block column and row (negative when cells are added
 * to the left or above: the numbers keep the standard frame's north-west corner as their origin), its blocks across and
 * down, its edges (game metres).
 */
export interface MapFrame extends BlockGrid {
  west: number;
  north: number;
  east: number;
  south: number;
}

/** The blocks of a frame as the row-by-row lists of the API hold them: block (bx, by) is at (by - by0) * cols + bx - bx0. */
export interface BlockGrid {
  bx0: number;
  by0: number;
  cols: number;
  rows: number;
}

export interface RecentProjectDto {
  path: string;
  name: string;
  exists: boolean;
  problem: string | null;
}

/** What the visit takes for the surface heights (SurfaceHeights.cs): the height data, the ground scan or both. */
export type HeightQuality = "speed" | "balance" | "quality";

/**
 * Whether the project makes Japanese maps (atlas maps with Japanese among their languages): only then do the screens ask
 * for Japanese labels and offer the labels' language (as Project.MakesJapanese).
 */
export function makesJapanese(f: ProjectFile): boolean {
  return f.maps.atlas.enabled && f.maps.atlas.languages.includes("ja");
}

/** The quality in use: the chosen one, or the default for the maps (as SurfaceHeights.QualityOf). */
export function heightQualityOf(f: ProjectFile): HeightQuality {
  const cellMaps = f.maps.atlas.enabled || f.maps.roadmap;
  return f.heightQuality ?? (!cellMaps ? "speed" : "quality");
}

export interface ProjectEdit {
  name?: string;
  satellite?: boolean;
  /** The styles (ids) and the languages (English always) of the atlas maps. */
  atlas?: { enabled?: boolean; styles?: string[]; languages?: string[] };
  roadmap?: boolean;
  /** "" = back to the default for the maps. */
  heightQuality?: HeightQuality | "";
  minimapMap?: string;
  minimapOutside?: MinimapOutside;
  parallel?: number;
  serverPreset?: ServerPreset;
  /** preset: a range preset's blocks put in (after the others; the frame must hold them). */
  range?: { include?: string[]; exclude?: string[]; reset?: boolean; preset?: RangePresetId };
  /** The whole list, or back to the preset's. */
  stopResources?: { list?: string[]; preset: boolean };
  /** The whole list of the server's own resources for the road data, in order. */
  serverResources?: string[];
  console?: { host: string; port: number };
  /** An http(s) address or a file; "" = nearest-postal's table. */
  postals?: string;
  /** The areas outside the standard map: on with the cells added on each side, or off (none added). */
  extraCells?: { on: boolean } & Partial<Sides>;
  cayoPerico?: boolean;
}

/** A place where a map's tiles changed: west, north, east, south (m) and the z8 tiles changed there. */
export interface ChangedArea {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  tiles: number;
  /** None of its tiles shows the change (every pixel within a few steps of before). */
  faint: boolean;
}

/**
 * What changed of a map since its tiles were kept (the last export): tiles kept, the z8 tiles (x, y, ...) and those whose
 * change does not show, the places (those that show the change first, largest first, then those that do not).
 */
export interface ChangedMap {
  map: string;
  tiles: number;
  z8: number[];
  faintZ8: number[] | null;
  areas: ChangedArea[];
}

export interface BeforeDto {
  maps: ChangedMap[];
}

/** Blocks of the range marked for retake, or the mark taken off (`retake: false`); `all`: every mark off. */
export interface RetakeRequest {
  blocks?: string[];
  retake?: boolean;
  all?: boolean;
}

/** Every block of the project's frame row by row from its north-west corner (a BlockGrid); see BlocksDto in ProjectEndpoints.cs. */
export interface BlocksDto extends BlockGrid {
  range: string;
  defaultRange: string;
  items: number[];
  retake: string;
  ortho: string;
  cells: string[];
  /** The colour the satellite map's open sea is painted with (#rrggbb); null until its lower zooms are made. */
  satelliteSea: string | null;
}

export interface TodoRow {
  id: string;
  needed: boolean;
  reason: "notNeeded" | "done" | "noVisit" | null;
  remaining: number;
  unit: string;
  land: number | null;
  water: number | null;
  for: string[];
  low: number;
  high: number;
  usesGame: boolean;
  byHand: boolean;
  targets: string[];
  children: TodoRow[];
}

export interface TodoTable {
  rows: TodoRow[];
  low: number;
  high: number;
  gameLow: number;
  gameHigh: number;
  workers: number;
  processors: number;
  rangeBlocks: number;
  rangeLand: number;
  rangeWater: number;
  cells: number;
}

export interface PlanDto {
  table: TodoTable;
  runnable: string[];
  /** Per runnable row: units a run could do right now (blocks without their capture wait for the visit). */
  ready: Record<string, number>;
  notes: string[];
  /** What was left per row when the running (or last) run of this project started (the job list counts down from it). */
  atStart: { runId: string; remaining: Record<string, number> } | null;
}

export interface CheckDto {
  id: "capture" | "game" | "render" | "preset" | "gameFiles" | "serverResources" | "postals" | "fonts" | "disk";
  ok: boolean | null;
  values: Record<string, string>;
}

/** That the capture resource of a version is on the project's server (Core/Capture/ResourcePlacement.cs). */
export interface ResourcePlacement {
  version: string;
  /** saved: the app wrote it into a folder; answered: it answered in the game; user: the user said so. */
  how: "saved" | "answered" | "user";
  atUtc: string;
  folder: string | null;
}

/** The capture resource inside the exe, and the open project's mark (GET /api/capture-resource). */
export interface CaptureResourceDto {
  name: string;
  version: string;
  placed: ResourcePlacement | null;
  /** The folder just written (save only). */
  written: string | null;
}

/** POST /api/game/resource/start: what the game said, and the console's lines of the attempt. */
export interface ResourceStartDto {
  state: "noConsole" | "running" | "started" | "notStarted";
  /** The name it answered under (another folder name still captures). */
  resource: string | null;
  version: string | null;
  /** The version of the exe's resource. */
  expected: string;
  ace: boolean | null;
  console: string[];
  /** Why it did not start: the server has no resource of that name, refused the command, or said nothing. */
  reason: "notFound" | "denied" | "noAnswer" | null;
}

// ---- jobs

export type JobState = "running" | "stopping" | "stopped" | "done" | "failed";
export type StageState = "waiting" | "running" | "done" | "stopped" | "failed";

export interface StageProgress {
  id: string;
  row: string;
  unit: string;
  state: StageState;
  usesGame: boolean;
  /** Finding its units first (and measuring what it needs, like the scale correction). */
  preparing: boolean;
  total: number;
  done: number;
  failed: number;
  interrupted: number;
  startedUtc: string | null;
  endedUtc: string | null;
  remainingLow: number;
  remainingHigh: number;
  error: string | null;
}

export interface ActiveUnit {
  stage: string;
  unit: string;
  phase: string | null;
  fraction: number;
  startedUtc: string;
  seconds: number;
}

export interface UnitFailure {
  stage: string;
  unit: string;
  message: string;
  atUtc: string;
}

/** Something a stage tells apart from its progress (JobSnapshot.cs): gameDone = the work in the game is over. */
export interface Announcement {
  key: "gameDone" | string;
  /** gameDone: resource, stopped (1 / 0 / kept / unknown), restarted (the resources started again, comma-separated). */
  values: Record<string, string>;
  atUtc: string;
}

export interface JobSnapshot {
  runId: string;
  runFolder: string;
  project: string;
  projectName: string;
  state: JobState;
  stopMode: "none" | "boundary" | "now";
  workers: number;
  processors: number;
  running: number;
  retiring: number;
  memoryLimitedAt: number | null;
  startedUtc: string;
  endedUtc: string | null;
  elapsed: number;
  stage: string | null;
  stages: StageProgress[];
  active: ActiveUnit[];
  failureCount: number;
  failures: UnitFailure[];
  remainingLow: number;
  remainingHigh: number;
  cpu: number;
  memoryAvailable: number;
  memoryTotal: number;
  processMemory: number;
  locks: { key: string; stages: string[] }[];
  notes: string[];
  error: string | null;
  /** Why a stage stopped the run (the game is gone, another player came on). */
  stopReason: string | null;
  announcements: Announcement[];
}

export interface StageUnits {
  id: string;
  row: string;
  unit: string;
  state: StageState;
  targets: string[];
  done: string[];
  failed: string[];
  interrupted: string[];
}

export interface JobLogLine {
  runId: string;
  atUtc: string;
  line: string;
}

// ---- the game: pre-check and graphics settings (Core/Capture/Precheck.cs, App/Game/RenderSettingsFile.cs)

export type PrecheckItemId = "console" | "resource" | "window" | "resources" | "testShot" | "environment" | "settle";

export interface PrecheckItem {
  id: PrecheckItemId;
  /** null when it was not checked (an earlier check failed). */
  ok: boolean | null;
  values: Record<string, string>;
  /** The server's words (English), for the details. */
  message: string;
  /** What is wrong, for the screen's own words; empty when fine. */
  codes: string[] | null;
}

export interface PrecheckBox {
  x: number;
  y: number;
  width: number;
  height: number;
  /** In the part of the frame the map uses; boxes off it are shown but do not fail the check. */
  inMap: boolean;
}

/** One block of the settle measurement. */
export interface SettleBlock {
  block: string;
  /** From this time after READY on (ms) the frames no longer changed; null when not measured (problem). */
  stableMs: number | null;
  fps: number;
  settleMs: number;
  maxReq: number;
  water: number;
  samples: { ms: number; changed: number | null }[];
  /** tile, water, frames, unsettled; null when measured. */
  problem: string | null;
  reason: string | null;
}

/** The settle measurement of a pre-check: the wait before each shot comes from it. */
export interface SettleMeasurement {
  atUtc: string;
  district: "north" | "south";
  blocks: SettleBlock[];
  stableMs: number | null;
  waitMs: number;
  fps: number | null;
  ok: boolean;
}

export interface PrecheckReport {
  atUtc: string;
  folder: string;
  items: PrecheckItem[];
  boxes: PrecheckBox[];
  notification: boolean;
  settle: SettleMeasurement | null;
  ok: boolean;
  /** When the app stopped the capture resource after this check (the end of a capture): the check counts as not made. */
  resourceStoppedUtc?: string | null;
}

/** What uses the game by hand (the connection check, the start of the capture resource); null: nothing. */
export type GameUse = "precheck" | "resourceStart" | null;

export interface RenderDifference {
  section: string;
  key: string;
  current: string | null;
  wanted: string;
  why: "detail" | "lod" | "window" | "still" | "stable";
}

export interface RenderStatus {
  path: string;
  exists: boolean;
  fiveMRunning: boolean;
  differences: RenderDifference[];
  backups: string[];
}

export interface RenderChange {
  backup: string;
  status: RenderStatus;
}

// ---- export (Core/Export/*.cs, App/Web/ExportEndpoints.cs)

export interface TileSetInfo {
  map: string;
  tiles: number;
  bytes: number;
  zooms: number[];
  upToDate: boolean;
  /** Per zoom level 0..8. */
  tilesPerZoom: number[];
  bytesPerZoom: number[];
}

export interface MinimapInfo {
  map: string | null;
  size: number;
  /** Textures made and up to date, of `total`: the 6 sheets, the small whole map and the textures beyond the standard map. */
  made: number;
  bytes: number;
  /** The textures beyond the standard map the range needs (one per cell). */
  extra: number;
  /** The resource carries Cayo Perico's island map drawing nothing (the project reads the island's roads). */
  islandMap: boolean;
  /** Cayo Perico's land blocks the range does not hold. */
  islandLandMissing: number;
  /** Why this PC cannot read what the resource takes from the game's files (the interior maps, the island map): GTA V or the keys not found; null = it can. */
  game: "gta" | "keys" | null;
  total: number;
  ready: boolean;
}

export interface ExportChoices {
  folder: string | null;
  maps: string[] | null;
  zip: boolean | null;
  baseUrl: string | null;
  minimap: boolean | null;
  resourceName: string | null;
  /** The finest zoom of the web tiles (6-8). */
  maxZoom: number | null;
  /** The maps written as layered files for editing; never kept between exports. */
  editableMaps: string[] | null;
  /** The zoom level of the layered files (6 or 7). */
  editableZoom: number | null;
  /** The formats of the layered files ("psd", "svg"); empty = none chosen. */
  editableFormats: string[] | null;
  /** The language of the screens, for the names of the layers (sent with an export). */
  language?: string | null;
}

export interface ExportProblem {
  code:
    | "NOTHING" | "NO_TILES" | "MINIMAP_NOT_READY" | "NO_MINIMAP" | "MINIMAP_NO_GTA" | "MINIMAP_NO_KEYS" | "NOT_EMPTY" | "BAD_NAME" | "BAD_URL" | "BAD_ZOOM"
    | "NO_FOLDER" | "EDITABLE_NOT_READY" | "BAD_EDITABLE" | "NO_EDITABLE_FORMAT"
    // of the conversion of an edited picture only
    | "BAD_PICTURE" | "BAD_MAP";
  message: string;
}

/** A map that can be written as a layered file (see Core/Export/ExportInventory.cs). */
export interface EditableMapInfo {
  map: string;
  /** Its tiles and, for an atlas or road map, its cells' data are at hand. */
  ready: boolean;
  upToDate: boolean;
}

export interface EditableInfo {
  maps: EditableMapInfo[];
  /** The blocks of the frame's sides: a picture is 256 px a block at zoom 6, 512 at zoom 7. */
  blocksX: number;
  blocksY: number;
}

/** A layered file in the export folder's editable/ folder. */
export interface ExportEditable {
  atUtc: string;
  map: string;
  zoom: number;
  format: string;
  file: string;
  bytes: number;
  width: number;
  height: number;
  layers: string[];
}

export interface ExportRecord {
  atUtc: string;
  version: string;
  project: string;
  web: { atUtc: string; maps: string[]; zip: boolean; tiles: number; bytes: number; baseUrl: string | null; complete: boolean; maxZoom: number } | null;
  /** fromPicture: its pictures were made from an edited picture, not from the map's tiles. */
  resources: { atUtc: string; name: string; map: string; size: number; fromPicture: boolean }[];
  editable: ExportEditable[] | null;
  /** The web tiles of edited pictures in the folder's web-edited/ folder (name: the folder there). */
  webEdited: { atUtc: string; name: string; map: string; picture: string; zoom: number; tiles: number; bytes: number; complete: boolean }[] | null;
}

export interface ExportInfo {
  maps: TileSetInfo[];
  minimap: MinimapInfo;
  options: ExportChoices;
  problems: ExportProblem[];
  last: ExportRecord | null;
  defaultFolder: string;
  editable: EditableInfo;
  /** The edited picture last converted (its full path) and the map taken for it. */
  picture: { file: string | null; map: string | null };
}

/** A file as an edited picture (see Core/Export/PictureConvert.cs): its size, the zoom level it stands for, why it cannot be converted. */
export interface PictureInfo {
  file: string;
  width: number;
  height: number;
  zoom: number | null;
  problem: "NOT_FOUND" | "NOT_PNG" | "INTERLACED" | "BAD_SIZE" | null;
}

/** The choices of a conversion of an edited picture (ConvertRequest in ExportEndpoints.cs). */
export interface ConvertChoices {
  folder: string | null;
  file: string | null;
  /** The map the picture was made from; null = the one its name tells, else the minimap's, else the first. */
  map: string | null;
  tiles: boolean;
  minimap: boolean;
  baseUrl: string | null;
}

export interface ConvertInfo {
  picture: PictureInfo | null;
  map: string | null;
  /** The map the picture's file name tells, or null. */
  guessedMap: string | null;
  problems: ExportProblem[];
  resourceName: string | null;
}

// ---- road editor (see RoadEditorEndpoints.cs)

/** A street name of the game: hash, English, Japanese. */
export interface StreetName {
  hash: number;
  en: string;
  ja: string | null;
}

/**
 * The game's path data as columns. Node flags: 1 junction, 2 highway, 4 tunnel, 8 unpaved, 16 switched off. Links: one
 * per connection from linkA to linkB (node indexes), lanes that way and back, flags 1 narrow, 2 not for navigation, 4
 * shortcut.
 */
export interface RoadPathsDto {
  version: string;
  keys: string[];
  x: number[];
  y: number[];
  z: number[];
  street: number[];
  flags: number[];
  linkA: number[];
  linkB: number[];
  lanesForward: number[];
  lanesBack: number[];
  linkFlags: number[];
  streets: StreetName[];
}

/** Values of a node in the road edits file; absent = not given (a game node: unchanged). */
export interface RoadNodeValues {
  x?: number;
  y?: number;
  z?: number;
  street?: number;
  highway?: boolean;
  tunnel?: boolean;
  unpaved?: boolean;
  switchedOff?: boolean;
}

export interface RoadNodeEdit extends RoadNodeValues {
  hidden?: boolean;
  original?: RoadNodeValues;
  /** The group of edits it belongs to (a key of RoadEditsFile.groups). */
  group?: string;
}

export interface RoadLinkValues {
  lanesForward?: number;
  lanesBack?: number;
  narrow?: boolean;
}

export interface RoadLinkEdit extends RoadLinkValues {
  from: string;
  to: string;
  width?: number | null;
  hidden?: boolean;
  original?: RoadLinkValues;
  /** The group of edits it belongs to (a key of RoadEditsFile.groups). */
  group?: string;
}

/** A street name the road edits add: English, and Japanese when given (a label: drawn on the maps of each language). */
export interface AddedStreetName {
  en: string;
  ja?: string;
}

/** The road edits file (Docs/spec/project-format.ja.md). */
export interface RoadEditsFile {
  format: 1;
  /** Groups of edits that belong together (the bundled edits), by id (the nodes' and links' group); left out when none. */
  groups?: Record<string, RoadEditGroup>;
  /** The street names the edits add, by hash (the nodes' street); left out when none. */
  streets?: Record<string, AddedStreetName>;
  nodes: Record<string, RoadNodeEdit>;
  links: RoadLinkEdit[];
}

export type NotAppliedReason = "nodeMissing" | "nodeChanged" | "linkMissing" | "linkChanged" | "linkExists" | "endNotApplied";

export interface NotAppliedEdit {
  kind: "node" | "link";
  /** A node's key, or a link's two node keys separated by a space. */
  key: string;
  reason: NotAppliedReason;
}

export interface RoadEditorDto {
  /** Why the editor cannot be used, or null. */
  unavailable: "noRoadMaps" | "noGameFiles" | null;
  file: string | null;
  edits: RoadEditsFile | null;
  problem: string | null;
  notApplied: NotAppliedEdit[];
  pathsVersion: string | null;
  /** The road shapes last made (null: not made yet). */
  shapesVersion: string | null;
  /** The road shapes step is left to do: the shapes shown are older than the edits saved. */
  shapesLeft: boolean;
  /** The maps whose colours the road tiles can take. */
  maps: string[];
}

export interface RoadNodeDto {
  key: string;
  values: Record<string, unknown>;
  street: StreetName | null;
}

export interface RoadLinkDto {
  from: string;
  to: string;
  records: Record<string, unknown>[];
}

export interface RoadGroundDto {
  block: string | null;
  scanned: boolean;
  material: string | null;
  materialClass: string | null;
  height: number | null;
  water: number | null;
}

/** Names in English and Japanese (Japanese may be missing: the English one then). */
export interface Names {
  en: string;
  ja?: string;
}

/**
 * The name of something a project keeps (a POI folder, group or style, a style, a group of road edits): one text in the
 * user's own words. What the app bundles carries its English and Japanese names instead, shown in the screen's language;
 * a copy of it takes the name of the screen's language at that moment.
 */
export type ItemName = string | Names;

/** A name as a screen in `lang` shows it. */
export function itemName(n: ItemName | null | undefined, lang: "en" | "ja"): string {
  if (n == null) return "";
  if (typeof n === "string") return n;
  return lang === "ja" && n.ja ? n.ja : n.en;
}

/** The name a copy takes: one text (a bundled name's in `lang`). */
export function copiedName(n: ItemName, lang: "en" | "ja"): string {
  return itemName(n, lang);
}

/**
 * A group of road edits as the file holds it: its name, or an object of the name (`name`, a bundled group's `en` and
 * `ja`) with `whereFound`: the group's edits of the game's nodes and links apply where the project's path data holds
 * them as the edit found them, and are left out without being listed where it does not (the bundled group that hides
 * North Yankton's roads).
 */
export type RoadEditGroup = string | (Names & { whereFound?: boolean }) | { name: string; whereFound?: boolean };

export function groupName(g: RoadEditGroup): ItemName {
  if (typeof g === "string") return g;
  if ("name" in g) return g.name;
  return g.ja !== undefined ? { en: g.en, ja: g.ja } : { en: g.en };
}

export function groupWhereFound(g: RoadEditGroup | undefined): boolean {
  return typeof g === "object" && g !== null && g.whereFound === true;
}

/** The group as a copy keeps it: one name (a bundled name's in `lang`), with its `whereFound`. */
export function copiedGroup(g: RoadEditGroup, lang: "en" | "ja"): RoadEditGroup {
  const name = copiedName(groupName(g), lang);
  return groupWhereFound(g) ? { name, whereFound: true } : name;
}

/** The controls a row of the style table edits its value with. */
export type StyleControl = "color" | "number" | "choice" | "check" | "checks" | "font" | "text" | "table";
/** `percent`: a value of 0 to 1 in the style, shown and entered as 0 to 100 %. */
export type StyleUnit = "m" | "m2" | "deg" | "px" | "em" | "percent";
/** The rows of the project screen's table a change makes the maps rebuild. */
export type StyleRebuild = "cells" | "cells.prep" | "mapData.regions" | "mapData.labels";

export interface StyleChoice {
  value: string;
  name: Names;
}

/** What a row and a table's column share: names, help, control, range, choices, what a change rebuilds. */
export interface StyleEntry {
  name: Names;
  description: Names;
  control: StyleControl;
  min?: number;
  max?: number;
  step?: number;
  unit?: StyleUnit;
  choices?: StyleChoice[];
  rebuilds: StyleRebuild[];
}

export interface StyleColumn extends StyleEntry {
  id: string;
}

/** A row of the style table (data/style-schema.json): a place in the style and how it is edited. */
export interface StyleItem extends StyleEntry {
  key: string;
  /** A heading shown above the row (the first row of a part of its group). */
  heading?: Names;
  /** The row shows only while the value at `key` equals `equals`. */
  when?: { key: string; equals: string };
  /** The row is for the maps of this language: it shows only while the project makes them. */
  language?: string;
  columns?: StyleColumn[];
}

export interface StyleGroup {
  id: string;
  name: Names;
  items: StyleItem[];
}

export interface StyleSchema {
  format: number;
  groups: StyleGroup[];
}

/** A style's values as a style file holds them. */
export type StyleValues = Record<string, unknown>;

/** A style of a list: bundled (read only; its name in English and Japanese) or the project's own (made from `base`); a file that cannot be read says why. */
export interface StyleListItem {
  id: string;
  name: ItemName;
  base: string | null;
  bundled: boolean;
  /** The project's maps that use the style. */
  maps: string[];
  problem: string | null;
}

export interface StyleListDto {
  /** The project's styles folder (null: none yet). */
  folder: string | null;
  styles: StyleListItem[];
  /** The game's zones' names by code, from the project's game files (empty before they are read). */
  zoneNames: Record<string, Names>;
}

/** A style's values; a project style's also the values it changed and its base's values. */
export interface StyleDto extends StyleListItem {
  values: StyleValues;
  changes: StyleValues;
  baseValues: StyleValues | null;
}

/** The style editor's sample land: made (ready), being made (its progress 0 to 1), not made, or failed (why). */
export interface SampleStatus {
  state: "none" | "making" | "ready" | "failed";
  progress: number;
  message: string | null;
  /** The land's frame: west, north, east, south (m). */
  frame: [number, number, number, number];
  /** The place first shown (its middle, m). */
  place: [number, number];
  /** The windows' sides the preview offers (m). */
  sizes: number[];
}

/** What the style editor's preview draws: the sample land, or the open project's data. */
export type PreviewSource = "sample" | "project";

/** A place of the preview on the project's data: its name, its middle (m), whether a block with its data is there. */
export interface PreviewPlace {
  name: string;
  x: number;
  y: number;
  ready: boolean;
}

/** The preview on the open project's data (GET /api/project/styles/preview). */
export interface ProjectPreviewStatus {
  state: "none" | "ready";
  /** What the project still lacks (while none). */
  waiting: string | null;
  /** The frame of the blocks it can draw: west, north, east, south (m); empty while none. */
  frame: number[];
  /** A character per block of the project's frame (map), row by row from its north-west corner: 1 drawn, 0 not (empty while none). */
  blocks: string;
  /** The project's map frame the blocks are laid out on. */
  map: MapFrame;
  /** The places the preview recommends, and the project's own. */
  recommended: PreviewPlace[];
  places: PreviewPlace[];
  /** The windows' sides (m). */
  sizes: number[];
}

/** A window drawn (POST /api/project/styles/preview): the id of its z8 tiles, its edges (west, north, east, south, m), the seconds it took. */
export interface StylePreviewDrawing {
  id: string;
  bounds: [number, number, number, number];
  seconds: number;
}

/**
 * A step of the drawing that paints a point (from the top): its kind, the colour it paints, the part of the pixel it
 * covers, the shading's factor, the road class (0 road, 1 highway), the paint (a building colour, a ground paint), the
 * sea band and bed, a label's text, and for a building the rule its colour came from (zone, region with the region, default).
 */
export interface StylePickStep {
  kind: string;
  color: string | null;
  cover: number;
  factor: number;
  class: number;
  paint: string | null;
  band: number;
  bed: string | null;
  text: string | null;
  rule: string | null;
  region: string | null;
}

/** A part of the ground picture at a point: a kind of ground or a region (its share), a tone or the trees (its weight). */
export interface StylePickPart {
  kind: "ground" | "region" | "tone" | "trees";
  id: string;
  color: string | null;
  share: number;
}

/** Where the colour of a point comes from (POST /api/project/styles/preview/pick). */
export interface StylePick {
  color: string;
  steps: StylePickStep[];
  point: { zone: string; zoneEn: string | null; zoneJa: string | null; ground: string; water: boolean; building: boolean };
  ground: StylePickPart[];
}

// ---------------------------------------------------------------- points of interest (the POI screen)

/** A point's label: the text the maps draw, by language (en, ja; the English one where a language has none). */
export type PoiLabel = Record<string, string>;

/** Which maps a point shows on (null in a file: taken from its group and folders). */
export interface PoiShow {
  atlas: boolean;
  roadmap: boolean;
}

/** How a point is drawn: its label as text, its label in a circle, a circle, an icon. */
export type PoiLook = "text" | "badge" | "dot" | "icon";

/** A folder of the POI folder ("" is the POI folder itself); a null name is the folder's own. */
export interface PoiEditFolder {
  path: string;
  name: ItemName | null;
  order: number;
  style: string | null;
  show: PoiShow | null;
  visible: boolean;
  locked: boolean;
}

/** A point: its name (the lists'; "" none), its label (the maps' text); null values come from its group and folders, or its style. */
export interface PoiEditPoint {
  id: string;
  name: string;
  label: PoiLabel;
  x: number;
  y: number;
  style: string | null;
  show: PoiShow | null;
  color: string | null;
  size: number | null;
  visible: boolean | null;
  locked: boolean | null;
}

/** A group of points: one file of the POI folder (its path without .json); a null name is the file's own. */
export interface PoiEditGroup {
  path: string;
  points: PoiEditPoint[];
  name: ItemName | null;
  order: number;
  style: string | null;
  show: PoiShow | null;
  visible: boolean;
  locked: boolean;
  credit: string | null;
}

export interface PoiEditStyle {
  id: string;
  name: ItemName;
  look: PoiLook;
  color: string;
  size: number;
  weight: "normal" | "bold";
  outline: string | null;
  outlineWidth: number;
  badgeColor: string | null;
  showLabel: boolean;
  icon: string | null;
  image: string | null;
  labelSize: number | null;
}

/** What the POI screen edits: the folders, the groups with their points, the project's own POI styles. */
export interface PoiEditSet {
  folders: PoiEditFolder[];
  groups: PoiEditGroup[];
  styles: PoiEditStyle[];
}

/** GET /api/project/poi. */
export interface PoiEditorDto {
  folder: string | null;
  stylesFile: string | null;
  set: PoiEditSet | null;
  bundledStyles: PoiEditStyle[];
  bundledGroups: string[];
  fonts: Record<string, string>;
  background: string;
  problem: string | null;
}

/** The MDI icons (GET /api/poi/icons). */
export interface PoiIcons {
  format: number;
  version: string;
  icons: Record<string, { path: string; aliases: string[]; tags: string[] }>;
}

/** Why a line of a file is left out (a code the screen words; some carry a value). */
export type PoiImportReason = { code: "x" | "y" | "offMap" | "color" | "size" | "empty" | "columns" | "json" | "list" | "point"; value: string | null };

/** Points read from a CSV or JSON file (POST /api/project/poi/import). */
export interface PoiImportDto {
  points: PoiEditPoint[];
  skipped: { line: number; reasons: PoiImportReason[] }[];
  name: string;
}
