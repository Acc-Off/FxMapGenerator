import { useEffect, useState } from "react";
import { api, ApiError } from "../shared/api";
import { Button, Check, HeaderCell, Info, Select, type HelpKey, type Reason } from "../shared/controls";
import { errorText, useT, type MessageKey } from "../shared/i18n";
import type { CaptureResourceDto, PrecheckItem, PrecheckItemId, RenderStatus, ResourcePlacement, ResourceStartDto, SettleMeasurement } from "../shared/types";
import { isActive, useProjectStore } from "../project/store";

const ITEMS: Record<PrecheckItemId, readonly [MessageKey, HelpKey]> = {
  console: ["precheck.item.console", "help.precheck.item.console"],
  resource: ["precheck.item.resource", "help.precheck.item.resource"],
  window: ["precheck.item.window", "help.precheck.item.window"],
  resources: ["precheck.item.resources", "help.precheck.item.resources"],
  testShot: ["precheck.item.testShot", "help.precheck.item.testShot"],
  environment: ["precheck.item.environment", "help.precheck.item.environment"],
  settle: ["precheck.item.settle", "help.precheck.item.settle"],
};

/** What a code of a pre-check item says (the item's values fill the placeholders). */
const CODES: Record<string, MessageKey> = {
  noConsole: "precheck.code.noConsole",
  noAnswer: "precheck.code.noAnswer",
  protocol: "precheck.code.protocol",
  version: "precheck.code.version",
  noServer: "precheck.code.noServer",
  noAce: "precheck.code.noAce",
  players: "precheck.code.players",
  noWindow: "precheck.code.noWindow",
  windowSize: "precheck.code.windowSize",
  noStates: "precheck.code.noStates",
  envFailed: "precheck.code.envFailed",
  tileFailed: "precheck.code.tileFailed",
  noFrame: "precheck.code.noFrame",
  notification: "precheck.code.notification",
  boxes: "precheck.code.boxes",
  noStatus: "precheck.code.noStatus",
  weather: "precheck.code.weather",
  time: "precheck.code.time",
  npcs: "precheck.code.npcs",
  unsettled: "precheck.code.unsettled",
  notMeasured: "precheck.code.notMeasured",
};

const DISTRICTS: Record<string, MessageKey> = {
  north: "precheck.district.north",
  south: "precheck.district.south",
};

/** Why a block of the settle measurement was not measured. */
const SETTLE_PROBLEMS: Record<string, MessageKey> = {
  tile: "precheck.found.settle.problem.tile",
  water: "precheck.found.settle.problem.water",
  frames: "precheck.found.settle.problem.frames",
  unsettled: "precheck.found.settle.problem.unsettled",
};

/** Milliseconds as seconds with at most two decimals (1500 -> 1.5). */
function seconds(ms: string | number | null | undefined): string {
  return ms === null || ms === undefined || ms === "" ? "?" : String(Number((Number(ms) / 1000).toFixed(2)));
}

const STATES: Record<string, MessageKey> = {
  started: "precheck.state.started",
  stopped: "precheck.state.stopped",
  missing: "precheck.state.missing",
};

const WHY: Record<string, MessageKey> = {
  detail: "render.why.detail",
  lod: "render.why.lod",
  window: "render.why.window",
  still: "render.why.still",
  stable: "render.why.stable",
};

function when(utc: string): string {
  return new Date(utc).toLocaleString();
}

/**
 * The FiveM setup and check, in the order the work is done. Before starting FiveM: the capture resource
 * put into the server's resources (written by the app into a folder, or downloaded as a zip) and FiveM's graphics
 * settings for the shots (changed while FiveM is closed). After joining the server: the resource started from the game
 * console when it does not answer, and the check with the game (its items one by one in plain words, the test shot
 * with what stood out boxed).
 */
