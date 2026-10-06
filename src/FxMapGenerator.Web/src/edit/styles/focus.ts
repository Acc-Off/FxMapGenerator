import { create } from "zustand";

/** A row of the style list to show: its key, and in a table the row (a zone's code, a band's number) and the column. */
export interface FocusTarget {
  key: string;
  row?: string;
  column?: string;
}

interface FocusState {
  target: FocusTarget | null;
  /** Counts the requests, so the same row asked for again is shown again. */
  n: number;
  show(target: FocusTarget): void;
}

/** Asks the style list to open a row's group, bring the row (or a table's cell) into view and mark it for a moment. */
export const useFocusStore = create<FocusState>((set) => ({
  target: null,
  n: 0,
  show: (target) => set((s) => ({ target, n: s.n + 1 })),
}));

/** How long a row shown stays marked (ms). */
export const FOCUS_MS = 1800;
