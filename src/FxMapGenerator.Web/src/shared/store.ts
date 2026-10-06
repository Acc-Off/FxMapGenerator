import { create } from "zustand";
import { api } from "./api";
import { useI18nStore } from "./i18n";
import type { AppSettings, DiagnosticsDto, StatusDto, ThemeSetting } from "./types";

interface AppStore {
  /** null until the event stream has connected once; false after the server went away. */
  connected: boolean | null;
  /** Set after the user quit from the UI, so the page does not report a crash. */
  quit: boolean;
  status: StatusDto | null;
  settings: AppSettings | null;
  diagnostics: DiagnosticsDto | null;
  /** The app's settings window (language, theme, GTA V and keys) is open. */
  settingsOpen: boolean;
  setSettingsOpen(open: boolean): void;
  setConnected(connected: boolean): void;
  setStatus(status: StatusDto): void;
  loadStatus(): Promise<void>;
  loadSettings(): Promise<void>;
  saveSettings(next: AppSettings): Promise<AppSettings>;
  loadDiagnostics(): Promise<void>;
  quitApp(): Promise<void>;
}

export function applyTheme(theme: ThemeSetting): void {
  const root = document.documentElement;
  if (theme === "light" || theme === "dark") root.dataset.theme = theme;
  else delete root.dataset.theme;
}

export const useAppStore = create<AppStore>((set) => ({
  connected: null,
  quit: false,
  status: null,
  settings: null,
  diagnostics: null,
  settingsOpen: false,

  setSettingsOpen: (settingsOpen) => set({ settingsOpen }),
  setConnected: (connected) => set({ connected }),
  setStatus: (status) => set({ status }),

  async loadStatus() {
    set({ status: await api.status() });
  },

  async loadSettings() {
    const settings = await api.settings();
    applyTheme(settings.theme);
    useI18nStore.getState().setLanguage(settings.language);
    set({ settings });
  },

  async saveSettings(next) {
    const saved = await api.saveSettings(next);
    applyTheme(saved.theme);
    useI18nStore.getState().setLanguage(saved.language);
    set({ settings: saved });
    return saved;
  },

  async loadDiagnostics() {
    set({ diagnostics: await api.diagnostics() });
  },

  async quitApp() {
    set({ quit: true });
    await api.quit();
  },
}));