export function PrecheckScreen() {
  const t = useT();
  const latest = useProjectStore((s) => s.precheck);
  const job = useProjectStore((s) => s.job);
  const project = useProjectStore((s) => s.project)!;
  const gameUse = useProjectStore((s) => s.gameUse);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const gameBusy = isActive(job) && !!job?.stages.some((s) => s.usesGame && (s.state === "running" || s.state === "waiting"));
  // a check made before the app stopped the capture resource (the end of a capture) counts as not made
  const stoppedAt = latest?.resourceStoppedUtc ?? null;
  const report = stoppedAt ? null : latest;
  const checking = running || gameUse === "precheck";

  const run = async () => {
    setRunning(true);
    setError(null);
    try {
      useProjectStore.getState().setPrecheck(await api.runPrecheck());
    } catch (e) {
      setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
    } finally {
      setRunning(false);
    }
  };

  // one user of the game at a time: the check waits for the resource's start and for a run's work in the game
  const runReason: Reason | null = checking ? "reason.checking" : gameUse === "resourceStart" ? "reason.resourceStarting" : gameBusy ? "reason.gameBusy" : null;
  const problems = report ? report.items.filter((i) => i.ok !== true).length : 0;
  const inMap = report ? report.boxes.filter((b) => b.inMap).length : 0;
  const outside = report ? report.boxes.length - inMap : 0;

  return (
    <div className="screen precheck">
      <Info help="help.fivem.intro" block className="screen-intro muted">
        {t("fivem.intro")}
      </Info>

      <section className="fivem-section">
        <Info help="help.fivem.before" block className="fivem-section-title">
          <span className="fivem-step">1</span>
          {t("fivem.before")}
        </Info>
        <div className="precheck-body">
          <div data-guide="precheck.resource">
            <ResourcePanel />
          </div>
          <div data-guide="precheck.render">
            <RenderPanel />
          </div>
        </div>
      </section>

      <section className="fivem-section">
        <Info help="help.fivem.after" block className="fivem-section-title">
          <span className="fivem-step">2</span>
          {t("fivem.after")}
        </Info>
        <div className="precheck-body">
          <div className="precheck-items">
            <div data-guide="precheck.start">
              <StartPanel gameBusy={gameBusy} stoppedAt={stoppedAt} />
            </div>
            <div data-guide="precheck.check">
              <div className="screen-head precheck-head">
                <Info help="help.precheck.items" className="section-title">
                  {t("precheck.items")}
                </Info>
                <span className="spacer" />
                <Button help="help.precheck.run" variant="primary" disabledReason={runReason} onClick={() => void run()}>
                  {checking ? t("precheck.running") : t("precheck.run")}
                </Button>
              </div>
              <Info help="help.precheck.intro" block className="muted">
                {t("precheck.intro", { console: `${project.file.console.host}:${project.file.console.port}` })}
              </Info>
              {report && (
                <Info help="help.precheck.last" block className={report.ok ? "text-ok" : "text-error"}>
                  {t("precheck.last", { at: when(report.atUtc) })} {report.ok ? t("precheck.ok") : t("precheck.problems", { n: problems })}
                </Info>
              )}
              {stoppedAt && (
                <Info help="help.precheck.reset" block className="note">
                  {t("precheck.reset", { at: when(stoppedAt) })}
                </Info>
              )}
              {error && <div className="banner banner-error">{error}</div>}
              {report ? (
                <ul className="precheck-list">
                  {(Object.keys(ITEMS) as PrecheckItemId[])
                    // a check without shots (scans only) leaves out the window, the test shot and the settle measurement
                    .filter((id) => !report.items.some((i) => i.id === id && i.codes?.includes("notNeeded")))
                    .map((id) => (
                      <Item key={id} id={id} item={report.items.find((i) => i.id === id) ?? null} settle={report.settle} />
                    ))}
                </ul>
              ) : (
                !stoppedAt && <p className="muted">{t("precheck.none")}</p>
              )}
            </div>
          </div>
          <div className="precheck-shot" data-guide="precheck.shot">
            <Info help="help.precheck.shot" block className="section-title">
              {t("precheck.shot")}
            </Info>
            {report?.items.some((i) => i.id === "testShot" && i.ok !== null && !i.codes?.some((c) => c !== "boxes" && c !== "notification")) ? (
              <>
                <img className="shot" src={api.precheckFile("shot-marked.png", report.atUtc)} alt={t("precheck.shot")} />
                <Info help="help.precheck.shot.legend" block className="muted">
                  {t("precheck.shot.area")}{" "}
                  {inMap > 0 ? t("precheck.shot.boxed", { n: inMap }) : t("precheck.shot.clean")}
                  {outside > 0 && ` ${t("precheck.shot.outside", { n: outside })}`}
                  {report.notification && ` ${t("precheck.shot.notification")}`}
                </Info>
              </>
            ) : (
              <p className="muted">{t("precheck.shot.none")}</p>
            )}
          </div>
        </div>
      </section>
    </div>
  );
}

