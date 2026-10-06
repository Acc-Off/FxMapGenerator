import { useState, type ReactElement, type ReactNode } from "react";
import { Button, HeaderCell, Info, type HelpKey } from "../shared/controls";
import { duration } from "../shared/format";
import { useT, type MessageKey } from "../shared/i18n";
import type { StageProgress, TodoRow } from "../shared/types";
import { rowHelp, rowText, unitText } from "./labels";
import { isActive, jobOfProject, useProjectStore } from "./store";

/**
 * A row while the list follows a run: a bar with done / total, a word (preparing, running, ...), the table as it is now
 * (not started yet), done, or as the table says (not needed, nothing left). Times are what the run has left for the row.
 */
type Live =
  | { kind: "bar"; done: number; total: number; failed: number; tag: MessageKey | null; low: number; high: number }
  | { kind: "word"; word: MessageKey; help: HelpKey; low: number; high: number }
  | { kind: "waiting"; low: number; high: number }
  | { kind: "done" }
  | { kind: "idle" };

const VISIT = "visit";

/** Units counted one by one on a bar; a stage done once for the whole map only says it is running. */
const WHOLE = new Set(["world", "set"]);

/**
 * The job list: per step what is left and how long it takes with the chosen workers. Rows
 * open to their parts; a click outlines the row's blocks or cells on the map. While the map shows a run of this
 * project, the list follows the run: the steps not started yet keep their count and time, a running one shows a bar
 * with what is done, the visit's items count down from what was left when the run started, and a row with parts sums
 * them up. The times left are the run's own (measured as it goes), so the total matches the run status.
 */
