import { useEffect, useState, type CSSProperties } from "react";
import { api } from "../shared/api";
import { Button, Info, Splitter, type HelpKey } from "../shared/controls";
import { useT, type MessageKey } from "../shared/i18n";
import { useAppStore } from "../shared/store";
import { EditScreen, mayLeaveEditing } from "../edit/EditScreen";
import { ExportScreen } from "../export/ExportScreen";
import { GuideButton } from "../guide/GuideLayer";
import { PrecheckScreen } from "../precheck/PrecheckScreen";
import { ProjectSettingsScreen } from "../settings/ProjectSettingsScreen";
import { ViewScreen } from "../view/ViewScreen";
import { MapView } from "./MapView";
import { failing, PrereqSummary } from "./PrereqSummary";
import { RunStatus } from "./RunStatus";
import { SidePanel } from "./SidePanel";
import { setLeaveCheck, useProjectStore, type Screen } from "./store";

// leaving the editing tab asks about its unsaved edits
setLeaveCheck((from) => from !== "edit" || mayLeaveEditing());
import { TodoPanel } from "./TodoPanel";

const SCREENS: readonly (readonly [Screen, MessageKey, HelpKey])[] = [
  ["settings", "screen.settings", "help.screen.settings"],
  ["project", "screen.project", "help.screen.project"],
  ["precheck", "screen.precheck", "help.screen.precheck"],
  ["edit", "screen.edit", "help.screen.edit"],
  ["view", "screen.view", "help.screen.view"],
  ["export", "screen.export", "help.screen.export"],
];

/** The right-hand panel's width (App/Services/AppSettings.cs): the default, and the least left to the side panel and the map. */
const JOB_LIST_WIDTH = 440;
const JOB_LIST_MIN = 320;
const JOB_LIST_MAX = 1200;
const SIDE_AND_MAP_MIN = 250 + 360;

function fitWidth(width: number): number {
  return Math.round(Math.max(JOB_LIST_MIN, Math.min(JOB_LIST_MAX, window.innerWidth - SIDE_AND_MAP_MIN, width)));
}

/**
 * An open project: the header with the screens (project settings, plan & progress, FiveM setup & check, map view,
 * export; a screen with prerequisites not met shows how many) and the app's settings, and the screen shown. The project
 * screen: left the maps to output, the map with the range tools in the middle and the run status under it,
 * the job list and the prerequisites not met on the right. Planning and running happen on the same screen.
 */
export function ProjectScreen() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const error = useProjectStore((s) => s.error);
  const screen = useProjectStore((s) => s.screen);
  const checks = useProjectStore((s) => s.checks);
  const { clearError, setScreen } = useProjectStore.getState();
  const settings = useAppStore((s) => s.settings);
  const saveSettings = useAppStore((s) => s.saveSettings);
  const setSettingsOpen = useAppStore((s) => s.setSettingsOpen);
  // the width while the splitter is dragged (and until the setting is saved); else the saved one, fitted to the window
  const [dragWidth, setDragWidth] = useState<number | null>(null);
  const [, setWindowWidth] = useState(window.innerWidth);
  useEffect(() => {
    const resized = () => setWindowWidth(window.innerWidth);
    window.addEventListener("resize", resized);
    return () => window.removeEventListener("resize", resized);
  }, []);
  const jobListWidth = fitWidth(dragWidth ?? settings?.jobListWidth ?? JOB_LIST_WIDTH);
  const keepWidth = (width: number) => {
    const w = fitWidth(width);
    const current = useAppStore.getState().settings;
    if (!current || current.jobListWidth === w) {
      setDragWidth(null);
      return;
    }
    setDragWidth(w);
    void saveSettings({ ...current, jobListWidth: w }).finally(() => setDragWidth(null));
  };

  return (
    <div className="project">
      <header className="project-header">
        <Button help="help.project.close" onClick={() => (screen !== "edit" || mayLeaveEditing()) && void api.closeProject()}>
          {t("project.close")}
        </Button>
        <Info help="help.project.name" className="project-name">
          {project.name}
        </Info>
        <nav className="tabs" data-guide="project.tabs">
          {SCREENS.map(([id, label, help]) => {
            const n = failing(checks, id);
            return (
              <Button key={id} help={help} className="tab" pressed={screen === id} onClick={() => setScreen(id)}>
                {t(label)}
                {n > 0 && <span className="tab-count">✗ {n}</span>}
              </Button>
            );
          })}
        </nav>
        <Info help="help.project.path" className="path muted">
          {project.path}
        </Info>
        <span className="spacer" />
        <GuideButton />
        <Button help="help.appSettings.open" onClick={() => setSettingsOpen(true)}>
          {t("appSettings.open")}
        </Button>
      </header>
      {error && (
        <div className="banner banner-error project-error">
          {error}
          <Button help="help.project.error.close" variant="link" onClick={clearError}>
            {t("common.close")}
          </Button>
        </div>
      )}
      {screen === "settings" ? (
        <ProjectSettingsScreen />
      ) : screen === "edit" ? (
        <EditScreen />
      ) : screen === "precheck" ? (
        <PrecheckScreen />
      ) : screen === "view" ? (
        <ViewScreen />
      ) : screen === "export" ? (
        <ExportScreen />
      ) : (
        <div className={`project-body${dragWidth !== null ? " is-resizing" : ""}`} style={{ "--job-list-width": `${jobListWidth}px` } as CSSProperties}>
          <SidePanel />
          <main className="project-center">
            <div className="project-map">
              <MapView />
            </div>
            <RunStatus />
          </main>
          <Splitter help="help.panel.resize" width={jobListWidth} defaultWidth={JOB_LIST_WIDTH} onResize={(w) => setDragWidth(fitWidth(w))} onCommit={keepWidth} />
          <aside className="project-right">
            <div data-guide="project.jobs">
              <Info help="help.todo.title" block className="section-title">
                {t("todo.title")}
              </Info>
              <TodoPanel />
            </div>
            <div data-guide="project.prereq">
              <Info help="help.prereq.title" block className="section-title">
                {t("prereq.title")}
              </Info>
              <PrereqSummary />
            </div>
            <Info help="help.project.work" block className="path muted work-path">
              {t("project.work", { path: project.workFolder })}
            </Info>
          </aside>
        </div>
      )}
    </div>
  );
}
