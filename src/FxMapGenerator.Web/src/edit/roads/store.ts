import { create } from "zustand";
import { api, ApiError } from "../../shared/api";
import { errorText, t, useI18nStore } from "../../shared/i18n";
import type { RoadEditorDto, RoadEditsFile } from "../../shared/types";
import { ALL_SHOWN, applyEdits, type Base, emptyEdits, makeBase, nextAdded, sameEdits, type Shown, shownOnly, takeIn, type View } from "./model";

/**
 * Tools of the left map: draw nodes and links, move nodes and links, select (click, Shift adds), select in an area (a
 * rectangle; Ctrl: a traced outline), select a road up to its branches (Ctrl: its whole street).
 */
export type RoadTool = "draw" | "move" | "select" | "box" | "road";
/** The tools in the panel's order (drawing and moving first, then the ways of selecting); the keys 1 to 5 choose them. */
export const TOOLS: readonly RoadTool[] = ["draw", "move", "select", "box", "road"];

export interface RoadSelection {
  nodes: string[];
  links: string[];
}

const EMPTY: RoadSelection = { nodes: [], links: [] };
/** Undo steps kept. */
const UNDO = 200;

interface RoadEditorState {
  /** The project the data belongs to. */
  project: string | null;
  status: RoadEditorDto | null;
  base: Base | null;
  loading: boolean;
  error: string | null;
  /** The edits as saved (the file's contents, or none). */
  saved: RoadEditsFile;
  edits: RoadEditsFile;
  view: View | null;
  past: RoadEditsFile[];
  future: RoadEditsFile[];
  /** The next number of an added node in this session (numbers are not used twice). */
  counter: number;
  selection: RoadSelection;
  tool: RoadTool;
  /** The last node of the chain being drawn. */
  chain: string | null;
  /** The kinds the left map shows (the legend's check boxes; kept while the app is open, not saved). */
  shown: Shown;
  saving: boolean;
  /** The bundled edits (read once), or null before they are read. */
  bundled: RoadEditsFile | null;
  /** The game files are being read (the button). */
  reading: boolean;
  /** What the last reading of the game files found, or the server's message when it failed; null before one. */
  gameFiles: { changed: boolean; nodes: number; seconds: number } | { failed: string } | null;

  load(project: string): Promise<void>;
  /** Reads the game files now (before a run has, or again after the game or a server's road data changed), then the status again. */
  readGameFiles(): Promise<void>;
  /** Reads the status again (after a run or a save elsewhere); the path data again when it changed. */
  refresh(): Promise<void>;
  /** A change of the edits (one undo step). */
  change(next: RoadEditsFile, selection?: RoadSelection): void;
  /** Takes groups of the bundled edits in (one undo step), numbering the added nodes on from this session's. */
  takeIn(ids: string[]): void;
  undo(): void;
  redo(): void;
  select(selection: RoadSelection): void;
  setTool(tool: RoadTool): void;
  setChain(key: string | null): void;
  /** Shows or leaves out kinds on the left map; what it leaves out leaves the selection too. */
  setShown(shown: Shown): void;
  takeNumber(): number;
  save(): Promise<boolean>;
  clearError(): void;
  dirty(): boolean;
}

function message(e: unknown): string {
  return e instanceof ApiError ? errorText(e.code, e.message) : e instanceof Error ? e.message : String(e);
}

