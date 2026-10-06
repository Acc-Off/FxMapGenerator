import { create } from "zustand";
import { api, ApiError } from "../../shared/api";
import { errorText, t, useI18nStore } from "../../shared/i18n";
import type { StyleDto, StyleItem, StyleListDto, StyleListItem, StyleSchema, StyleValues } from "../../shared/types";
import { revertedRow, sameJson } from "./schema";

/** The style chosen last in each project (kept while the app is open). */
const lastChosen = new Map<string, string>();
/** Undo steps kept. */
const UNDO = 200;
/** Changes of one value this close together are one undo step (a slider dragged, a colour picked, a name typed). */
const MERGE_MS = 1000;

interface StyleEditorState {
  /** The project the styles belong to. */
  project: string | null;
  schema: StyleSchema | null;
  list: StyleListDto | null;
  /** The shared styles, once read (the window that makes a style reads them). */
  shared: StyleListItem[] | null;
  /** The font families of this PC, once read (the font rows read them). */
  fonts: string[] | null;
  /** The style shown as saved (its values), or null while it is read. */
  current: StyleDto | null;
  /** The values being edited (null: the saved ones). */
  edited: StyleValues | null;
  past: StyleValues[];
  future: StyleValues[];
  /** The last change: the value it changed and when (quick changes of one value merge into one undo step). */
  last: { key: string; at: number } | null;
  loading: boolean;
  saving: boolean;
  error: string | null;
  /** A short message after an operation (made, saved into the shared styles, ...). */
  notice: string | null;

  load(project: string): Promise<void>;
  /** Shows another style (unsaved edits of the one shown are dropped: ask first). */
  choose(id: string): Promise<void>;
  loadShared(): Promise<void>;
  loadFonts(): Promise<void>;
  /** The values shown: the edits, else the saved values. */
  values(): StyleValues | null;
  /** A change of the values (one undo step, or merged into the last one when it changed the same value just before). */
  change(next: StyleValues, key: string): void;
  /** A row back to the base style's values (one undo step). */
  revert(item: StyleItem): void;
  undo(): void;
  redo(): void;
  dirty(): boolean;
  /** Drops the edits: the saved values come back, the undo list goes. */
  discard(): void;
  save(): Promise<boolean>;
  /** A new project style from another; ID_TAKEN when the id is taken (nothing made). */
  create(from: string, shared: boolean, id: string, name: string): Promise<"ok" | "ID_TAKEN" | "error">;
  rename(name: string): Promise<boolean>;
  remove(): Promise<boolean>;
  /** A style file taken in (under `id` when given); ID_TAKEN when its id is taken (nothing read in). */
  importFile(text: string, id?: string): Promise<"ok" | "ID_TAKEN" | "error">;
  /** Copies the style into the shared styles; EXISTS when a shared style has its id and `overwrite` is not set. */
  share(overwrite: boolean): Promise<"ok" | "EXISTS" | "error">;
  setNotice(notice: string | null): void;
  clearError(): void;
}

/** An error for the screen; a style with errors says them all (the server names every problem). */
function message(e: unknown): string {
  if (e instanceof ApiError && e.code === "INVALID") return t("styles.invalid", { message: e.message });
  if (e instanceof ApiError && e.code === "LOCKED") return t("styles.save.locked");
  return e instanceof ApiError ? errorText(e.code, e.message) : e instanceof Error ? e.message : String(e);
}

/** No edits: the saved values, an empty undo list. */
const fresh = (): Pick<StyleEditorState, "edited" | "past" | "future" | "last"> => ({ edited: null, past: [], future: [], last: null });

