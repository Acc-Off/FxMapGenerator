import { type ReactNode, useEffect, useMemo, useState } from "react";
import { Button, Info, type Reason, TextField } from "../../shared/controls";
import { type Lang, type MessageKey, useI18nStore, useT } from "../../shared/i18n";
import type { Names, StyleItem, StyleRebuild, StyleSchema, StyleUnit, StyleValues } from "../../shared/types";
import { makesJapanese } from "../../shared/types";
import { useProjectStore } from "../../project/store";
import { changedFromBase, matches, nameOf, setAt, shown, unitScale, valueAt } from "./schema";
import { FOCUS_MS, useFocusStore } from "./focus";
import { ValueEditor } from "./StyleControls";
import { TableEditor } from "./StyleTables";

/** The groups closed (kept while the app is open). */
const closed = new Set<string>();

/** The rebuilds a row shows as tags: every row redraws the cells, so that one is no tag. */
const TAGS: readonly (readonly [StyleRebuild, MessageKey])[] = [
  ["cells.prep", "row.cells.prep"],
  ["mapData.regions", "row.mapData.regions"],
  ["mapData.labels", "row.mapData.labels"],
];

const TAG_HELP = {
  "cells.prep": "help.styles.tag.prep",
  "mapData.regions": "help.styles.tag.regions",
  "mapData.labels": "help.styles.tag.labels",
} as const;

interface Props {
  schema: StyleSchema;
  values: StyleValues;
  /** The base style's values (null for a bundled style: nothing is marked as changed). */
  base: StyleValues | null;
  /** Why the values cannot be changed (a bundled style), or null. */
  disabled: Reason | null;
  /** A change of the values; `key` names the value changed (quick changes of one value are one undo step). */
  onChange(next: StyleValues, key: string): void;
  /** A row back to the base style's values. */
  onRevert(item: StyleItem): void;
  /** The game's zones' names by code (the table per zone). */
  zoneNames: Record<string, Names>;
}

/**
 * The left of the style editor: a search, then the groups of the style table (each can be closed) with a row per value
 * of the style: its name (help: the row's description), its control, whether it differs from the base style (and a
 * button that takes it back), and the steps of the project screen a change of it makes the maps rebuild.
 */