export function TodoPanel() {
  const t = useT();
  const plan = useProjectStore((s) => s.plan);
  const checks = useProjectStore((s) => s.checks);
  const selected = useProjectStore((s) => s.selectedRow);
  const job = useProjectStore((s) => s.job);
  const project = useProjectStore((s) => s.project);
  const view = useProjectStore((s) => s.view);
  const select = useProjectStore((s) => s.select);
  const setScreen = useProjectStore((s) => s.setScreen);
  const [open, setOpen] = useState<Set<string>>(() => new Set(["visit"]));

  if (!plan) return <p className="muted">{t("common.loading")}</p>;
  const anyReady = Object.values(plan.ready).some((n) => n > 0);
  const live = view === "run" && jobOfProject(job, project) ? job : null;
  const progress = new Map<string, StageProgress>();
  if (live) for (const s of live.stages) progress.set(s.row, s);
  const start = live && plan.atStart?.runId === live.runId ? plan.atStart.remaining : null;

  const toggle = (id: string) =>
    setOpen((o) => {
      const n = new Set(o);
      if (n.has(id)) n.delete(id);
      else n.add(id);
      return n;
    });

  // what is left: the count on one line, and for blocks of both kinds the split on a second, smaller line
  const leftText = (r: TodoRow): ReactNode => {
    if (!r.needed) return t("left.notNeeded");
    if (r.remaining === 0) return r.reason === "noVisit" ? t("left.noVisit") : t("left.done");
    const count = t("left.pending", { what: unitText(t, r.unit, r.remaining) });
    if (r.unit !== "block" || r.land === null || r.water === null || r.water === 0 || r.land === 0) return <span className="todo-count">{count}</span>;
    return (
      <>
        <div className="todo-count">{count}</div>
        <div className="todo-split">{t("unit.blockSplit", { land: r.land, water: r.water })}</div>
      </>
    );
  };

  // The work by hand before a visit, done on the FiveM screen: what is still to do, from the prerequisites.
  const check = (id: string) => checks?.find((c) => c.id === id);
  const byHandText = (r: TodoRow): { text: ReactNode; done: boolean } | null => {
    if (!r.needed || r.remaining === 0) return null;
    if (r.id === "gameSetup") {
      const left = [check("capture")?.ok === false && t("todo.setup.capture"), check("render")?.ok === false && t("todo.setup.render")].filter((x): x is string => !!x);
      return left.length === 0
        ? { done: true, text: <Info help="help.todo.setup.done" className="text-ok">{t("todo.setup.done")}</Info> }
        : { done: false, text: <Info help="help.todo.setup.left">{t("todo.setup.left", { what: left.join(t("list.join")) })}</Info> };
    }
    if (r.id === "precheck") {
      const game = check("game"), preset = check("preset");
      if (game?.ok === true && preset?.ok !== false)
        return { done: true, text: <Info help="help.todo.check.done" className="text-ok">{t("todo.check.done", { at: game.values.checkedAt ? new Date(game.values.checkedAt).toLocaleString() : "" })}</Info> };
      if (game?.ok === false || preset?.ok === false)
        return { done: false, text: <Info help="help.todo.check.problems" className="text-error">{t("todo.check.problems")}</Info> };
      return { done: false, text: <Info help="help.todo.check.none" className="todo-count">{t("todo.check.none")}</Info> };
    }
    return null;
  };

  // ---- the rows while following a run

  const stageLive = (r: TodoRow, p: StageProgress): Live => {
    const low = p.remainingLow, high = p.remainingHigh;
    switch (p.state) {
      case "waiting":
        return !r.needed || (r.remaining === 0 && high <= 0) ? { kind: "idle" } : { kind: "waiting", low, high };
      case "running":
        if (p.preparing) return { kind: "word", word: "todo.state.preparing", help: "help.todo.state.preparing", low, high };
        if (WHOLE.has(p.unit) && p.total <= 1) return { kind: "word", word: "todo.state.running", help: "help.todo.state.running", low, high };
        return { kind: "bar", done: p.done, total: p.total, failed: p.failed, tag: null, low, high };
      case "done":
        return { kind: "done" };
      case "stopped":
        return { kind: "bar", done: p.done, total: p.total, failed: p.failed, tag: "todo.state.stopped", low: 0, high: 0 };
      case "failed":
        return { kind: "bar", done: p.done, total: p.total, failed: p.failed, tag: "todo.state.failed", low: 0, high: 0 };
    }
  };

  // The visit's items are not stages: each counts down from what the run started with, and shares the visit's time
  // left (measured per block as it goes) in the proportion of the table's estimates (their middles: one proportion for
  // both ends, so that no item's range comes out upside down).
  const visitPart = (r: TodoRow, visit: TodoRow, vp: StageProgress): Live => {
    if (!r.needed) return { kind: "idle" };
    const over = vp.state === "done" || vp.state === "stopped" || vp.state === "failed";
    const started = vp.state !== "waiting" && !vp.preparing;
    const pending = visit.children.filter((c) => c.needed && c.remaining > 0 && !(c.id === "visit.prepare" && started));
    const middle = (c: TodoRow) => (c.low + c.high) / 2;
    const sumMiddle = pending.reduce((s, c) => s + middle(c), 0);
    const share = (c: TodoRow) => {
      const part = sumMiddle > 0 ? middle(c) / sumMiddle : 0;
      return { low: part * vp.remainingLow, high: part * vp.remainingHigh };
    };
    if (r.id === "visit.prepare") {
      if (started || over) return { kind: "done" };
      return vp.state === "waiting" ? { kind: "waiting", ...share(r) } : { kind: "word", word: "todo.state.running", help: "help.todo.state.running", ...share(r) };
    }
    if (r.id === "visit.cleanup") return over ? { kind: "done" } : r.remaining > 0 ? { kind: "waiting", ...share(r) } : { kind: "idle" };
    const total = start?.[r.id] ?? r.remaining;
    const left = Math.min(r.remaining, total);
    if (total === 0) return { kind: "idle" };
    if (left === 0) return { kind: "done" };
    return { kind: "bar", done: total - left, total, failed: 0, tag: over && vp.state !== "done" ? (vp.state === "failed" ? "todo.state.failed" : "todo.state.stopped") : null, ...share(r) };
  };

  // The game files' two parts run together as one step.
  const partOf = (r: TodoRow, p: StageProgress): Live => {
    if (!r.needed || (r.remaining === 0 && p.state === "waiting")) return { kind: "idle" };
    const whole = stageLive(r, p);
    if (whole.kind !== "waiting" && whole.kind !== "word" && whole.kind !== "bar") return whole;
    // half of the step's time each, as the table estimates
    return { ...whole, low: whole.low / 2, high: whole.high / 2 };
  };

  // A row with parts that is not a step itself: the sum of its parts (units when they all count the same thing,
  // else steps), with their times.
  const sum = (parts: { row: TodoRow; live: Live | null }[]): Live => {
    const needed = parts.filter((x) => x.row.needed && x.live && x.live.kind !== "idle");
    if (needed.length === 0) return { kind: "idle" };
    const low = needed.reduce((s, x) => s + ("low" in x.live! ? x.live.low : 0), 0);
    const high = needed.reduce((s, x) => s + ("high" in x.live! ? x.live.high : 0), 0);
    if (needed.every((x) => x.live!.kind === "done")) return { kind: "done" };
    if (needed.every((x) => x.live!.kind === "waiting")) return { kind: "waiting", low, high };
    const units = new Set(needed.map((x) => progress.get(x.row.id)?.unit ?? x.row.unit));
    const sameUnits = units.size === 1 && !WHOLE.has([...units][0]);
    let done = 0, total = 0, failed = 0;
    for (const x of needed) {
      const p = progress.get(x.row.id);
      const l = x.live!;
      if (sameUnits) {
        const n = p && p.state !== "waiting" ? p.total : l.kind === "bar" ? l.total : x.row.remaining;
        total += n;
        done += l.kind === "done" ? n : l.kind === "bar" ? l.done : 0;
        failed += l.kind === "bar" ? l.failed : 0;
      } else {
        total += 1;
        done += l.kind === "done" ? 1 : 0;
      }
    }
    return { kind: "bar", done, total, failed, tag: null, low, high };
  };

  const liveOf = (r: TodoRow, parent: TodoRow | null): Live | null => {
    if (!live) return null;
    const p = progress.get(r.id);
    if (p) return stageLive(r, p);
    const vp = progress.get(VISIT);
    if (parent?.id === VISIT && vp) return visitPart(r, parent, vp);
    const pp = parent ? progress.get(parent.id) : undefined;
    if (pp) return partOf(r, pp);
    if (r.children.length > 0 && r.children.some((c) => progress.has(c.id)))
      return sum(r.children.map((c) => ({ row: c, live: liveOf(c, r) })));
    return null;
  };

  const bar = (l: Extract<Live, { kind: "bar" }>) => (
    <Info help="help.todo.progress" className="todo-progress">
      <span className="bar small">
        <span style={{ width: `${l.total > 0 ? (100 * l.done) / l.total : 0}%` }} />
      </span>
      {t("todo.progress", { done: l.done, total: l.total })}
      {l.failed > 0 && <span className="text-error"> {t("todo.progress.failed", { n: l.failed })}</span>}
      {l.tag && <span className={l.tag === "todo.state.failed" ? "text-error" : "muted"}> {t(l.tag)}</span>}
    </Info>
  );

  const rows: ReactElement[] = [];
  const walk = (r: TodoRow, depth: number, parent: TodoRow | null) => {
    const l = liveOf(r, parent);
    const hand = r.byHand ? byHandText(r) : null;
    const hasChildren = r.children.length > 0;
    const idle = hand ? hand.done : l ? l.kind === "done" || l.kind === "idle" : !r.needed || r.remaining === 0;
    const afterVisit = !live && !r.byHand && !anyReady && r.remaining > 0 && r.needed;
    let left: ReactNode;
    let time = "";
    if (hand) {
      left = hand.text;
      if (!idle && r.high > 0) time = duration(t, r.low, r.high);
    } else if (!l) {
      left = leftText(r);
      if (!idle && r.high > 0) time = duration(t, r.low, r.high);
    } else
      switch (l.kind) {
        case "bar":
          left = bar(l);
          if (l.high > 0) time = duration(t, l.low, l.high);
          break;
        case "word":
          left = (
            <Info help={l.help} className="todo-count">
              {t(l.word)}
            </Info>
          );
          if (l.high > 0) time = duration(t, l.low, l.high);
          break;
        case "waiting":
          left =
            r.remaining > 0 ? (
              leftText(r)
            ) : (
              <Info help="help.todo.state.waiting" className="todo-count">
                {t("todo.state.waiting")}
              </Info>
            );
          if (l.high > 0) time = duration(t, l.low, l.high);
          break;
        case "done":
          left = t("left.done");
          break;
        case "idle":
          left = leftText(r);
          break;
      }
    rows.push(
      <tr key={r.id} className={`${idle ? "row-idle" : ""}${selected === r.id ? " row-selected" : ""}`}>
        <td style={{ paddingLeft: 6 + depth * 16 }}>
          {hasChildren ? (
            <Button help="help.todo.expand" variant="link" className="expander" onClick={() => toggle(r.id)}>
              {open.has(r.id) ? "▾" : "▸"}
            </Button>
          ) : (
            <span className="expander-gap" />
          )}
          <Button help={rowHelp(r.id)} variant="row" className="todo-name" onClick={() => select(r.id)}>
            {rowText(t, r.id)}
          </Button>
          {r.needed && r.usesGame && depth === 0 && !r.byHand && (
            <Info help="help.todo.game" className="tag">
              {t("todo.game.mark")}
            </Info>
          )}
          {r.needed && r.byHand && (
            <Info help="help.todo.byHand" className="tag">
              {t("todo.byHand")}
            </Info>
          )}
          {r.byHand && r.needed && r.remaining > 0 && (
            <Button help="help.todo.openPrecheck" variant="link" className="row-link" onClick={() => setScreen("precheck")}>
              {t("todo.openPrecheck")}
            </Button>
          )}
          {afterVisit && (
            <Info help="help.todo.afterVisit" className="tag">
              {t("todo.afterVisit")}
            </Info>
          )}
        </td>
        <td>{left}</td>
        <td className="num">{time}</td>
      </tr>,
    );
    if (hasChildren && open.has(r.id)) for (const c of r.children) walk(c, depth + 1, r);
  };
  for (const r of plan.table.rows) walk(r, 0, null);

  const table = plan.table;
  // while a run goes on, the total is the run's time left (the same as the run status) and its part in the game
  const running = live && isActive(live) ? live : null;
  const totalLow = running ? running.remainingLow : table.low, totalHigh = running ? running.remainingHigh : table.high;
  const gameStages = running ? running.stages.filter((s) => s.usesGame) : [];
  const gameLow = running ? gameStages.reduce((s, x) => s + x.remainingLow, 0) : table.gameLow;
  const gameHigh = running ? gameStages.reduce((s, x) => s + x.remainingHigh, 0) : table.gameHigh;
  return (
    <div className="todo">
      <table className="todo-table">
        <thead>
          <tr>
            <HeaderCell help="help.col.step">{t("col.step")}</HeaderCell>
            <HeaderCell help="help.col.left">{t("col.left")}</HeaderCell>
            <HeaderCell help="help.col.time" className="num">
              {t("col.time")}
            </HeaderCell>
          </tr>
        </thead>
        <tbody>{rows}</tbody>
        <tfoot>
          <tr>
            <td>
              <Info help={running ? "help.todo.totalRunning" : "help.todo.total"}>{t("todo.total")}</Info>
            </td>
            <td>
              <Info help="help.todo.range" className="muted">
                {t("todo.range", { blocks: table.rangeBlocks, cells: table.cells })}
              </Info>
            </td>
            <td className="num">
              {duration(t, totalLow, totalHigh)}
              {gameHigh > 0 && <div className="muted">{t("todo.game", { time: duration(t, gameLow, gameHigh) })}</div>}
            </td>
          </tr>
        </tfoot>
      </table>
    </div>
  );
}