export const useStyleEditor = create<StyleEditorState>((set, get) => ({
  project: null,
  schema: null,
  list: null,
  shared: null,
  fonts: null,
  current: null,
  ...fresh(),
  loading: false,
  saving: false,
  error: null,
  notice: null,

  async load(project) {
    if (get().project !== project) set({ project, list: null, current: null, ...fresh(), shared: null, error: null, notice: null });
    set({ loading: true });
    try {
      const [schema, list] = await Promise.all([get().schema ? Promise.resolve(get().schema!) : api.styleSchema(), api.styles()]);
      if (get().project !== project) return;
      set({ schema, list, loading: false });
      // edits not saved stay while the app is open (the tab left and opened again)
      const cur = get().current;
      if (cur && get().dirty() && list.styles.some((s) => s.id === cur.id)) return;
      const want = cur?.id ?? lastChosen.get(project);
      const id = list.styles.some((s) => s.id === want) ? want! : list.styles[0].id;
      await get().choose(id);
    } catch (e) {
      set({ loading: false, error: message(e) });
    }
  },

  async choose(id) {
    const project = get().project;
    if (!project) return;
    lastChosen.set(project, id);
    const item = get().list?.styles.find((s) => s.id === id);
    if (item?.problem) {
      // a file that cannot be read: its list entry, without values
      set({ current: { ...item, values: {}, changes: {}, baseValues: null }, ...fresh() });
      return;
    }
    try {
      const current = await api.style(id);
      if (get().project === project) set({ current, ...fresh(), error: null });
    } catch (e) {
      set({ error: message(e) });
    }
  },

  async loadShared() {
    try {
      set({ shared: await api.sharedStyles() });
    } catch (e) {
      set({ shared: [], error: message(e) });
    }
  },

  async loadFonts() {
    if (get().fonts) return;
    try {
      set({ fonts: await api.fonts() });
    } catch {
      set({ fonts: [] });
    }
  },

  values() {
    return get().edited ?? get().current?.values ?? null;
  },

  change(next, key) {
    const { current, past, last } = get();
    const before = get().values();
    if (!current || current.bundled || !before || sameJson(next, before)) return;
    const now = Date.now();
    const merge = last !== null && last.key === key && now - last.at < MERGE_MS && past.length > 0;
    set({ edited: next, past: merge ? past : [...past.slice(-(UNDO - 1)), before], future: [], last: { key, at: now } });
  },

  revert(item) {
    const { current } = get();
    const values = get().values();
    if (!current?.baseValues || !values) return;
    get().change(revertedRow(item, values, current.baseValues), `revert:${item.key}:${Date.now()}`);
    set({ last: null });
  },

  undo() {
    const { past, future } = get();
    const values = get().values();
    if (past.length === 0 || !values) return;
    set({ edited: past[past.length - 1], past: past.slice(0, -1), future: [values, ...future], last: null });
  },

  redo() {
    const { past, future } = get();
    const values = get().values();
    if (future.length === 0 || !values) return;
    set({ edited: future[0], past: [...past, values], future: future.slice(1), last: null });
  },

  dirty() {
    const { edited, current } = get();
    return edited !== null && current !== null && !sameJson(edited, current.values);
  },

  discard() {
    set({ ...fresh() });
  },

  async save() {
    const { current } = get();
    const values = get().values();
    if (!current || current.bundled || !values) return false;
    set({ saving: true });
    try {
      const saved = await api.saveStyle(current.id, values);
      // the undo list stays: undo goes back to the values before the save (which are then unsaved)
      set({ current: saved, edited: null, saving: false, error: null, last: null });
      return true;
    } catch (e) {
      set({ saving: false, error: message(e) });
      return false;
    }
  },

  async create(from, shared, id, name) {
    try {
      const made = await api.createStyle(from, shared, id, name);
      set({ list: await api.styles(), current: made, ...fresh(), error: null });
      if (get().project) lastChosen.set(get().project!, made.id);
      return "ok";
    } catch (e) {
      if (e instanceof ApiError && e.code === "ID_TAKEN") return "ID_TAKEN";
      set({ error: message(e) });
      return "error";
    }
  },

  async rename(name) {
    const id = get().current?.id;
    if (!id) return false;
    try {
      const current = await api.renameStyle(id, name);
      set({ list: await api.styles(), current, ...fresh(), error: null });
      return true;
    } catch (e) {
      set({ error: message(e) });
      return false;
    }
  },

  async remove() {
    const id = get().current?.id;
    if (!id) return false;
    try {
      const list = await api.deleteStyle(id);
      set({ list, current: null, ...fresh(), error: null });
      await get().choose(list.styles[0].id);
      return true;
    } catch (e) {
      set({ error: message(e) });
      return false;
    }
  },

  async importFile(text, id) {
    try {
      const made = await api.importStyle(text, useI18nStore.getState().lang, id);
      set({ list: await api.styles(), current: made, ...fresh(), error: null });
      if (get().project) lastChosen.set(get().project!, made.id);
      return "ok";
    } catch (e) {
      if (e instanceof ApiError && e.code === "ID_TAKEN") return "ID_TAKEN";
      set({ error: message(e) });
      return "error";
    }
  },

  async share(overwrite) {
    const id = get().current?.id;
    if (!id) return "error";
    try {
      set({ shared: await api.shareStyle(id, overwrite), error: null });
      return "ok";
    } catch (e) {
      if (e instanceof ApiError && e.code === "EXISTS") return "EXISTS";
      set({ error: message(e) });
      return "error";
    }
  },

  setNotice(notice) {
    set({ notice });
  },

  clearError() {
    set({ error: null });
  },
}));