export function StyleList({ schema, values, base, disabled, onChange, onRevert, zoneNames }: Props) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  // the rows of a language the project makes no maps in are left out (the Japanese font without Japanese maps)
  const japanese = useProjectStore((s) => (s.project ? makesJapanese(s.project.file) : false));
  const [query, setQuery] = useState("");
  const [, redraw] = useState(0);
  const [flash, setFlash] = useState<string | null>(null);
  const focus = useFocusStore((s) => s.target);
  const focusN = useFocusStore((s) => s.n);
  const searching = query.trim().length > 0;
  // a row asked for (from the preview): its group opened, the row brought into view and marked for a moment
  useEffect(() => {
    if (!focus) return;
    setQuery("");
    const group = schema.groups.find((g) => g.items.some((i) => i.key === focus.key));
    if (group) closed.delete(group.id);
    redraw((n) => n + 1);
    setFlash(focus.key);
    const frame = window.requestAnimationFrame(() =>
      document.querySelector(`.style-row[data-style-key="${CSS.escape(focus.key)}"]`)?.scrollIntoView({ block: focus.row ? "start" : "center" }),
    );
    const timer = window.setTimeout(() => setFlash(null), FOCUS_MS);
    return () => {
      window.cancelAnimationFrame(frame);
      window.clearTimeout(timer);
    };
  }, [focusN]); // eslint-disable-line react-hooks/exhaustive-deps
  const groups = useMemo(
    () =>
      schema.groups.map((g) => {
        const items = g.items.filter((i) => shown(i, values) && (!i.language || i.language === "en" || (i.language === "ja" && japanese)));
        return { group: g, items, found: items.filter((i) => matches(i, query)), changed: items.filter((i) => changedFromBase(i, values, base)).length };
      }),
    [schema, values, base, query, japanese],
  );
  const anyFound = groups.some((g) => g.found.length > 0);
  const toggle = (id: string) => {
    if (closed.has(id)) closed.delete(id);
    else closed.add(id);
    redraw((n) => n + 1);
  };
  return (
    <div className="style-list" data-guide="styles.list">
      <div className="style-search">
        <TextField help="help.styles.search" label={t("styles.search")} value={query} onChange={setQuery} placeholder={t("styles.search.placeholder")} wide />
      </div>
      <div className="style-groups">
        {!anyFound && (
          <Info help="help.styles.search" block className="muted style-none">
            {t("styles.search.none")}
          </Info>
        )}
        {groups.map(({ group, items, found, changed }) => {
          if (found.length === 0 || items.length === 0) return null;
          const open = searching || !closed.has(group.id);
          return (
            <section key={group.id} className="style-group">
              <Button help="help.styles.group" variant="row" className="style-group-head" pressed={open} onClick={() => toggle(group.id)}>
                <span className="style-group-arrow">{open ? "▾" : "▸"}</span>
                <span className="style-group-name">{nameOf(group.name, lang)}</span>
                <span className="muted">{searching ? t("styles.group.found", { n: found.length, all: items.length }) : t("styles.group.count", { n: items.length })}</span>
                {changed > 0 && <span className="style-changed">{t("styles.group.changed", { n: changed })}</span>}
              </Button>
              {open && (
                <div className="style-rows">
                  {found.map((item, i) => (
                    <Row
                      key={`${item.key}-${i}`}
                      item={item}
                      flash={flash === item.key}
                      values={values}
                      changed={changedFromBase(item, values, base)}
                      heading={item.heading && !searching ? nameOf(item.heading, lang) : null}
                      disabled={disabled}
                      onSet={(v) => onChange(setAt(values, item.key, v), item.key)}
                      onRevert={base && !disabled ? () => onRevert(item) : null}
                      baseText={base ? valueText(t, item, base, lang) : ""}
                      table={
                        item.control === "table" ? (
                          <TableEditor item={item} values={values} base={base} disabled={disabled} zoneNames={zoneNames} onChange={onChange} />
                        ) : null
                      }
                    />
                  ))}
                </div>
              )}
            </section>
          );
        })}
      </div>
    </div>
  );
}

interface RowProps {
  item: StyleItem;
  /** Marked for a moment (shown from the preview). */
  flash: boolean;
  values: StyleValues;
  changed: boolean;
  heading: string | null;
  disabled: Reason | null;
  onSet(value: unknown): void;
  /** Takes the row back to the base style's value, or null (a bundled style). */
  onRevert: (() => void) | null;
  /** The base style's value as text (the revert button's help). */
  baseText: string;
  /** A table's editor, shown under the row across the list. */
  table: ReactNode;
}

function Row({ item, flash, values, changed, heading, disabled, onSet, onRevert, baseText, table }: RowProps) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  return (
    <>
      {heading && (
        <Info help="help.styles.heading" block className="style-heading">
          {heading}
        </Info>
      )}
      <div className={`style-row${changed ? " is-changed" : ""}${flash ? " is-focus" : ""}`} data-style-key={item.key}>
        <Info help={{ text: nameOf(item.description, lang) }} className="style-row-name">
          {nameOf(item.name, lang)}
        </Info>
        <div className={`style-row-value style-row-${item.control}`}>
          {item.control === "table" ? <ValueView item={item} values={values} lang={lang} /> : <ValueEditor item={item} values={values} disabled={disabled} onSet={onSet} />}
        </div>
        <div className="style-row-marks">
          {changed && (
            <Info help="help.styles.changed" className="style-changed">
              {t("styles.changed")}
            </Info>
          )}
          {changed && onRevert && (
            <Button help={{ text: t("help.styles.revert", { value: baseText }) }} variant="link" className="style-revert" onClick={onRevert}>
              {t("styles.revert")}
            </Button>
          )}
          {TAGS.filter(([id]) => item.rebuilds.includes(id)).map(([id, label]) => (
            <Info key={id} help={TAG_HELP[id as keyof typeof TAG_HELP]} className={`style-tag style-tag-${id.replace(".", "-")}`}>
              {t(label)}
            </Info>
          ))}
        </div>
      </div>
      {table && <div className={`style-table-block${changed ? " is-changed" : ""}`}>{table}</div>}
    </>
  );
}