/** How the mark that the resource is on the server came about. */
const HOW: Record<ResourcePlacement["how"], MessageKey> = {
  saved: "fivem.resource.how.saved",
  answered: "fivem.resource.how.answered",
  user: "fivem.resource.how.user",
};

/**
 * The capture resource for the server: written by the app as the folder fxmapgen-capture into a folder chosen (the
 * server's resources when the server is on this PC or a shared folder; one already there is replaced only when asked),
 * or downloaded as a zip holding that folder (for hosts that take a zip). The folder name is the resource name the app
 * starts it by. The app cannot see the server's folders, so a mark says it is there: set when the app wrote it or it
 * answered in the game, or by hand.
 */
function ResourcePanel() {
  const t = useT();
  const refresh = useProjectStore((s) => s.refresh);
  const [info, setInfo] = useState<CaptureResourceDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<{ folder: string; found: string } | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    void api.captureResource().then(setInfo, () => setInfo(null));
  }, []);

  const fail = (e: unknown) => setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
  const save = async (folder: string, replace: boolean) => {
    setBusy(true);
    setError(null);
    setConfirm(null);
    try {
      const r = await api.saveCapture(folder, replace);
      setInfo(r);
      setMessage(t("fivem.resource.saved", { path: r.written ?? folder }));
      await refresh();
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  };
  const pick = async () => {
    setMessage(null);
    const r = await api.pickFolder(undefined, t("fivem.resource.pick"));
    if (!r.path) return;
    try {
      const f = await api.captureFound(r.path);
      if (f.found) setConfirm({ folder: r.path, found: f.found });
      else await save(r.path, false);
    } catch (e) {
      fail(e);
    }
  };
  const mark = async (placed: boolean) => {
    try {
      setInfo(await api.capturePlaced(placed));
      await refresh();
    } catch (e) {
      fail(e);
    }
  };

  if (!info) return <p className="muted">{t("common.loading")}</p>;
  const placed = info.placed?.version === info.version;
  return (
    <div className="resource-panel">
      <Info help="help.fivem.resource" block className="section-title">
        {t("fivem.resource")}
      </Info>
      <Info help="help.fivem.resource.version" block>
        {t("fivem.resource.version", { name: info.name, version: info.version })}
      </Info>
      <div className="render-actions">
        <Button help="help.fivem.resource.save" variant="primary" disabledReason={busy ? "reason.saving" : null} onClick={() => void pick()}>
          {t("fivem.resource.save")}
        </Button>
        <Button help="help.fivem.resource.zip" onClick={() => (window.location.href = api.captureZipUrl)}>
          {t("fivem.resource.zip")}
        </Button>
      </div>
      <Info help="help.fivem.resource.name" block className="note">
        {t("fivem.resource.name", { name: info.name })}
      </Info>
      {confirm && (
        <div className="render-actions">
          <Info help="help.fivem.resource.replace">{t("fivem.resource.replaceQ", { folder: confirm.folder, name: info.name, found: confirm.found })}</Info>
          <Button help="help.fivem.resource.replace" variant="primary" onClick={() => void save(confirm.folder, true)}>
            {t("fivem.resource.replace")}
          </Button>
          <Button help="help.fivem.resource.cancel" onClick={() => setConfirm(null)}>
            {t("common.cancel")}
          </Button>
        </div>
      )}
      <Check help="help.fivem.resource.placed" checked={placed} onChange={(v) => void mark(v)}>
        {t("fivem.resource.placed", { version: info.version })}
      </Check>
      {info.placed && (
        <Info help="help.fivem.resource.how" block className="muted">
          {t(HOW[info.placed.how], { version: info.placed.version, at: when(info.placed.atUtc), folder: info.placed.folder ?? "" })}
          {!placed && ` ${t("fivem.resource.old", { version: info.version })}`}
        </Info>
      )}
      {message && (
        <Info help="help.fivem.resource.saved" block className="text-ok">
          {message}
        </Info>
      )}
      {error && <div className="banner banner-error">{error}</div>}
    </div>
  );
}

