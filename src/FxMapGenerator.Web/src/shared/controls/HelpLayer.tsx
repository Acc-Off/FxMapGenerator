import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useT, type Params } from "../i18n";
import type { HelpKey, ReasonKey } from "./help";

/** Delay before the help appears. */
const DELAY_MS = 400;
const GAP = 6;

interface Tip {
  help: HelpKey;
  /** A text of its own (data-help-text) instead of the key's. */
  text: string | null;
  reason: ReasonKey | null;
  params?: Params;
  anchor: DOMRect;
}

/**
 * The one tooltip of the page. Any element carrying data-help (set by the controls) shows its help after a short
 * delay, on mouse hover and on keyboard focus alike (data-help-text: a text of its own instead of the key's);
 * data-help-reason adds why the item is disabled. Not while a screen's guide is shown.
 */
export function HelpLayer() {
  const t = useT();
  const [tip, setTip] = useState<Tip | null>(null);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);
  const box = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let timer = 0;
    let current: HTMLElement | null = null;
    const find = (e: Event) => (e.target instanceof Element ? e.target.closest<HTMLElement>("[data-help]") : null);
    const hide = () => {
      window.clearTimeout(timer);
      current = null;
      setTip(null);
    };
    const show = (el: HTMLElement) => {
      // none while a guide is shown: its box is the explanation
      if (el === current || document.body.classList.contains("driver-active")) return;
      window.clearTimeout(timer);
      current = el;
      setTip(null);
      timer = window.setTimeout(() => {
        if (current !== el || !el.isConnected) return;
        const params = el.dataset.helpReasonParams ? (JSON.parse(el.dataset.helpReasonParams) as Params) : undefined;
        setTip({ help: el.dataset.help as HelpKey, text: el.dataset.helpText ?? null, reason: (el.dataset.helpReason as ReasonKey | undefined) ?? null, params, anchor: el.getBoundingClientRect() });
      }, DELAY_MS);
    };
    const onOver = (e: PointerEvent) => {
      const el = find(e);
      if (el) show(el);
      else if (current) hide();
    };
    const onFocus = (e: FocusEvent) => {
      const el = find(e);
      if (el) show(el);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") hide();
    };
    document.addEventListener("pointerover", onOver);
    document.addEventListener("focusin", onFocus);
    document.addEventListener("focusout", hide);
    document.addEventListener("pointerdown", hide);
    document.addEventListener("keydown", onKey);
    window.addEventListener("scroll", hide, true);
    window.addEventListener("blur", hide);
    return () => {
      window.clearTimeout(timer);
      document.removeEventListener("pointerover", onOver);
      document.removeEventListener("focusin", onFocus);
      document.removeEventListener("focusout", hide);
      document.removeEventListener("pointerdown", hide);
      document.removeEventListener("keydown", onKey);
      window.removeEventListener("scroll", hide, true);
      window.removeEventListener("blur", hide);
    };
  }, []);

  // Below the item when it fits, above otherwise; kept inside the window.
  useLayoutEffect(() => {
    if (!tip || !box.current) {
      setPos(null);
      return;
    }
    const { width, height } = box.current.getBoundingClientRect();
    const a = tip.anchor;
    const below = a.bottom + GAP + height <= window.innerHeight;
    const top = below ? a.bottom + GAP : Math.max(GAP, a.top - GAP - height);
    const left = Math.min(Math.max(GAP, a.left), Math.max(GAP, window.innerWidth - width - GAP));
    setPos({ left, top });
  }, [tip]);

  if (!tip) return null;
  return (
    <div
      ref={box}
      className="help-tip"
      role="tooltip"
      style={pos ? { left: pos.left, top: pos.top } : { left: -9999, top: -9999 }}
    >
      <HelpText text={tip.text ?? t(tip.help)} />
      {tip.reason && <div className="help-tip-reason">{t(tip.reason, tip.params)}</div>}
    </div>
  );
}

/**
 * A help text: paragraphs (a line break stays one, an empty line starts a new paragraph) and small tables, written as
 * lines that start with "|" and separate their cells with "|" (the first row is the head).
 */
function HelpText({ text }: { text: string }) {
  const blocks: ({ kind: "p"; lines: string[] } | { kind: "table"; rows: string[][] })[] = [];
  let open = false;
  for (const line of text.split("\n")) {
    const last = blocks[blocks.length - 1];
    if (line.startsWith("|")) {
      const cells = line.slice(1).split("|").map((c) => c.trim());
      if (last?.kind === "table") last.rows.push(cells);
      else blocks.push({ kind: "table", rows: [cells] });
      open = false;
    } else if (line.trim() === "") open = false;
    else if (open && last?.kind === "p") last.lines.push(line);
    else {
      blocks.push({ kind: "p", lines: [line] });
      open = true;
    }
  }
  return (
    <>
      {blocks.map((b, i) =>
        b.kind === "p" ? (
          <p key={i} className="help-tip-text">
            {b.lines.join("\n")}
          </p>
        ) : (
          <table key={i} className="help-tip-table">
            <tbody>
              {b.rows.map((r, j) => (
                <tr key={j} className={j === 0 ? "is-head" : undefined}>
                  {r.map((c, k) => (
                    <td key={k}>{c}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        ),
      )}
    </>
  );
}
