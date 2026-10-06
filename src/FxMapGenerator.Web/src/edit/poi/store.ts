import { create } from "zustand";
import { api, ApiError } from "../../shared/api";
import { errorText, t, useI18nStore } from "../../shared/i18n";
import { makesJapanese, type PoiEditorDto, type PoiEditSet, type PoiIcons } from "../../shared/types";
import { useProjectStore } from "../../project/store";
import { remap, remapKey, sameSet } from "./model";

/**
 * The language of the labels the screen shows: the one chosen when the project makes Japanese maps, else English (the
 * only language of its maps).
 */
export function useLabelLang(): "en" | "ja" {
  const japanese = useProjectStore((s) => (s.project ? makesJapanese(s.project.file) : false));
  const lang = usePoiEditor((s) => s.labelLang);
  return japanese ? lang : "en";
}

/** Tools of the map: place a point, move points, select (click, Shift adds), select in an area (a rectangle; Ctrl: a traced outline). */
export type PoiTool = "place" | "move" | "select" | "box";
/** The tools in the panel's order; the keys 1 to 4 choose them. */
export const POI_TOOLS: readonly PoiTool[] = ["place", "move", "select", "box"];

/** What is selected: folders, groups (paths) and points (keys). */
export interface PoiSelection {
  folders: string[];
  groups: string[];
  points: string[];
}

export const NOTHING: PoiSelection = { folders: [], groups: [], points: [] };
/** Undo steps kept. */
const UNDO = 200;

interface PoiEditorState {
  project: string | null;
  dto: PoiEditorDto | null;
  loading: boolean;
  error: string | null;
  notice: string | null;
  /** The set as saved, and as edited. */
  saved: PoiEditSet | null;
  set: PoiEditSet | null;
  past: PoiEditSet[];
  future: PoiEditSet[];
  selection: PoiSelection;
  tool: PoiTool;
  /** The left side: the folders' tree or the POI styles. */
  side: "points" | "styles";
  /** The POI style shown on the styles side. */
  styleId: string | null;
  /** The folders opened in the tree (kept while the app is open). */
  open: Set<string>;
  icons: PoiIcons | null;
  saving: boolean;
  /** The language of the labels the screen shows (the map, the samples, the lists) when the project makes Japanese maps. */
  labelLang: "en" | "ja";

  load(project: string): Promise<void>;
  loadIcons(): Promise<void>;
  /** A change of the set (one undo step), with the selection after it. */
  change(next: PoiEditSet, selection?: PoiSelection): void;
  /** A folder or group moved from one path to another: the selection and the opened folders follow it. */
  moved(from: string, to: string): void;
  undo(): void;
  redo(): void;
  select(selection: PoiSelection): void;
  setTool(tool: PoiTool): void;
  setSide(side: "points" | "styles"): void;
  setStyleId(id: string | null): void;
  setLabelLang(lang: "en" | "ja"): void;
  toggle(folder: string, open?: boolean): void;
  save(): Promise<boolean>;
  discard(): void;
  dirty(): boolean;
  clearError(): void;
  setNotice(text: string | null): void;
}

function message(e: unknown): string {
  return e instanceof ApiError ? errorText(e.code, e.message) : e instanceof Error ? e.message : String(e);
}

export const usePoiEditor = create<PoiEditorState>((set, get) => ({
  project: null,
  dto: null,
  loading: false,
  error: null,
  notice: null,
  saved: null,
  set: null,
  past: [],
  future: [],
  selection: NOTHING,
  tool: "select",
  side: "points",
  styleId: null,
  open: new Set([""]),
  icons: null,
  saving: false,
  labelLang: useI18nStore.getState().lang,

  async load(project) {
    if (get().project !== project)
      set({ project, dto: null, saved: null, set: null, past: [], future: [], selection: NOTHING, error: null, notice: null, styleId: null, open: new Set([""]) });
    set({ loading: true });
    try {
      const dto = await api.poi();
      if (get().project !== project) return;
      const keep = get().dirty() && get().dto !== null;
      set({ dto, saved: dto.set, set: keep ? get().set : dto.set, loading: false });
    } catch (e) {
      set({ loading: false, error: message(e) });
    }
  },

  async loadIcons() {
    if (get().icons) return;
    try {
      const icons = await api.poiIcons();
      set({ icons });
    } catch (e) {
      set({ error: message(e) });
    }
  },

  change(next, selection) {
    const { set: cur, past } = get();
    if (!cur || sameSet(cur, next)) {
      if (selection) set({ selection });
      return;
    }
    set({ set: next, past: [...past.slice(-(UNDO - 1)), cur], future: [], ...(selection ? { selection } : {}) });
  },

  moved(from, to) {
    const { selection, open } = get();
    set({
      selection: {
        folders: selection.folders.map((p) => remap(p, from, to)),
        groups: selection.groups.map((p) => remap(p, from, to)),
        points: selection.points.map((k) => remapKey(k, from, to)),
      },
      open: new Set([...open].map((p) => remap(p, from, to))),
    });
  },

  undo() {
    const { past, set: cur, future } = get();
    if (!cur || past.length === 0) return;
    set({ set: past[past.length - 1], past: past.slice(0, -1), future: [cur, ...future], selection: NOTHING });
  },

  redo() {
    const { past, set: cur, future } = get();
    if (!cur || future.length === 0) return;
    set({ set: future[0], past: [...past, cur], future: future.slice(1), selection: NOTHING });
  },

  select(selection) {
    set({ selection });
  },

  setTool(tool) {
    set({ tool });
  },

  setSide(side) {
    set({ side });
  },

  setStyleId(id) {
    set({ styleId: id });
  },

  setLabelLang(lang) {
    set({ labelLang: lang });
  },

  toggle(folder, open) {
    const next = new Set(get().open);
    const on = open ?? !next.has(folder);
    if (on) next.add(folder);
    else next.delete(folder);
    set({ open: next });
  },

  async save() {
    const cur = get().set;
    if (!cur) return false;
    set({ saving: true });
    try {
      const dto = await api.savePoi(cur, useI18nStore.getState().lang);
      set({ dto, saved: dto.set, set: dto.set ?? cur, saving: false, error: null });
      return true;
    } catch (e) {
      const text = e instanceof ApiError && e.code === "INVALID" ? t("poi.save.invalid", { message: e.message }) : message(e);
      set({ saving: false, error: text });
      return false;
    }
  },

  discard() {
    set({ set: get().saved, past: [], future: [], selection: NOTHING });
  },

  dirty() {
    const { set: cur, saved } = get();
    return cur !== null && !sameSet(cur, saved);
  },

  clearError() {
    set({ error: null });
  },

  setNotice(text) {
    set({ notice: text });
  },
}));
