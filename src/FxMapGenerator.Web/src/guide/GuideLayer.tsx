import { driver, type Driver } from "driver.js";
import "driver.js/dist/driver.css";
import { useEffect } from "react";
import { create } from "zustand";
import { useEditKind } from "../edit/EditScreen";
import { useProjectStore } from "../project/store";
import { Button } from "../shared/controls";
import { type MessageKey, type Params, t, useT } from "../shared/i18n";
import { useAppStore } from "../shared/store";
import type { GuideAnswer } from "../shared/types";
import "./guide.css";
import { type GuideId, GUIDES, present, selectorOf } from "./guides";

/** How long the screen must have shown what its guide needs before it asks (a screen passed through asks nothing). */
const ASK_AFTER_MS = 800;
/** How often the screen is looked at, and a guide's frame placed again (the screens move under it: rows, lists). */
const TICK_MS = 500;

interface GuideState {
  /** The guide of the screen shown. */
  id: GuideId;
  /** Whether any step of it has its item on the screen (the header's button can show it). */
  available: boolean;
  /** The guide asked about, while the question is shown. */
  asking: GuideId | null;
  /** The guide shown. */
  running: GuideId | null;
}

export const useGuideStore = create<GuideState>(() => ({ id: "start", available: false, asking: null, running: null }));

/** Guides answered "later", or left without an answer: asked about again once the page is opened again. */
const later = new Set<GuideId>();
/** Since when the screen has shown what its guide needs (null: not yet). */
let readySince: number | null = null;
let shown: Driver | null = null;

/** The guide of the screen shown: the start screen, a project's tab, or the editing tab's kind. */
function currentGuide(): GuideId {
  const { project, screen } = useProjectStore.getState();
  if (!project) return "start";
  return screen === "edit" ? useEditKind.getState().kind : screen;
}

/** The screen's name as the question writes it. */
function screenName(tr: (key: MessageKey, params?: Params) => string, id: GuideId): string {
  if (id === "start") return tr("guide.screen.start");
  if (id === "roads" || id === "poi" || id === "styles") return tr("guide.screen.edit", { screen: tr("screen.edit"), kind: tr(`edit.${id}`) });
  return tr(`screen.${id}`);
}

/** Keeps an answer about a guide in the app's settings (an earlier answer stays unless replace). */
function answer(id: GuideId, value: GuideAnswer, replace: boolean): void {
  const { settings, saveSettings } = useAppStore.getState();
  if (!settings || (!replace && settings.guides[id])) return;
  void saveSettings({ ...settings, guides: { ...settings.guides, [id]: value } }).catch(() => undefined);
}

/** The box takes its title and text as HTML: the dictionaries' texts go in as text (a line break stays one). */
function html(text: string): string {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/\n/g, "<br>");
}

/**
 * Shows a guide: the steps whose items are on the screen, one by one, in a box beside each (a closed box counts as
 * seen). It only explains: the item lit and the rest of the page cannot be pressed meanwhile, the screens' keys and
 * the hover help wait (they look for the page's driver-active mark).
 */
export function startGuide(id: GuideId): void {
  const guide = GUIDES[id];
  if (!guide || shown) return;
  const steps = guide.steps.filter(present);
  if (steps.length === 0) return;
  let follow = 0;
  const d = driver({
    steps: steps.map((s) => ({
      element: selectorOf(s),
      popover: { title: html(t(`guide.${s.name}.title`)), description: html(t(`guide.${s.name}.text`)), side: s.side },
    })),
    popoverClass: "guide-popover",
    overlayOpacity: 0.55,
    stagePadding: 6,
    stageRadius: 8,
    smoothScroll: true,
    disableActiveInteraction: true,
    skipMissingElement: true,
    showProgress: true,
    nextBtnText: t("guide.next"),
    prevBtnText: t("guide.back"),
    doneBtnText: t("guide.done"),
    onPopoverRender: (popover, { state }) => {
      popover.progress.textContent = t("guide.progress", { current: (state.activeIndex ?? 0) + 1, total: steps.length });
      popover.closeButton.setAttribute("aria-label", t("guide.close"));
    },
    onDestroyed: () => {
      window.clearInterval(follow);
      shown = null;
      useGuideStore.setState({ running: null });
      answer(id, "seen", false);
    },
  });
  shown = d;
  useGuideStore.setState({ asking: null, running: id });
  follow = window.setInterval(() => d.isActive() && d.refresh(), TICK_MS);
  d.drive();
}

/** Looks at the screen: which guide it has, whether its items are there, and whether to ask about it. */
function tick(): void {
  const id = currentGuide();
  const before = useGuideStore.getState();
  if (id !== before.id) {
    if (before.asking) later.add(before.asking);
    if (shown) shown.destroy();
    readySince = null;
    useGuideStore.setState({ id, asking: null });
  }
  const s = useGuideStore.getState();
  const guide = GUIDES[id];
  const available = !!guide && guide.steps.some(present);
  if (available !== s.available) useGuideStore.setState({ available });
  const settings = useAppStore.getState().settings;
  if (!guide || !settings || settings.guides[id] || later.has(id) || s.asking || s.running) return;
  const ready = present(guide.steps[0]) && (guide.ready?.() ?? true) && !document.querySelector(".modal-backdrop");
  if (!ready) {
    readySince = null;
    return;
  }
  readySince ??= Date.now();
  if (Date.now() - readySince >= ASK_AFTER_MS) useGuideStore.setState({ asking: id });
}

/**
 * The guides' part of the page: it watches which screen is shown and, the first time a screen with a guide is opened on
 * this PC (no answer in the app's settings), asks in a small box whether to show it: show, later (asked again when the
 * page is opened again; not kept), don't ask again. The box is no window: the screen stays usable.
 */
export function GuideLayer() {
  const tr = useT();
  const asking = useGuideStore((s) => s.asking);
  useEffect(() => {
    const timer = window.setInterval(tick, TICK_MS);
    // a tab or kind chosen is looked at as soon as it is drawn
    const soon = () => window.requestAnimationFrame(tick);
    const offProject = useProjectStore.subscribe((s, p) => {
      if (s.screen !== p.screen || !s.project !== !p.project) soon();
    });
    const offKind = useEditKind.subscribe(soon);
    tick();
    return () => {
      window.clearInterval(timer);
      offProject();
      offKind();
    };
  }, []);

  if (!asking) return null;
  const question = tr("guide.ask", { screen: screenName(tr, asking) });
  const close = () => useGuideStore.setState({ asking: null });
  return (
    <div className="guide-ask" role="dialog" aria-label={question}>
      <div className="guide-ask-text">{question}</div>
      <div className="guide-ask-buttons">
        <Button help="help.guide.show" variant="primary" onClick={() => startGuide(asking)}>
          {tr("guide.ask.show")}
        </Button>
        <Button
          help="help.guide.later"
          onClick={() => {
            later.add(asking);
            close();
          }}
        >
          {tr("guide.ask.later")}
        </Button>
        <Button
          help="help.guide.never"
          variant="link"
          onClick={() => {
            answer(asking, "never", true);
            close();
          }}
        >
          {tr("guide.ask.never")}
        </Button>
      </div>
      <div className="guide-ask-note muted">{tr("guide.ask.note")}</div>
    </div>
  );
}

/** The header's button: the guide of the screen shown, at any time (whatever was answered). */
export function GuideButton() {
  const tr = useT();
  const available = useGuideStore((s) => s.available);
  return (
    <Button help="help.guide.open" disabledReason={available ? null : "reason.guide.none"} onClick={() => startGuide(currentGuide())}>
      {tr("guide.open")}
    </Button>
  );
}
