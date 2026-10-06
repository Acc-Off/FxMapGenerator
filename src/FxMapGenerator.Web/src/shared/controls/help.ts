import type { MessageKey, Params } from "../i18n";

/** Hover help of one screen item (see locales/ja.ts). */
export type HelpKey = Extract<MessageKey, `help.${string}`>;
/** A help text of its own instead of a dictionary key (the style editor's rows take theirs from the style table). */
export interface HelpText {
  text: string;
}
/** The hover help an item carries: a dictionary key, or a text of its own. */
export type Help = HelpKey | HelpText;
/** Why an item cannot be used right now; shown under its help while it is disabled. */
export type ReasonKey = Extract<MessageKey, `reason.${string}`>;
/** A reason, optionally with values for its {placeholders}. */
export type Reason = ReasonKey | { key: ReasonKey; params: Params };

/** Attributes the help layer reads. Every control spreads these onto its element. */
export function helpAttributes(help: Help, reason?: Reason | null): Record<string, string> {
  const own: Record<string, string> = typeof help === "string" ? { "data-help": help } : { "data-help": "", "data-help-text": help.text };
  if (!reason) return own;
  if (typeof reason === "string") return { ...own, "data-help-reason": reason };
  return { ...own, "data-help-reason": reason.key, "data-help-reason-params": JSON.stringify(reason.params) };
}