export const useRoadEditor = create<RoadEditorState>((set, get) => ({
  project: null,
  status: null,
  base: null,
  loading: false,
  error: null,
  saved: emptyEdits(),
  edits: emptyEdits(),
  view: null,
  past: [],
  future: [],
  counter: 1,
  selection: EMPTY,
  tool: "select",
  chain: null,
  shown: ALL_SHOWN,
  saving: false,
  bundled: null,
  reading: false,
  gameFiles: null,

  async readGameFiles() {
    const project = get().project;
    if (!project || get().reading) return;
    set({ reading: true, gameFiles: null });
    try {
      const r = await api.roadGameFiles();
      if (get().project !== project) return;
      set({ gameFiles: { changed: r.changed, nodes: r.nodes, seconds: r.seconds } });
      await get().load(project);
    } catch (e) {
      if (get().project === project) set({ gameFiles: { failed: e instanceof ApiError && e.code === "LOCKED" ? errorText(e.code, e.message) : e instanceof Error ? e.message : String(e) } });
    } finally {
      set({ reading: false });
    }
  },

  async load(project) {
    if (get().project !== project)
      set({ project, status: null, base: null, saved: emptyEdits(), edits: emptyEdits(), view: null, past: [], future: [], counter: 1, selection: EMPTY, chain: null, error: null, gameFiles: null });
    set({ loading: true });
    if (!get().bundled) void api.roadBundled().then((bundled) => set({ bundled }), () => undefined);
    try {
      const status = await api.roadEditor();
      if (get().project !== project) return;
      let base = get().base;
      if (!status.unavailable && status.pathsVersion && base?.version !== status.pathsVersion) {
        const dto = await api.roadPaths();
        if (get().project !== project) return;
        base = makeBase(dto);
      }
      const keep = get().dirty() && get().status !== null;
      const saved = status.edits ?? emptyEdits();
      const edits = keep ? get().edits : saved;
      set({
        status,
        base: status.unavailable ? null : base,
        saved,
        edits,
        view: base && !status.unavailable ? applyEdits(base, edits) : null,
        counter: nextAdded(edits, get().counter),
        loading: false,
      });
    } catch (e) {
      set({ loading: false, error: message(e) });
    }
  },

  async refresh() {
    const p = get().project;
    if (p) await get().load(p);
  },

  change(next, selection) {
    const { edits, past, base } = get();
    if (!base) return;
    set({
      edits: next,
      view: applyEdits(base, next),
      past: [...past.slice(-(UNDO - 1)), edits],
      future: [],
      counter: nextAdded(next, get().counter),
      ...(selection ? { selection } : {}),
    });
  },

  takeIn(ids) {
    const { base, edits, bundled, counter } = get();
    if (!base || !bundled || ids.length === 0) return;
    // the groups taken in keep their names in the screen's language
    get().change(takeIn(base, edits, bundled, ids, counter, useI18nStore.getState().lang).edits, EMPTY);
  },

  undo() {
    const { past, edits, future, base } = get();
    if (!base || past.length === 0) return;
    const prev = past[past.length - 1];
    set({ edits: prev, view: applyEdits(base, prev), past: past.slice(0, -1), future: [edits, ...future], chain: null });
  },

  redo() {
    const { past, edits, future, base } = get();
    if (!base || future.length === 0) return;
    const next = future[0];
    set({ edits: next, view: applyEdits(base, next), past: [...past, edits], future: future.slice(1), chain: null });
  },

  select(selection) {
    set({ selection });
  },

  setTool(tool) {
    set({ tool, chain: null });
  },

  setChain(key) {
    set({ chain: key });
  },

  setShown(shown) {
    const { base, view, selection } = get();
    set({ shown, selection: base && view ? shownOnly(base, view, selection, shown) : selection });
  },

  takeNumber() {
    const n = get().counter;
    set({ counter: n + 1 });
    return n;
  },

  async save() {
    const { edits } = get();
    set({ saving: true });
    try {
      const status = await api.saveRoadEdits(edits);
      const saved = status.edits ?? emptyEdits();
      set({ status, saved, saving: false, error: null });
      return true;
    } catch (e) {
      // edits the server does not take: the screen made them (a fault of its own), so tell what to do and why
      const text = e instanceof ApiError && e.code === "INVALID" ? t("roads.save.invalid", { message: e.message }) : message(e);
      set({ saving: false, error: text });
      return false;
    }
  },

  clearError() {
    set({ error: null });
  },

  dirty() {
    const { edits, saved } = get();
    return !sameEdits(edits, saved);
  },
}));