/**
 * Starting the capture resource from the game console (the player needs the server's permission for it): when it
 * answers it is left as it is; otherwise the app sends refresh and ensure and asks again. When it still does not answer,
 * the likely reasons, and the console's own lines. A result from before the app stopped the resource (the end of a
 * capture) is shown no more.
 */
function StartPanel({ gameBusy, stoppedAt }: { gameBusy: boolean; stoppedAt: string | null }) {
  const t = useT();
  const refresh = useProjectStore((s) => s.refresh);
  const gameUse = useProjectStore((s) => s.gameUse);
  const [result, setResult] = useState<{ dto: ResourceStartDto; at: number } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lines, setLines] = useState(false);

  const start = async () => {
    setBusy(true);
    setError(null);
    try {
      const dto = await api.startResource();
      setResult({ dto, at: Date.now() });
      await refresh();
    } catch (e) {
      setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
    } finally {
      setBusy(false);
    }
  };

  const starting = busy || gameUse === "resourceStart";
  // one user of the game at a time: the start waits for the connection check and for a run's work in the game
  const startReason: Reason | null = starting ? "reason.starting" : gameUse === "precheck" ? "reason.checking" : gameBusy ? "reason.gameBusy" : null;
  const r = result && !(stoppedAt && Date.parse(stoppedAt) > result.at) ? result.dto : null;
  const otherName = r?.resource && r.resource !== "fxmapgen-capture";
  const otherVersion = r?.version && r.version !== r.expected;
  return (
    <div className="start-panel">
      <div className="screen-head">
        <Info help="help.fivem.start" className="section-title">
          {t("fivem.start")}
        </Info>
        <span className="spacer" />
        <Button help="help.fivem.start.run" disabledReason={startReason} onClick={() => void start()}>
          {starting ? t("fivem.start.running") : t("fivem.start.run")}
        </Button>
      </div>
      {r && (
        <div className="start-result">
          {r.state === "noConsole" && (
            <Info help="help.fivem.start.result" block className="check-row check-ng">
              <span className="check-mark">✗</span>
              {t("fivem.start.noConsole")}
            </Info>
          )}
          {(r.state === "running" || r.state === "started") && (
            <Info help="help.fivem.start.result" block className={`check-row ${otherVersion ? "check-ng" : "check-ok"}`}>
              <span className="check-mark">{otherVersion ? "✗" : "✓"}</span>
              {t(r.state === "running" ? "fivem.start.running.ok" : "fivem.start.started", { resource: r.resource ?? "", version: r.version ?? "" })}
              {otherName && <div className="muted">{t("fivem.start.otherName", { resource: r.resource ?? "" })}</div>}
              {otherVersion && <div className="text-error">{t("fivem.start.otherVersion", { version: r.version ?? "", expected: r.expected })}</div>}
            </Info>
          )}
          {r.state === "notStarted" && (
            <Info help="help.fivem.start.result" block className="check-row check-ng">
              <span className="check-mark">✗</span>
              {t("fivem.start.notStarted")}
              <div className="precheck-fix">
                {t(r.reason === "notFound" ? "fivem.start.why.notFound" : r.reason === "denied" ? "fivem.start.why.denied" : "fivem.start.why.noAnswer")}
              </div>
            </Info>
          )}
          {r.console.length > 0 && (
            <>
              <Button help="help.fivem.start.console" variant="link" onClick={() => setLines((v) => !v)}>
                {lines ? t("fivem.start.console.hide") : t("fivem.start.console.show")}
              </Button>
              {lines && <pre className="start-console">{r.console.join("\n")}</pre>}
            </>
          )}
        </div>
      )}
      {error && <div className="banner banner-error">{error}</div>}
    </div>
  );
}

