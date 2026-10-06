import type { MessageKey, Params } from "./i18n";

type T = (key: MessageKey, params?: Params) => string;

/** An estimate range for people, both ends in the same unit (seconds, minutes or hours), like the command line's. */
export function duration(t: T, low: number, high: number): string {
  if (high <= 0) return t("time.zero");
  const [div, key, digits]: [number, MessageKey, number] = high < 90 ? [1, "time.sec", 0] : high < 90 * 60 ? [60, "time.min", 0] : [3600, "time.hour", 1];
  const f = (v: number) => (v / div).toFixed(digits);
  let a = f(Math.max(low, 0));
  let b = f(high);
  if (digits === 0 && a === "0") a = t("time.underOne");
  if (digits === 0 && b === "0") b = t("time.underOne"); // both ends under half a second
  return t(key, { v: a === b ? b : t("time.range", { a, b }) });
}

/** Seconds as m:ss (or s when short). */
export function clock(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

export function gigabytes(bytes: number): string {
  return (bytes / 2 ** 30).toFixed(1);
}

/** Windows paths compare without case and slash differences. */
export function samePath(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return false;
  const n = (p: string) => p.replace(/\//g, "\\").toLowerCase();
  return n(a) === n(b);
}
