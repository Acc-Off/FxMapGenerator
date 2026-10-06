import { useEffect, useRef, useState, type ReactNode } from "react";
import { api } from "../shared/api";
import { Button, Info, Slider, type HelpKey, type Reason } from "../shared/controls";
import { clock, duration, gigabytes } from "../shared/format";
import { useT, type MessageKey } from "../shared/i18n";
import type { Announcement, JobSnapshot, JobState, TodoRow } from "../shared/types";
import { phaseText, stageText, unitText, workerMarks } from "./labels";
import { isActive, jobOfProject, useProjectStore } from "./store";

/** The work beside the visit that makes the provisional satellite tiles (Core/Satellite/ProvisionalOrtho.cs). */
const PROVISIONAL = "ortho.provisional";

const STATE: Record<JobState, MessageKey> = {
  running: "status.state.running",
  stopping: "status.state.stopping",
  stopped: "status.state.stopped",
  done: "status.state.done",
  failed: "status.state.failed",
};

/** A unit by its own name: a map's cell as the cell, a minimap sheet as the sheet (the step says which map). */
function shortUnit(t: (key: MessageKey, params?: Record<string, string | number>) => string, unit: string): string {
  const sheet = /@\d+\/(\d+_\d+)$/.exec(unit);
  if (sheet) return t("status.unit.sheet", { name: sheet[1] });
  if (/@\d+\/lod$/.test(unit)) return t("status.unit.lod");
  const cell = /(?:^|\/)(cell_-?\d+_-?\d+)$/.exec(unit);
  return cell ? cell[1] : unit;
}

/** One part of the run status that opens and closes. */
function Section({ help, title, open, onToggle, children }: { help: HelpKey; title: string; open: boolean; onToggle: () => void; children: ReactNode }) {
  return (
    <div className={`acc${open ? " acc-open" : ""}`}>
      <Button help={help} variant="row" className="acc-head" onClick={onToggle}>
        <span className="acc-mark">{open ? "▾" : "▸"}</span>
        {title}
      </Button>
      {open && <div className="acc-body">{children}</div>}
    </div>
  );
}

/**
 * The run status under the map: one line that is always there (what a run would do, or the running step with its
 * progress, time left, errors, CPU, memory, workers and the buttons) and below it the parts that open and close:
 * the units being processed, the errors, the log.
 */
