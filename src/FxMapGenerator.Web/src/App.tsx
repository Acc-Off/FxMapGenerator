import { useEffect } from "react";
import { GuideLayer } from "./guide/GuideLayer";
import { api, subscribeEvents } from "./shared/api";
import { AppSettingsDialog } from "./shared/AppSettingsDialog";
import { HelpLayer } from "./shared/controls";
import { useI18nStore, useT } from "./shared/i18n";
import { applyTheme, useAppStore } from "./shared/store";
import { ProjectScreen } from "./project/ProjectScreen";
import { useProjectStore } from "./project/store";
import { StartScreen } from "./start/StartScreen";

// Until the server's language setting arrives, follow the browser.
useI18nStore.getState().setLanguage("auto");

export function App() {
  const connected = useAppStore((s) => s.connected);
  const quit = useAppStore((s) => s.quit);
  const project = useProjectStore((s) => s.project);
  const settingsOpen = useAppStore((s) => s.settingsOpen);
  const t = useT();

  // Initial data, then live updates over SSE (every tab shows the same project and run).
  useEffect(() => {
    const { loadSettings, loadStatus, loadDiagnostics, setStatus, setConnected } = useAppStore.getState();
    const projects = useProjectStore.getState();
    void loadSettings().catch(() => undefined);
    void loadStatus().catch(() => undefined);
    void loadDiagnostics().catch(() => undefined);
    void api.project().then((p) => projects.setProject(p), () => undefined);
    void api.job().then((j) => j && projects.onJob(j), () => undefined);
    return subscribeEvents({
      status: setStatus,
      settings: (s) => {
        applyTheme(s.theme);
        useI18nStore.getState().setLanguage(s.language);
        useAppStore.setState({ settings: s });
      },
      project: (p) => useProjectStore.getState().setProject(p),
      job: (j) => useProjectStore.getState().onJob(j),
      jobLog: (l) => useProjectStore.getState().onLog(l),
      precheck: (r) => useProjectStore.getState().setPrecheck(r),
      gameUse: (u) => useProjectStore.setState({ gameUse: u.kind }),
      connection: (ok) => {
        setConnected(ok);
        if (ok) void loadStatus().catch(() => undefined);
      },
    });
  }, []);

  return (
    <>
      {quit ? (
        <div className="banner">{t("footer.quit.done")}</div>
      ) : (
        connected === false && (
          <div className="banner banner-error">
            <strong>{t("status.disconnected.title")}</strong> {t("status.disconnected.body")}
          </div>
        )
      )}
      {project ? <ProjectScreen /> : <StartScreen />}
      {settingsOpen && (
        <AppSettingsDialog
          onClose={() => {
            useAppStore.getState().setSettingsOpen(false);
            // the project's checks read the app's GTA V and key folders
            if (useProjectStore.getState().project) void useProjectStore.getState().refresh();
          }}
        />
      )}
      <HelpLayer />
      <GuideLayer />
    </>
  );
}
