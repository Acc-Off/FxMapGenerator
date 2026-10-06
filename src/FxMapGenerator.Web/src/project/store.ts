import { create } from "zustand";
import { api, ApiError } from "../shared/api";
import { samePath } from "../shared/format";
import { errorText } from "../shared/i18n";
import { setCurrentFrame } from "../shared/mapFrame";
import { setOwnStyles } from "./labels";
import type { BlocksDto, CheckDto, GameUse, JobLogLine, JobSnapshot, PlanDto, PrecheckReport, ProjectDto, ProjectEdit, RetakeRequest, StageUnits } from "../shared/types";

/**
 * Map tools: move the map, draw a rectangle to add or exclude blocks, click blocks one by one; draw a rectangle to mark
 * blocks for retake, click blocks to mark or unmark them.
 */
export type Tool = "pan" | "add" | "remove" | "toggle" | "retake" | "retakeToggle";
/** Tools that draw a rectangle (the map's own dragging is off meanwhile). */
export const RECT_TOOLS: ReadonlySet<Tool> = new Set<Tool>(["add", "remove", "retake"]);
/** What lies under the block grid. */
export type Base = "postal" | "satellite" | "none";
/** What the block grid shows: the data held, or what the run did. */
export type MapView = "data" | "run";
/** The screens of an open project. */
export type Screen = "settings" | "edit" | "project" | "precheck" | "view" | "export";

const LOG_LINES = 500;
const UNITS_EVERY_MS = 1000;
/** Finished tiles and the blocks' data while a run goes on (a visit takes about 2.5 s a block). */
const TILES_EVERY_MS = 5000;
const SAVE_WORKERS_MS = 400;

interface ProjectStore {
  project: ProjectDto | null;
  blocks: BlocksDto | null;
  plan: PlanDto | null;
  checks: CheckDto[] | null;
  /** Workers the job list is estimated with and new runs use (the project's default, saved after a pause). */
  workers: number;
  job: JobSnapshot | null;
  units: StageUnits[] | null;
  log: JobLogLine[];
  /** The job-list row whose blocks / cells are outlined on the map. */
  selectedRow: string | null;
  /** The block grid shows the data or the run (switched to "run" when a run of this project starts). */
  view: MapView;
  /** Bumped to reload our own tiles. */
  tileVersion: number;
  tool: Tool;
  base: Base;
  /** A block to move the map to. */
  focus: { name: string; at: number } | null;
  /** The last failed action, shown until the next one. */
  error: string | null;
  screen: Screen;
  /** The latest pre-check of the open project (from the server, and the SSE event precheck). */
  precheck: PrecheckReport | null;
  /** What uses the game by hand right now (the SSE event gameUse): the game takes one of a run, the check, the resource's start at a time. */
  gameUse: GameUse;

  setProject(project: ProjectDto | null): void;
  refresh(): Promise<void>;
  edit(edit: ProjectEdit): Promise<void>;
  /** Marks blocks for retake or takes the mark off (the state of the work folder, not the project file). */
  retake(request: RetakeRequest): Promise<void>;
  setWorkers(workers: number): void;
  start(): Promise<void>;
  stop(mode: "boundary" | "now"): Promise<void>;
  onJob(job: JobSnapshot): void;
  onLog(line: JobLogLine): void;
  select(row: string | null): void;
  setTool(tool: Tool): void;
  setBase(base: Base): void;
  setView(view: MapView): void;
  focusBlock(name: string): void;
  clearError(): void;
  setScreen(screen: Screen): void;
  setPrecheck(report: PrecheckReport | null): void;
}

export const isActive = (job: JobSnapshot | null | undefined) => job?.state === "running" || job?.state === "stopping";

/**
 * The satellite tiles are provisional while the job list still has satellite work (the orthorectification or its lower
 * zooms): a visit orthorectifies each block right after its shot with a provisional scale correction, which the
 * orthorectification step confirms (or replaces) after the visit, and the lower zooms are made whole at the end.
 */
export function satelliteProvisional(plan: PlanDto | null, blocks: BlocksDto | null): boolean {
  if (!plan || !blocks?.ortho.includes("1")) return false;
  return plan.table.rows
    .flatMap((r) => [r, ...r.children])
    .some((r) => (r.id === "ortho" || r.id === "lowZoom.satellite") && r.needed && r.remaining > 0);
}

/** The job belongs to the open project. */
export function jobOfProject(job: JobSnapshot | null, project: ProjectDto | null): boolean {
  return !!job && !!project && samePath(job.project, project.path);
}

/** Asked before the screen changes: false keeps the screen (the editing tab's unsaved edits). */
let leaveCheck: ((from: Screen) => boolean) | null = null;
export function setLeaveCheck(check: ((from: Screen) => boolean) | null) {
  leaveCheck = check;
}

let unitsTimer = 0;
let tilesTimer = 0;
let workersTimer = 0;

function message(e: unknown): string {
  return e instanceof ApiError ? errorText(e.code, e.message) : e instanceof Error ? e.message : String(e);
}

