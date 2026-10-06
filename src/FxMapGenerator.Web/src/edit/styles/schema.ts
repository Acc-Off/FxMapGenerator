import type { Lang } from "../../shared/i18n";
import { type ItemName, itemName, type StyleItem, type StyleRebuild, type StyleSchema, type StyleUnit, type StyleValues } from "../../shared/types";

/**
 * The value at a place of a style (`paint.roads.road.fill`; a number part indexes a list, `paint.roads.tunnel.dash.0`),
 * or undefined when the style has nothing there.
 */
export function valueAt(values: unknown, key: string): unknown {
  let node: unknown = values;
  for (const part of key.split(".")) {
    if (Array.isArray(node)) node = /^\d+$/.test(part) ? node[Number(part)] : undefined;
    else if (node !== null && typeof node === "object") node = (node as Record<string, unknown>)[part];
    else return undefined;
  }
  return node;
}

/** Whether a row shows for a style (its `when` holds). */
export function shown(item: StyleItem, values: StyleValues): boolean {
  return !item.when || String(valueAt(values, item.when.key)) === item.when.equals;
}

/** A name in the screen's language (English when there is no Japanese one; a project style's one name as it is). */
export function nameOf(n: ItemName | undefined, lang: Lang): string {
  return itemName(n, lang);
}

/** The places a table's columns hold, by the table row's key (the tests know the same). */
const TABLE_PLACES: Record<string, string[]> = {
  "regions.zones": ["regions.zones", "buildings.byZone", "labels.postal.sizeByZone"],
  "sea.bands": ["sea.bands", "paint.sea.sand", "paint.sea.rock", "paint.sea.opacity"],
  "shade.lights": ["shade.lights"],
  "labels.street.expand": ["labels.street.expand"],
};

/** The places a row edits: its key, or a table's places. */
export function placesOf(item: StyleItem): string[] {
  return item.control === "table" ? (TABLE_PLACES[item.key] ?? [item.key]) : [item.key];
}

/** Two JSON values the same (objects key by key, whatever their order). */
export function sameJson(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || b === null || typeof a !== "object" || typeof b !== "object") return a === b || (a === undefined && b === null) || (a === null && b === undefined);
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) {
    const bb = b as unknown[];
    return a.length === bb.length && a.every((x, i) => sameJson(x, bb[i]));
  }
  const ao = a as Record<string, unknown>, bo = b as Record<string, unknown>;
  const keys = new Set([...Object.keys(ao), ...Object.keys(bo)]);
  for (const k of keys) if (!sameJson(ao[k], bo[k])) return false;
  return true;
}

/** Whether a row's value differs from the base style's (always false for a bundled style). */
export function changedFromBase(item: StyleItem, values: StyleValues, base: StyleValues | null): boolean {
  if (!base) return false;
  return placesOf(item).some((p) => !sameJson(valueAt(values, p), valueAt(base, p)));
}

/** Whether a row answers a search: its names, heading, help (both languages) or key hold the words, whatever the case. */
export function matches(item: StyleItem, query: string): boolean {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return true;
  const text = [item.key, item.name.en, item.name.ja, item.heading?.en, item.heading?.ja, item.description.en, item.description.ja]
    .filter(Boolean)
    .join("\n")
    .toLowerCase();
  return words.every((w) => text.includes(w));
}

/** The rows of a group that show for a style. */
export function shownItems(schema: StyleSchema, groupId: string, values: StyleValues): StyleItem[] {
  return schema.groups.find((g) => g.id === groupId)?.items.filter((i) => shown(i, values)) ?? [];
}

/**
 * A copy of a style with the value at a place set (undefined takes the key away): objects on the way are copied (made
 * when missing), a number part sets an item of a list that is there.
 */