/**
 * One check: ✓ / ✗ / — (not reached), what was found, and what to do when something is wrong. The settle wait also
 * lists its blocks.
 */
function Item({ id, item, settle }: { id: PrecheckItemId; item: PrecheckItem | null; settle: SettleMeasurement | null }) {
  const t = useT();
  const [label, help] = ITEMS[id];
  const mark = !item || item.ok === null ? "skip" : item.ok ? "ok" : "ng";
  const v = item?.values ?? {};
  const district = v.district ? (DISTRICTS[v.district] ? t(DISTRICTS[v.district]) : v.district) : "";
  const found = (() => {
    if (!item || item.ok === null) return t("precheck.skipped");
    switch (id) {
      case "console":
        return t("precheck.found.console", { console: v.console ?? "" });
      case "resource":
        return v.version ? t("precheck.found.resource", { version: v.version, server: v.server ?? "", players: v.players ?? "?" }) : "";
      case "window":
        return v.width ? t("precheck.found.window", { width: v.width, height: v.height }) : "";
      case "resources": {
        const names = Object.keys(v);
        return names.length === 0 ? t("precheck.found.resources.none")
          : names.map((n) => `${n}: ${STATES[v[n]] ? t(STATES[v[n]]) : v[n]}`).join(t("precheck.found.join"));
      }
      case "testShot":
        return v.block
          ? t("precheck.found.testShot", { block: v.block, boxes: v.boxes ?? "0" }) +
              (v.outside && v.outside !== "0" ? t("precheck.found.testShot.outside", { outside: v.outside }) : "")
          : "";
      case "environment":
        return v.weather ? t("precheck.found.environment", { weather: v.weather, hour: v.hour, minute: (v.minute ?? "0").padStart(2, "0"), peds: v.nearPeds, vehicles: v.nearVehicles }) : "";
      case "settle":
        if (!v.district) return "";
        return item.ok
          ? t("precheck.found.settle", { district, stable: seconds(v.stableMs), fps: v.fps || "?", wait: seconds(v.waitMs) })
          : t("precheck.found.settle.failed", { district, tried: (v.tried ?? "").split(" ").join(t("precheck.found.join")), wait: seconds(v.waitMs) });
    }
  })();
  const blocks = id === "settle" && item && item.ok !== null && settle
    ? settle.blocks.map((b) => b.problem
        ? t(SETTLE_PROBLEMS[b.problem] ?? "precheck.found.settle.problem.tile", { block: b.block, for: seconds(v.forMs) })
        : t("precheck.found.settle.block", { block: b.block, stable: seconds(b.stableMs) }))
    : [];
  const codes = item?.codes ?? [];
  const params = { ...v, minute: (v.minute ?? "0").padStart(2, "0"), for: seconds(v.forMs), wait: seconds(v.waitMs) };
  return (
    <li className={`precheck-item check-${mark === "skip" ? "unknown" : mark}`}>
      <Info help={help} className="precheck-item-head">
        <span className="check-mark">{mark === "ok" ? "✓" : mark === "ng" ? "✗" : "—"}</span>
        {t(label)}
      </Info>
      {found && <div className="precheck-found muted">{found}</div>}
      {blocks.length > 0 && <div className="precheck-found muted">{blocks.join(t("precheck.found.join"))}</div>}
      {item && item.ok === false && (
        <ul className="precheck-fix">
          {codes.length > 0
            ? codes.map((c) => <li key={c}>{CODES[c] ? t(CODES[c], params) : item.message}</li>)
            : <li>{item.message}</li>}
        </ul>
      )}
    </li>
  );
}

