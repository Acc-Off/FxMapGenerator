import { useEffect, useState } from "react";
import { GuideButton } from "../guide/GuideLayer";
import { api, ApiError } from "../shared/api";
import { Button, Info } from "../shared/controls";
import { errorText, useT } from "../shared/i18n";
import { useAppStore } from "../shared/store";
import type { RecentProjectDto } from "../shared/types";
import { isActive, useProjectStore } from "../project/store";
import { NewProjectDialog } from "./NewProjectDialog";

/** The first screen: new / open / recent projects, the app's settings, where things are kept. */
export function StartScreen() {
  const t = useT();
  const status = useAppStore((s) => s.status);
  const settings = useAppStore((s) => s.settings);
  const diagnostics = useAppStore((s) => s.diagnostics);
  const setSettingsOpen = useAppStore((s) => s.setSettingsOpen);
  const quitApp = useAppStore((s) => s.quitApp);
  const job = useProjectStore((s) => s.job);
  const [notices, setNotices] = useState<string | null>(null);
  const [recent, setRecent] = useState<RecentProjectDto[] | null>(null);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // the recent list with names, again whenever the settings (the list) change
  useEffect(() => {
    void api.recentProjects().then(setRecent, () => setRecent([]));
  }, [settings]);

  const openNotices = () => {
    void api.notices().then(setNotices, (e: Error) => setNotices(t("common.failed", { message: e.message })));
  };
  const open = async (path: string) => {
    setError(null);
    try {
      await api.openProject(path);
    } catch (e) {
      setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
    }
  };
  const browse = async () => {
    const r = await api.pickFile(false, settings?.recentProjects[0], t("start.open"));
    if (r.path) await open(r.path);
  };

  return (
    <div className="start">
      <header className="start-header">
        <h1>{t("app.title")}</h1>
        <div className="start-settings">
          <GuideButton />
          <Button help="help.appSettings.open" onClick={() => setSettingsOpen(true)}>
            {t("appSettings.open")}
          </Button>
        </div>
      </header>

      <main className="start-main">
        {isActive(job) && job && (
          <div className="start-job" data-guide="start.job">
            <Info help="help.start.job">{t("start.job", { name: job.projectName })}</Info>
            <Button help="help.start.job.open" onClick={() => void open(job.project)}>
              {t("start.job.open")}
            </Button>
          </div>
        )}
        <div className="start-actions">
          <Button help="help.start.new" variant="primary" onClick={() => setCreating(true)}>
            {t("start.new")}
          </Button>
          <Button help="help.start.open" onClick={() => void browse()}>
            {t("start.open")}
          </Button>
        </div>
        {error && <p className="text-error">{error}</p>}
        <section className="start-recent" data-guide="start.recent">
          <Info help="help.start.recent" block className="section-title">
            {t("start.recent")}
          </Info>
          {recent && recent.length === 0 && <p className="muted">{t("start.recent.empty")}</p>}
          <ul className="recent-list">
            {recent?.map((r) => (
              <li key={r.path}>
                <Button
                  help="help.start.recent.item"
                  variant="row"
                  disabledReason={!r.exists ? "reason.fileMissing" : r.problem ? { key: "reason.cannotOpen", params: { code: r.problem } } : null}
                  onClick={() => void open(r.path)}
                >
                  <span className="recent-name">{r.name}</span>
                  <span className="recent-path muted">{r.path}</span>
                </Button>
                <Button help="help.start.recent.forget" variant="link" onClick={() => void api.forgetProject(r.path).then(setRecent)}>
                  {t("start.recent.forget")}
                </Button>
              </li>
            ))}
          </ul>
        </section>
      </main>

      <footer className="start-footer">
        {status && <Info help="help.footer.version">{t("app.version", { version: status.version })}</Info>}
        {diagnostics && (
          <>
            <Info help="help.footer.data" className="path">
              {t("footer.data", { path: diagnostics.dataDirectory })}
            </Info>
            <Info help="help.footer.log" className="path">
              {t("footer.log", { path: diagnostics.logPath })}
            </Info>
          </>
        )}
        <span className="spacer" />
        <Button help="help.footer.notices" variant="link" onClick={openNotices}>
          {t("footer.notices")}
        </Button>
        <Button help="help.footer.quit" variant="link" onClick={() => void quitApp()}>
          {t("footer.quit")}
        </Button>
      </footer>

      {creating && <NewProjectDialog onClose={() => setCreating(false)} />}

      {notices !== null && (
        <div className="modal-backdrop" onClick={() => setNotices(null)}>
          <div className="modal" role="dialog" aria-label={t("notices.title")} onClick={(e) => e.stopPropagation()}>
            <div className="modal-head">
              <h2>{t("notices.title")}</h2>
              <Button help="help.notices.close" onClick={() => setNotices(null)}>
                {t("common.close")}
              </Button>
            </div>
            <pre className="notices">{notices}</pre>
          </div>
        </div>
      )}
    </div>
  );
}