const UNITS: Record<StyleUnit, string> = { m: "m", m2: "m²", deg: "°", px: "px", em: "em", percent: "%" };

/** A number as a style holds it, with the row's unit (a percent value, 0 to 1 in the style, as 0 to 100 %). */
export function numberText(v: unknown, unit: StyleUnit | undefined): string {
  if (typeof v !== "number") return "—";
  const text = String(Math.round(v * unitScale(unit) * 1e6) / 1e6);
  return unit ? (unit === "deg" ? `${text}${UNITS[unit]}` : `${text} ${UNITS[unit]}`) : text;
}

/** A row's value as plain text (the base style's value in the revert button's help). */
function valueText(t: ReturnType<typeof useT>, item: StyleItem, values: StyleValues, lang: Lang): string {
  const v = valueAt(values, item.key);
  const choiceName = (value: unknown) => nameOf(item.choices?.find((c) => c.value === value)?.name, lang) || String(value ?? "—");
  switch (item.control) {
    case "number":
      return numberText(v, item.unit);
    case "choice":
      return choiceName(v);
    case "check":
      return t(v === true ? "styles.value.on" : "styles.value.off");
    case "checks":
      return Array.isArray(v) && v.length > 0 ? v.map(choiceName).join(lang === "ja" ? "、" : ", ") : t("styles.value.none");
    case "table":
      return tableSummary(t, item.key, values);
    default:
      return typeof v === "string" && v ? v : t("styles.value.none");
  }
}

/** A row's value as text, a colour swatch or a summary of a table (the values are not edited here). */
function ValueView({ item, values, lang }: { item: StyleItem; values: StyleValues; lang: Lang }) {
  const t = useT();
  const v = valueAt(values, item.key);
  const choiceName = (value: unknown) => nameOf(item.choices?.find((c) => c.value === value)?.name, lang) || String(value ?? "—");
  switch (item.control) {
    case "color":
      return (
        <span className="style-color">
          <span className="style-swatch" style={{ background: typeof v === "string" ? v : "transparent" }} />
          <code>{typeof v === "string" ? v : "—"}</code>
        </span>
      );
    case "number":
      return <span className="style-number">{numberText(v, item.unit)}</span>;
    case "choice":
      return <span>{choiceName(v)}</span>;
    case "check":
      return <span>{t(v === true ? "styles.value.on" : "styles.value.off")}</span>;
    case "checks":
      return <span className="style-checks">{Array.isArray(v) && v.length > 0 ? v.map(choiceName).join(lang === "ja" ? "、" : ", ") : t("styles.value.none")}</span>;
    case "font":
      return <span style={{ fontFamily: typeof v === "string" ? `"${v}"` : undefined }}>{typeof v === "string" ? v : "—"}</span>;
    case "text":
      return <span className="style-text">{typeof v === "string" && v ? v : t("styles.value.none")}</span>;
    case "table":
      return <span className="muted">{tableSummary(t, item.key, values)}</span>;
  }
}

/** A table's size in words: lights, depth bands, short forms, zones. */
function tableSummary(t: ReturnType<typeof useT>, key: string, values: StyleValues): string {
  const count = (k: string) => {
    const v = valueAt(values, k);
    return Array.isArray(v) ? v.length : v && typeof v === "object" ? Object.keys(v).length : 0;
  };
  switch (key) {
    case "shade.lights":
      return t("styles.table.lights", { n: count(key) });
    case "sea.bands":
      return t("styles.table.bands", { n: count(key) + 1 });
    case "labels.street.expand":
      return t("styles.table.expand", { n: count(key) });
    case "regions.zones": {
      const zones = new Set<string>();
      for (const k of ["regions.zones", "buildings.byZone", "labels.postal.sizeByZone"]) {
        const v = valueAt(values, k);
        if (v && typeof v === "object") for (const z of Object.keys(v)) zones.add(z);
      }
      return t("styles.table.zones", { n: zones.size });
    }
    default:
      return "";
  }
}