/** FiveM's graphics settings: the values the shots need that the file does not have yet, putting them in, backups. */
function RenderPanel() {
  const t = useT();
  const [status, setStatus] = useState<RenderStatus | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [backup, setBackup] = useState<string>("");
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = () => void api.render().then(setStatus, (e) => setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e)));
  useEffect(load, []);

  const act = async (work: () => Promise<{ backup: string; status: RenderStatus }>, done: MessageKey) => {
    setError(null);
    setConfirm(false);
    try {
      const r = await work();
      setStatus(r.status);
      setMessage(t(done, { backup: r.backup }));
    } catch (e) {
      setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
    }
  };

  if (!status) return error ? <div className="banner banner-error">{error}</div> : <p className="muted">{t("common.loading")}</p>;
  const applyReason: Reason | null = !status.exists ? "reason.noSettingsFile" : status.fiveMRunning ? "reason.fiveMRunning" : status.differences.length === 0 ? "reason.noDifference" : null;
  const restoreReason: Reason | null = status.fiveMRunning ? "reason.fiveMRunning" : status.backups.length === 0 ? "reason.noBackup" : null;
  const chosen = backup || status.backups[0] || "";

  return (
    <div className="render-panel">
      <Info help="help.render.title" block className="section-title">
        {t("render.title")}
      </Info>
      <Info help="help.render.path" block className="path muted">
        {status.path}
      </Info>
      <Info help="help.render.state" block className={status.differences.length === 0 && status.exists ? "text-ok" : undefined}>
        {!status.exists ? t("render.state.noFile") : status.differences.length === 0 ? t("render.state.same") : t("render.state.differs", { n: status.differences.length })}
        {status.fiveMRunning && ` ${t("render.state.running")}`}
      </Info>
      {status.differences.length > 0 && (
        <table className="render-table">
          <thead>
            <tr>
              <HeaderCell help="help.render.col.key">{t("render.col.key")}</HeaderCell>
              <HeaderCell help="help.render.col.current">{t("render.col.current")}</HeaderCell>
              <HeaderCell help="help.render.col.wanted">{t("render.col.wanted")}</HeaderCell>
              <HeaderCell help="help.render.col.why">{t("render.col.why")}</HeaderCell>
            </tr>
          </thead>
          <tbody>
            {status.differences.map((d) => (
              <tr key={`${d.section}/${d.key}`}>
                <td>{d.key}</td>
                <td>{d.current ?? t("render.missing")}</td>
                <td>{d.wanted}</td>
                <td>{WHY[d.why] ? t(WHY[d.why]) : d.why}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="render-actions">
        {confirm ? (
          <>
            <Info help="help.render.confirm">{t("render.confirm")}</Info>
            <Button help="help.render.apply" variant="primary" onClick={() => void act(api.applyRender, "render.applied")}>
              {t("render.apply.yes")}
            </Button>
            <Button help="help.render.cancel" onClick={() => setConfirm(false)}>
              {t("common.cancel")}
            </Button>
          </>
        ) : (
          <Button help="help.render.apply" disabledReason={applyReason} onClick={() => setConfirm(true)}>
            {t("render.apply")}
          </Button>
        )}
        <span className="spacer" />
        <Select
          help="help.render.backup"
          label={t("render.backup")}
          value={chosen}
          options={status.backups.length ? status.backups.map((b) => ({ value: b, label: b })) : [{ value: "", label: t("render.backup.none") }]}
          disabledReason={restoreReason}
          onChange={setBackup}
        />
        <Button help="help.render.restore" disabledReason={restoreReason} onClick={() => void act(() => api.restoreRender(chosen), "render.restored")}>
          {t("render.restore")}
        </Button>
        <Button help="help.render.reload" onClick={load}>
          {t("render.reload")}
        </Button>
      </div>
      {message && (
        <Info help="help.render.done" block className="text-ok">
          {message}
        </Info>
      )}
      {error && <div className="banner banner-error">{error}</div>}
    </div>
  );
}