export function RunStatus() {
  const t = useT();
  const project = useProjectStore((s) => s.project);
  const plan = useProjectStore((s) => s.plan);
  const job = useProjectStore((s) => s.job);
  const log = useProjectStore((s) => s.log);
  const workers = useProjectStore((s) => s.workers);
  const gameUse = useProjectStore((s) => s.gameUse);
  const { start, stop, setWorkers, focusBlock } = useProjectStore.getState();
  const [open, setOpen] = useState({ units: true, errors: true, log: false });
  const logBox = useRef<HTMLDivElement>(null);
  const mine = jobOfProject(job, project);
  const running = isActive(job);
  const shown = mine ? job : null;

  useEffect(() => {
    const box = logBox.current;
    if (box) box.scrollTop = box.scrollHeight;
  }, [log, open.log]);

  // What a run would do now: runnable rows with units ready (their share of the row's time), and the runnable rows after
  // a ready one that have work left (they get it from the earlier step of the same run). A row with parts is the sum of
  // its parts, so it counts through the parts this version runs (with the row's share), never together with them.
  const left: { low: number; high: number }[] = [];
  let earlier = false;
  const count = (r: TodoRow) => {
    if (!plan!.runnable.includes(r.id)) {
      r.children.forEach(count);
      return;
    }
    if (!r.needed) return;
    const ready = plan!.ready[r.id] ?? 0;
    let share = 0;
    if (ready > 0) {
      share = r.remaining > 0 ? Math.min(1, ready / r.remaining) : 1;
      earlier = true;
    } else if (earlier && r.remaining > 0) share = 1;
    if (share === 0) return;
    const parts = r.children.filter((c) => c.needed && plan!.runnable.includes(c.id));
    for (const p of parts.length > 0 ? parts : [r]) left.push({ low: p.low * share, high: p.high * share });
  };
  plan?.table.rows.forEach(count);
  const low = left.reduce((s, r) => s + r.low, 0), high = left.reduce((s, r) => s + r.high, 0);
  // a run with work in the game waits while the game is used by hand (the connection check, the resource's start)
  const visit = plan?.table.rows.find((r) => r.id === "visit");
  const gameWait: Reason | null = !gameUse || !visit?.needed || visit.remaining === 0 ? null : gameUse === "precheck" ? "reason.checking" : "reason.resourceStarting";
  const startReason: Reason | null = running ? (mine ? "reason.running" : "reason.jobElsewhere") : left.length === 0 ? "reason.nothingLeft" : gameWait;

  const processors = plan?.table.processors ?? navigator.hardwareConcurrency ?? 8;
  const index = shown ? shown.stages.findIndex((s) => s.id === shown.stage) : -1;
  const stage = shown && index >= 0 ? shown.stages[index] : null;
  const total = stage?.total ?? 0, done = stage?.done ?? 0;
  // workers above a lowered limit: the ones that started last finish and are not replaced
  const retiringFrom = shown ? shown.active.length - shown.retiring : 0;

  const headline = (() => {
    if (!shown) {
      if (running) return t("status.elsewhere", { name: job!.projectName });
      return left.length > 0 ? t("status.ready", { time: duration(t, low, high) }) : t("status.nothing");
    }
    if (running) {
      if (!stage) return t("status.state.starting");
      return t("status.step", { step: stageText(t, stage.row), i: index + 1, n: shown.stages.length });
    }
    const state = shown.state === "done" ? t("status.state.doneAfter", { time: clock(shown.elapsed) })
      : shown.state === "failed" ? t("status.state.failedWith", { error: shown.error ?? "" })
        : t(STATE[shown.state]);
    return left.length > 0 ? `${state} — ${t("status.ready", { time: duration(t, low, high) })}` : state;
  })();

  return (
    <section className="run-status" data-guide="project.run">
      {shown && <GameDoneNotice job={shown} />}
      <div className="run-head">
        <Info help="help.status.headline" className="run-headline">
          {t("status.title")}: {headline}
        </Info>
        {shown && running && stage?.preparing && (
          <Info help="help.status.preparing" className="muted">
            {t("status.preparing")}
          </Info>
        )}
        {shown && running && stage && !stage.preparing && stage.unit !== "set" && (
          <Info help="help.status.progress" className="run-progress">
            <span className="bar">
              <span style={{ width: `${total > 0 ? (100 * done) / total : 0}%` }} />
            </span>
            {t("status.progress", { done, what: unitText(t, stage.unit, total) })}
          </Info>
        )}
        {shown && running && stage && !stage.preparing && stage.unit === "set" && (
          <Info help="help.status.whole" className="muted">
            {t("status.whole")}
          </Info>
        )}
        {shown && running && <Info help="help.status.left">{t("status.left", { time: duration(t, shown.remainingLow, shown.remainingHigh) })}</Info>}
        {shown && (
          <Info help="help.status.errors" className={shown.failureCount > 0 ? "text-error" : undefined}>
            {t("status.errors", { n: shown.failureCount })}
          </Info>
        )}
        {shown && running && (
          <>
            <Info help="help.status.cpu">{t("status.cpu", { n: Math.round(shown.cpu * 100) })}</Info>
            <Info help="help.status.memory">{t("status.memory", { free: gigabytes(shown.memoryAvailable), total: gigabytes(shown.memoryTotal) })}</Info>
          </>
        )}
        <span className="spacer" />
        {shown && running && (
          <Slider
            help="help.panel.workers"
            label={t("panel.workers")}
            value={workers}
            min={1}
            max={processors}
            marks={workerMarks(processors)}
            display={t("workers.of", { n: workers, total: processors })}
            onChange={setWorkers}
          />
        )}
        {running && mine ? (
          <>
            <Button help="help.run.stopBoundary" disabledReason={shown!.stopMode !== "none" ? "reason.stopping" : null} onClick={() => void stop("boundary")}>
              {t("run.stopBoundary")}
            </Button>
            <Button help="help.run.stopNow" variant="danger" disabledReason={shown!.stopMode === "now" ? "reason.stopping" : null} onClick={() => void stop("now")}>
              {t("run.stopNow")}
            </Button>
          </>
        ) : (
          <Button help="help.run.start" variant="primary" disabledReason={startReason} onClick={() => void start()}>
            {t("run.start")}
          </Button>
        )}
      </div>
      {shown && running && shown.memoryLimitedAt !== null && (
        <Info help="help.status.memoryLimited" block className="text-warn run-note">
          {t("status.memoryLimited", { n: shown.memoryLimitedAt })}
        </Info>
      )}
      {shown?.state === "stopping" && (
        <Info help="help.status.stopping" block className="run-note">
          {t("status.stopping")}
        </Info>
      )}
      <div className="run-sections">
        {shown && running && (
          <Section
            help="help.status.units"
            title={t("status.units", { n: shown.active.length, limit: shown.workers })}
            open={open.units}
            onToggle={() => setOpen((o) => ({ ...o, units: !o.units }))}
          >
            <div className="run-units">
              {shown.active.length === 0 && <span className="muted">{t("status.units.none")}</span>}
              {shown.active.map((a, i) => (
                <Button key={`${a.stage}/${a.unit}`} help="help.status.unit" variant="row" onClick={() => focusBlock(a.unit)}>
                  <span className="unit-name">{shortUnit(t, a.unit)}</span>
                  <span className="unit-phase">
                    {a.stage === PROVISIONAL && <span className="unit-kind">{t("status.unit.provisional")}</span>}
                    {phaseText(t, a.phase)}
                  </span>
                  <span className="bar small">
                    <span style={{ width: `${Math.round(a.fraction * 100)}%` }} />
                  </span>
                  <span className="unit-time">{clock(a.seconds)}</span>
                  {i >= retiringFrom && <span className="unit-retiring">{t("status.retiring")}</span>}
                </Button>
              ))}
            </div>
          </Section>
        )}
        {shown && shown.failures.length > 0 && (
          <Section
            help="help.status.failures"
            title={t("status.failures", { n: shown.failureCount })}
            open={open.errors}
            onToggle={() => setOpen((o) => ({ ...o, errors: !o.errors }))}
          >
            <div className="run-failures">
              {shown.failures.slice(-20).map((f) => (
                <Button key={`${f.stage}/${f.unit}`} help="help.status.failure" variant="row" onClick={() => focusBlock(f.unit)}>
                  <span className="unit-name">{f.unit}</span>
                  <span className="text-error">{f.message}</span>
                </Button>
              ))}
            </div>
          </Section>
        )}
        <Section help="help.status.log" title={t("status.log")} open={open.log} onToggle={() => setOpen((o) => ({ ...o, log: !o.log }))}>
          <div ref={logBox} className="run-log">
            {log.length === 0 ? (
              <span className="muted">{t("status.log.empty")}</span>
            ) : (
              log.map((l, i) => (
                <div key={i}>
                  <span className="muted">{new Date(l.atUtc).toLocaleTimeString()}</span> {l.line}
                </div>
              ))
            )}
          </div>
        </Section>
      </div>
    </section>
  );
}