export const useProjectStore = create<ProjectStore>((set, get) => ({
  project: null,
  blocks: null,
  plan: null,
  checks: null,
  workers: 1,
  job: null,
  units: null,
  log: [],
  selectedRow: null,
  view: "data",
  tileVersion: 0,
  tool: "pan",
  base: "postal",
  focus: null,
  error: null,
  screen: "project",
  precheck: null,
  gameUse: null,

  setProject(project) {
    const before = get().project;
    setCurrentFrame(project?.frame);
    if (!project) {
      set({ project: null, blocks: null, plan: null, checks: null, selectedRow: null, view: "data", tool: "pan", screen: "project", precheck: null });
      return;
    }
    const other = !samePath(before?.path, project.path);
    setOwnStyles(project.ownStyles);
    set({
      project,
      // while the slider waits to save, keep what it shows
      ...(workersTimer ? {} : { workers: project.file.parallel }),
      ...(other ? { selectedRow: null, blocks: null, plan: null, checks: null, tool: "pan" as Tool, screen: "project" as Screen, precheck: null, view: jobOfProject(get().job, project) ? ("run" as MapView) : ("data" as MapView) } : {}),
    });
    if (other) void api.precheck().then((r) => set({ precheck: r }), () => undefined);
    void get().refresh();
  },

  async refresh() {
    const { project, workers } = get();
    if (!project) return;
    try {
      // the pre-check with them: a capture that ends stops the resource, and the check made before counts no more
      const [blocks, plan, checks, precheck] = await Promise.all([api.blocks(), api.plan(workers), api.checks(), api.precheck()]);
      if (samePath(get().project?.path, project.path)) set({ blocks, plan, checks, precheck });
    } catch (e) {
      set({ error: message(e) });
    }
  },

  async edit(edit) {
    try {
      const project = await api.editProject(edit);
      set({ error: null });
      get().setProject(project);
    } catch (e) {
      set({ error: message(e) });
    }
  },

  async retake(request) {
    try {
      const project = await api.retake(request);
      set({ error: null });
      get().setProject(project);
    } catch (e) {
      set({ error: message(e) });
    }
  },

  setWorkers(workers) {
    set({ workers });
    // the slider moves in steps: save (and change a running job) once it rests
    window.clearTimeout(workersTimer);
    workersTimer = window.setTimeout(() => {
      workersTimer = 0;
      const { job, project } = get();
      void get().edit({ parallel: workers });
      if (isActive(job) && jobOfProject(job, project))
        void api.setJobWorkers(workers).then((j) => get().onJob(j), (e) => set({ error: message(e) }));
    }, SAVE_WORKERS_MS);
  },

  async start() {
    const { project, workers } = get();
    if (!project) return;
    try {
      const job = await api.startJob(project.path, workers);
      set({ log: [], units: null, view: "run", error: null, selectedRow: null, tool: "pan" });
      get().onJob(job);
    } catch (e) {
      set({ error: message(e) });
    }
  },

  async stop(mode) {
    try {
      get().onJob(await api.stopJob(mode));
    } catch (e) {
      set({ error: message(e) });
    }
  },

  onJob(job) {
    const before = get().job;
    const mine = jobOfProject(job, get().project);
    const started = mine && isActive(job) && before?.runId !== job.runId;
    set({ job, ...(started ? { view: "run" as MapView } : {}) });
    if (!mine) return;
    // the map's colours per unit: at most once a second
    if (!unitsTimer) {
      unitsTimer = window.setTimeout(() => {
        unitsTimer = 0;
        void api.jobUnits().then((units) => set({ units }), () => undefined);
      }, before?.runId === job.runId ? UNITS_EVERY_MS : 0);
    }
    // finished tiles, newly taken data, what the job list has left, the prerequisites (the visit marks the resource as
    // on the server) and the pre-check (a visit that ends stops the resource) show up while it runs, and once more at the end
    if (isActive(job) && !tilesTimer) {
      tilesTimer = window.setTimeout(() => {
        tilesTimer = 0;
        set((s) => ({ tileVersion: s.tileVersion + 1 }));
        const path = get().project?.path;
        void Promise.all([api.blocks(), api.plan(get().workers), api.checks(), api.precheck()]).then(([blocks, plan, checks, precheck]) => {
          if (samePath(get().project?.path, path)) set({ blocks, plan, checks, precheck });
        }, () => undefined);
      }, TILES_EVERY_MS);
    }
    const ended = before?.runId === job.runId && isActive(before) && !isActive(job);
    if (ended || (!before && !isActive(job))) {
      // the last reload happens now; a reload still waiting from the run is not needed any more
      window.clearTimeout(tilesTimer);
      tilesTimer = 0;
      set((s) => ({ tileVersion: s.tileVersion + 1 }));
      void get().refresh();
      void api.jobUnits().then((units) => set({ units }), () => undefined);
    }
  },

  onLog(line) {
    set((s) => {
      const same = s.log.length === 0 || s.log[0].runId === line.runId;
      const log = same ? [...s.log, line] : [line];
      return { log: log.length > LOG_LINES ? log.slice(log.length - LOG_LINES) : log };
    });
  },

  select(row) {
    set((s) => ({ selectedRow: s.selectedRow === row ? null : row }));
  },

  setTool(tool) {
    set({ tool });
  },

  setBase(base) {
    set({ base });
  },

  setView(view) {
    set({ view });
  },

  focusBlock(name) {
    set({ focus: { name, at: Date.now() } });
  },

  clearError() {
    set({ error: null });
  },

  setScreen(screen) {
    if (screen === get().screen || !leaveCheck || leaveCheck(get().screen)) set({ screen, tool: "pan" });
  },

  setPrecheck(report) {
    set({ precheck: report });
    void get().refresh();
  },
}));