export function setAt(values: StyleValues, key: string, value: unknown): StyleValues {
  const parts = key.split(".");
  const set = (node: unknown, i: number): unknown => {
    const part = parts[i];
    if (Array.isArray(node)) {
      const k = Number(part);
      if (!/^\d+$/.test(part) || k >= node.length) return node;
      const copy = node.slice();
      copy[k] = i === parts.length - 1 ? value : set(node[k], i + 1);
      return copy;
    }
    const obj = node !== null && typeof node === "object" ? { ...(node as Record<string, unknown>) } : {};
    if (i === parts.length - 1) {
      if (value === undefined) delete obj[part];
      else obj[part] = value;
    } else obj[part] = set(obj[part], i + 1);
    return obj;
  };
  return set(values, 0) as StyleValues;
}

/** The row's places set to the base style's values (a place the base does not have is taken away). */
export function revertedRow(item: StyleItem, values: StyleValues, base: StyleValues): StyleValues {
  let next = values;
  for (const p of placesOf(item)) next = setAt(next, p, structuredClone(valueAt(base, p)));
  return next;
}

/** The colours a style uses (#rrggbb, small letters), in the order they come in its file. */
export function colorsIn(values: unknown, out: string[] = []): string[] {
  if (typeof values === "string") {
    const c = values.toLowerCase();
    if (/^#[0-9a-f]{6}$/.test(c) && !out.includes(c)) out.push(c);
  } else if (Array.isArray(values)) for (const v of values) colorsIn(v, out);
  else if (values !== null && typeof values === "object") for (const v of Object.values(values)) colorsIn(v, out);
  return out;
}

/** A number put on the row's step (and range), without the float's tail (0.1 + 0.2 stays 0.3). */
export function onStep(v: number, step: number, min?: number, max?: number): number {
  const decimals = Math.max(0, (String(step).split(".")[1] ?? "").length);
  let x = Math.round(v / step) * step;
  if (min !== undefined) x = Math.max(min, x);
  if (max !== undefined) x = Math.min(max, x);
  return Number(x.toFixed(decimals));
}

/** How many times larger the number shown is than the style's: a percent value (0 to 1 in the style) shows as 0 to 100. */
export function unitScale(unit: StyleUnit | undefined): number {
  return unit === "percent" ? 100 : 1;
}

/**
 * A number field's value, range and step as shown (the style's times {@link unitScale}), and the style's value of a
 * number entered, put on the style's step and range.
 */
export function shownNumber(entry: { min?: number; max?: number; step?: number; unit?: StyleUnit }, fallback: { min: number; max: number; step: number }) {
  const scale = unitScale(entry.unit);
  const min = entry.min ?? fallback.min, max = entry.max ?? fallback.max, step = entry.step ?? fallback.step;
  return {
    min: onStep(min * scale, step * scale),
    max: onStep(max * scale, step * scale),
    step: onStep(step * scale, step * scale),
    show: (v: number | null | undefined) => (typeof v === "number" ? onStep(v * scale, step * scale) : null),
    store: (x: number) => onStep(x / scale, step, min, max),
  };
}

/** The columns of a table that hold a place of it (the table's places line up with its columns, or all hold the one place). */
function columnsOf(item: StyleItem, place: string) {
  const places = placesOf(item), columns = item.columns ?? [];
  return places.length === columns.length ? [columns[places.indexOf(place)]] : columns;
}

/**
 * What saving the edits makes the maps drawn with the style rebuild: the rebuilds of the rows (of a table, of the
 * columns) whose values differ from the saved ones.
 */
export function rebuildsOf(schema: StyleSchema, saved: StyleValues, edited: StyleValues): Set<StyleRebuild> {
  const out = new Set<StyleRebuild>();
  for (const g of schema.groups)
    for (const item of g.items) {
      if (!shown(item, edited) && !shown(item, saved)) continue;
      for (const p of placesOf(item)) {
        if (sameJson(valueAt(saved, p), valueAt(edited, p))) continue;
        const marks = item.control === "table" ? columnsOf(item, p).flatMap((c) => c.rebuilds) : item.rebuilds;
        for (const m of marks) out.add(m);
      }
    }
  return out;
}