/**
 * The notice that the work in the game is over (the visit took every block): FiveM and the server can be closed, the
 * rest of the run goes on without the game. One line, with the details on request (the capture resource stopped, or
 * how to stop it when the app could not). It stays while FiveM runs on this PC and goes away once FiveM is closed.
 */
function GameDoneNotice({ job }: { job: JobSnapshot }) {
  const t = useT();
  const done: Announcement | undefined = job.announcements.find((a) => a.key === "gameDone");
  const [open, setOpen] = useState(false);
  const [closed, setClosed] = useState<string | null>(null);
  const [fiveM, setFiveM] = useState<boolean | null>(null);
  const at = done?.atUtc ?? null;
  useEffect(() => {
    if (!at) return;
    let live = true;
    const ask = () => void api.fiveM().then((r) => live && setFiveM(r.running), () => undefined);
    ask();
    const timer = window.setInterval(ask, 5000);
    return () => {
      live = false;
      window.clearInterval(timer);
    };
  }, [at]);
  if (!done || fiveM === false || closed === done.atUtc) return null;
  const v = done.values;
  const stopped = v.stopped === "1";
  const restarted = (v.restarted ?? "").split(",").filter((x) => x);
  return (
    <div className="notice">
      <div className="notice-line">
        <Info help="help.notice.gameDone" className="notice-text">
          <strong>{t("notice.gameDone")}</strong>
          {isActive(job) && ` ${t("notice.gameDone.rest")}`}
          {v.stopped === "0" && <span className="text-warn"> {t("notice.notStopped.short")}</span>}
        </Info>
        <span className="spacer" />
        <Button help="help.notice.details" variant="link" onClick={() => setOpen((o) => !o)}>
          {open ? t("notice.hide") : t("notice.details")}
        </Button>
        <Button help="help.notice.close" variant="link" onClick={() => setClosed(done.atUtc)}>
          {t("common.close")}
        </Button>
      </div>
      {open && (
        <Info help="help.notice.text" block className="notice-details">
          {stopped
            ? t("notice.stopped", { resource: v.resource ?? "" })
            : v.stopped === "0"
              ? t("notice.notStopped", { resource: v.resource ?? "" })
              : t("notice.kept", { resource: v.resource ?? "" })}
          {restarted.length > 0 && <div>{t("notice.restarted", { names: restarted.join(t("list.join")) })}</div>}
          <div className="muted">{t("notice.folder")}</div>
        </Info>
      )}
    </div>
  );
}
