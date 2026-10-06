import { type KeyboardEvent, useEffect, useMemo, useState } from "react";
import { Button, HeaderCell, Info, NumberField, type Reason, Select, TextField } from "../../shared/controls";
import { type Lang, useI18nStore, useT } from "../../shared/i18n";
import type { Names, StyleColumn, StyleItem, StyleValues } from "../../shared/types";
import { FOCUS_MS, useFocusStore } from "./focus";
import { ColorBox, UNITS } from "./StyleControls";
import { nameOf, onStep, sameJson, setAt, shownNumber, valueAt } from "./schema";

interface Props {
  item: StyleItem;
  values: StyleValues;
  /** The base style's values (a cell that differs from them is marked), or null for a bundled style. */
  base: StyleValues | null;
  disabled: Reason | null;
  /** The game's zones' names by code (the table per zone). */
  zoneNames: Record<string, Names>;
  /** A change of the values; `key` names what changed (quick changes of one cell are one undo step). */
  onChange(next: StyleValues, key: string): void;
}

/** The editor of one of the style's four tables, chosen by the row's key. */
export function TableEditor(props: Props) {
  switch (props.item.key) {
    case "regions.zones":
      return <ZonesTable {...props} />;
    case "sea.bands":
      return <BandsTable {...props} />;
    case "shade.lights":
      return <LightsTable {...props} />;
    case "labels.street.expand":
      return <ExpandTable {...props} />;
    default:
      return null;
  }
}

function columnOf(item: StyleItem, id: string): StyleColumn | undefined {
  return item.columns?.find((c) => c.id === id);
}

/** A column's heading with its description as help. */
function Head({ column, lang }: { column: StyleColumn | undefined; lang: Lang }) {
  return <HeaderCell help={{ text: nameOf(column?.description, lang) }}>{nameOf(column?.name, lang)}</HeaderCell>;
}

/** The help of a column's cells: its description. */
const cellHelp = (column: StyleColumn | undefined, lang: Lang) => ({ text: nameOf(column?.description, lang) });

/**
 * The cell of a table asked for from the preview (its row and column; null when none): marked for a moment and
 * brought into view; <paramref name="onRow"/> first makes the row shown (a zone's search).
 */
function useFocusCell(key: string, onRow?: (row: string) => void): { row: string; column?: string } | null {
  const focus = useFocusStore((s) => s.target);
  const n = useFocusStore((s) => s.n);
  const [cell, setCell] = useState<{ row: string; column?: string } | null>(null);
  useEffect(() => {
    if (!focus || focus.key !== key || focus.row === undefined) return;
    onRow?.(focus.row);
    setCell({ row: focus.row, column: focus.column });
    // after the list has brought the table's row into view
    const show = window.setTimeout(() => document.querySelector(".style-table .is-focus")?.scrollIntoView({ block: "center" }), 80);
    const timer = window.setTimeout(() => setCell(null), FOCUS_MS);
    return () => {
      window.clearTimeout(show);
      window.clearTimeout(timer);
    };
  }, [n]); // eslint-disable-line react-hooks/exhaustive-deps
  return cell;
}

/** A table cell's classes: changed from the base, marked from the preview. */
function cellClass(changed: boolean, focused: boolean): string | undefined {
  const c = `${changed ? "is-changed" : ""}${focused ? " is-focus" : ""}`.trim();
  return c || undefined;
}

/** A place whose value differs from the base style's. */
function changedAt(values: StyleValues, base: StyleValues | null, place: string): boolean {
  return !!base && !sameJson(valueAt(values, place), valueAt(base, place));
}

// ---------------------------------------------------------------- per zone

const ZONE_PLACES = { region: "regions.zones", building: "buildings.byZone", postal: "labels.postal.sizeByZone" } as const;

/**
 * The table per zone: a row a game zone (its name from the project's game files, its code), its region, its buildings'
 * colour and its postal codes' size; empty cells follow the general rule. A search narrows the rows.
 */
function ZonesTable({ item, values, base, disabled, zoneNames, onChange }: Props) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const [filter, setFilter] = useState("");
  const focus = useFocusCell(item.key, setFilter);
  const name = (z: string) => nameOf(zoneNames[z], lang);
  const codes = useMemo(() => {
    const all = new Set<string>(Object.keys(zoneNames));
    for (const src of [values, base])
      for (const place of Object.values(ZONE_PLACES)) {
        const o = src ? valueAt(src, place) : null;
        if (o && typeof o === "object") for (const z of Object.keys(o)) all.add(z);
      }
    return [...all].sort((a, b) => (name(a) || a).localeCompare(name(b) || b, lang) || a.localeCompare(b));
  }, [values, base, zoneNames, lang]); // eslint-disable-line react-hooks/exhaustive-deps
  const words = filter.trim().toLowerCase();
  const shown = words ? codes.filter((z) => [z, zoneNames[z]?.en, zoneNames[z]?.ja].some((s) => s?.toLowerCase().includes(words))) : codes;
  const region = columnOf(item, "region"), building = columnOf(item, "building"), postal = columnOf(item, "postal");
  const regionOptions = [{ value: "", label: t("styles.zones.noRegion") }, ...(region?.choices ?? []).map((c) => ({ value: c.value, label: nameOf(c.name, lang) }))];
  const buildingOptions = [{ value: "", label: t("styles.zones.noBuilding") }, ...(building?.choices ?? []).map((c) => ({ value: c.value, label: nameOf(c.name, lang) }))];
  const general = valueAt(values, "labels.postal.size");
  const set = (place: string, v: unknown) => onChange(setAt(values, place, v), place);
  const text = (place: string) => {
    const v = valueAt(values, place);
    return typeof v === "string" ? v : "";
  };
  return (
    <div className="style-table">
      <div className="style-table-tools">
        <TextField help="help.styles.zones.filter" label="" value={filter} onChange={setFilter} placeholder={t("styles.zones.filter")} />
        <Info help="help.styles.zones.count" className="muted">
          {t("styles.zones.count", { n: shown.length, all: codes.length })}
        </Info>
      </div>
      <div className="style-table-scroll">
        <table className="style-table-grid">
          <thead>
            <tr>
              <HeaderCell help="help.styles.zones.zone">{t("styles.zones.zone")}</HeaderCell>
              <Head column={region} lang={lang} />
              <Head column={building} lang={lang} />
              <Head column={postal} lang={lang} />
            </tr>
          </thead>
          <tbody>
            {shown.map((z) => {
              const [pr, pb, pp] = [`${ZONE_PLACES.region}.${z}`, `${ZONE_PLACES.building}.${z}`, `${ZONE_PLACES.postal}.${z}`];
              const size = valueAt(values, pp);
              // a value the table does not list (a hand-made file) stays shown as it is
              const withOwn = (options: { value: string; label: string }[], v: string) => (v && !options.some((o) => o.value === v) ? [...options, { value: v, label: v }] : options);
              return (
                <tr key={z}>
                  <td className="style-zone-name">
                    {name(z) && <span>{name(z)}</span>}
                    <code className="muted">{z}</code>
                  </td>
                  <td className={cellClass(changedAt(values, base, pr), focus?.row === z && focus.column === "region")}>
                    <Select help={cellHelp(region, lang)} value={text(pr)} options={withOwn(regionOptions, text(pr))} disabledReason={disabled} onChange={(v) => set(pr, v || undefined)} />
                  </td>
                  <td className={cellClass(changedAt(values, base, pb), focus?.row === z && focus.column === "building")}>
                    <Select help={cellHelp(building, lang)} value={text(pb)} options={withOwn(buildingOptions, text(pb))} disabledReason={disabled} onChange={(v) => set(pb, v || undefined)} />
                  </td>
                  <td className={changedAt(values, base, pp) ? "is-changed" : undefined}>
                    <NumberField
                      help={cellHelp(postal, lang)}
                      label=""
                      value={typeof size === "number" ? size : null}
                      min={postal?.min}
                      max={postal?.max}
                      step={postal?.step}
                      allowEmpty
                      placeholder={t("styles.zones.postalDefault", { size: typeof general === "number" ? general : "—" })}
                      unit={postal?.unit ? UNITS[postal.unit] : undefined}
                      disabledReason={disabled}
                      onChange={(n) => set(pp, n === null ? undefined : onStep(n, postal?.step ?? 0.01, postal?.min, postal?.max))}
                    />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- depth bands

/**
 * The depth bands: a row a band (from the depth before it to its own; the last one on down), its colour over a sand bed
 * and over a rock bed, and its opacity. A band splits into two (both starting with its colours and opacity) or merges
 * into the next (the last one into the band before); the bands' depths, both colour lists and the opacities change together.
 */
function BandsTable({ item, values, base, disabled, onChange }: Props) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const bands = numbers(valueAt(values, "sea.bands"));
  const sand = texts(valueAt(values, "paint.sea.sand"));
  const rock = texts(valueAt(values, "paint.sea.rock"));
  const n = bands.length;
  // a style without opacities draws every band opaque
  const opacityRaw = numbers(valueAt(values, "paint.sea.opacity"));
  const opacity = Array.from({ length: n + 1 }, (_, i) => opacityRaw[i] ?? 1);
  const depth = columnOf(item, "depth"), sandCol = columnOf(item, "sand"), rockCol = columnOf(item, "rock"), opacityCol = columnOf(item, "opacity");
  const step = depth?.step ?? 0.05;
  // a cell is compared with the base's only while there are as many bands (else the rows do not line up)
  const lined = base && numbers(valueAt(base, "sea.bands")).length === n ? base : null;
  const focus = useFocusCell(item.key);
  const focused = (i: number, column: string) => focus?.row === String(i) && focus.column === column;
  const write = (b: number[], s: string[], r: string[], o: number[]) => {
    let next = setAt(values, "sea.bands", b);
    next = setAt(next, "paint.sea.sand", s);
    next = setAt(next, "paint.sea.rock", r);
    next = setAt(next, "paint.sea.opacity", o);
    onChange(next, `sea.bands:${Date.now()}`);
  };
  /** Band i as two: a new depth in its middle (the last band: at twice where it starts). */
  const split = (i: number) => {
    const lo = i === 0 ? 0 : bands[i - 1];
    const mid = i < n ? onStep((lo + bands[i]) / 2, step) : onStep(Math.max(lo * 2, lo + 1), step, undefined, depth?.max);
    const b = [...bands], s = [...sand], r = [...rock], o = [...opacity];
    b.splice(i, 0, mid);
    s.splice(i, 0, sand[i]);
    r.splice(i, 0, rock[i]);
    o.splice(i, 0, opacity[i]);
    write(b, s, r, o);
  };
  /** Band i merged into the next one (the last band into the one before it). */
  const merge = (i: number) => {
    const b = [...bands], s = [...sand], r = [...rock], o = [...opacity];
    if (i < n) {
      b.splice(i, 1);
      s.splice(i, 1);
      r.splice(i, 1);
      o.splice(i, 1);
    } else {
      b.splice(n - 1, 1);
      s.splice(n, 1);
      r.splice(n, 1);
      o.splice(n, 1);
    }
    write(b, s, r, o);
  };
  const thin = (i: number) => {
    const lo = i === 0 ? 0 : bands[i - 1];
    return i < n && bands[i] - lo < 2 * step - 1e-9;
  };
  const fmt = (v: number) => String(Math.round(v * 1000) / 1000);
  // the opacities shown on the column's scale (0 to 100 %), stored as 0 to 1
  const opacityShown = shownNumber(opacityCol ?? {}, { min: 0, max: 1, step: 0.01 });
  return (
    <div className="style-table">
      <table className="style-table-grid">
        <thead>
          <tr>
            <Head column={depth} lang={lang} />
            <Head column={sandCol} lang={lang} />
            <Head column={rockCol} lang={lang} />
            <Head column={opacityCol} lang={lang} />
            <HeaderCell help="help.styles.table.actions" className="style-table-actions" />
          </tr>
        </thead>
        <tbody>
          {Array.from({ length: n + 1 }, (_, i) => {
            const lo = i === 0 ? 0 : bands[i - 1];
            return (
              <tr key={i}>
                <td className={i < n && changedAt(values, lined, `sea.bands.${i}`) ? "is-changed" : undefined}>
                  {i < n ? (
                    <span className="style-band-depth">
                      <span className="muted">{t("styles.bands.from", { from: fmt(lo) })}</span>
                      <NumberField
                        help={cellHelp(depth, lang)}
                        label=""
                        value={bands[i]}
                        min={onStep(lo + step, step)}
                        max={i + 1 < n ? onStep(bands[i + 1] - step, step) : depth?.max}
                        step={step}
                        unit={depth?.unit ? UNITS[depth.unit] : undefined}
                        disabledReason={disabled}
                        onChange={(v) => {
                          if (v !== null) onChange(setAt(values, `sea.bands.${i}`, onStep(v, step)), `sea.bands.${i}`);
                        }}
                      />
                    </span>
                  ) : (
                    <span className="muted">{t("styles.bands.deeper", { from: fmt(lo) })}</span>
                  )}
                </td>
                <td className={cellClass(changedAt(values, lined, `paint.sea.sand.${i}`), focused(i, "sand"))}>
                  <ColorBox value={sand[i] ?? null} values={values} disabled={disabled} help={cellHelp(sandCol, lang)} onSet={(c) => onChange(setAt(values, `paint.sea.sand.${i}`, c), `paint.sea.sand.${i}`)} />
                </td>
                <td className={cellClass(changedAt(values, lined, `paint.sea.rock.${i}`), focused(i, "rock"))}>
                  <ColorBox value={rock[i] ?? null} values={values} disabled={disabled} help={cellHelp(rockCol, lang)} onSet={(c) => onChange(setAt(values, `paint.sea.rock.${i}`, c), `paint.sea.rock.${i}`)} />
                </td>
                <td className={cellClass(changedAt(values, lined, `paint.sea.opacity.${i}`), focused(i, "opacity"))}>
                  <NumberField
                    help={cellHelp(opacityCol, lang)}
                    label=""
                    value={opacityShown.show(opacity[i])}
                    min={opacityShown.min}
                    max={opacityShown.max}
                    step={opacityShown.step}
                    unit={opacityCol?.unit ? UNITS[opacityCol.unit] : undefined}
                    disabledReason={disabled}
                    onChange={(v) => {
                      if (v === null) return;
                      // a style without opacities gets the whole list with the first change
                      const o = [...opacity];
                      o[i] = opacityShown.store(v);
                      onChange(setAt(values, "paint.sea.opacity", o), `paint.sea.opacity.${i}`);
                    }}
                  />
                </td>
                <td className="style-table-actions">
                  <Button help="help.styles.bands.add" variant="link" disabledReason={disabled ?? (thin(i) ? "reason.styles.bands.thin" : null)} onClick={() => split(i)}>
                    {t("styles.bands.add")}
                  </Button>
                  <Button help="help.styles.bands.remove" variant="link" disabledReason={disabled ?? (n <= 1 ? "reason.styles.bands.last" : null)} onClick={() => merge(i)}>
                    {t("styles.bands.remove")}
                  </Button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function numbers(v: unknown): number[] {
  return Array.isArray(v) ? v.filter((x): x is number => typeof x === "number") : [];
}

function texts(v: unknown): string[] {
  return Array.isArray(v) ? v.map((x) => (typeof x === "string" ? x : "#000000")) : [];
}

// ---------------------------------------------------------------- lights

interface Light {
  azimuth: number;
  weight: number;
}

/** The lights: a row a light (the direction it comes from, its weight), with a small drawing of them; at least one. */
function LightsTable({ item, values, base, disabled, onChange }: Props) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const raw = valueAt(values, "shade.lights");
  const lights: Light[] = Array.isArray(raw) ? raw.map((l) => ({ azimuth: Number((l as Light)?.azimuth ?? 0), weight: Number((l as Light)?.weight ?? 0) })) : [];
  const azimuth = columnOf(item, "azimuth"), weight = columnOf(item, "weight");
  const baseLights = base ? valueAt(base, "shade.lights") : null;
  const lined = base && Array.isArray(baseLights) && baseLights.length === lights.length ? base : null;
  const setList = (next: Light[]) => onChange(setAt(values, "shade.lights", next), `shade.lights:${Date.now()}`);
  const setCell = (i: number, field: keyof Light, v: number) => onChange(setAt(values, `shade.lights.${i}.${field}`, v), `shade.lights.${i}.${field}`);
  const most = Math.max(0.0001, ...lights.map((l) => l.weight));
  return (
    <div className="style-table style-table-lights">
      <table className="style-table-grid">
        <thead>
          <tr>
            <Head column={azimuth} lang={lang} />
            <Head column={weight} lang={lang} />
            <HeaderCell help="help.styles.table.actions" className="style-table-actions" />
          </tr>
        </thead>
        <tbody>
          {lights.map((l, i) => (
            <tr key={i}>
              <td className={changedAt(values, lined, `shade.lights.${i}.azimuth`) ? "is-changed" : undefined}>
                <NumberField
                  help={cellHelp(azimuth, lang)}
                  label=""
                  value={l.azimuth}
                  min={azimuth?.min}
                  max={azimuth?.max}
                  step={azimuth?.step}
                  unit={azimuth?.unit ? UNITS[azimuth.unit] : undefined}
                  disabledReason={disabled}
                  onChange={(v) => v !== null && setCell(i, "azimuth", onStep(v, azimuth?.step ?? 1, azimuth?.min, azimuth?.max))}
                />
              </td>
              <td className={changedAt(values, lined, `shade.lights.${i}.weight`) ? "is-changed" : undefined}>
                <NumberField
                  help={cellHelp(weight, lang)}
                  label=""
                  value={l.weight}
                  min={weight?.min}
                  max={weight?.max}
                  step={weight?.step}
                  disabledReason={disabled}
                  onChange={(v) => v !== null && setCell(i, "weight", onStep(v, weight?.step ?? 0.05, weight?.min, weight?.max))}
                />
              </td>
              <td className="style-table-actions">
                <Button help="help.styles.lights.remove" variant="link" disabledReason={disabled ?? (lights.length <= 1 ? "reason.styles.lights.last" : null)} onClick={() => setList(lights.filter((_, j) => j !== i))}>
                  {t("styles.lights.remove")}
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="style-table-foot">
        <Button
          help="help.styles.lights.add"
          disabledReason={disabled}
          onClick={() => setList([...lights, { azimuth: lights.length > 0 ? (lights[lights.length - 1].azimuth + 90) % 360 : 315, weight: 0.2 }])}
        >
          {t("styles.lights.add")}
        </Button>
        <Info help="help.styles.lights.compass" className="style-compass">
          <svg viewBox="-32 -32 64 64" width="64" height="64" aria-hidden="true">
            <circle r="28" className="style-compass-ring" />
            <text y="-18" textAnchor="middle" className="style-compass-north">N</text>
            {lights.map((l, i) => {
              const a = (l.azimuth * Math.PI) / 180, len = 26 * (l.weight / most);
              return <line key={i} x1="0" y1="0" x2={Math.sin(a) * len} y2={-Math.cos(a) * len} className="style-compass-light" />;
            })}
          </svg>
        </Info>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- short forms

/**
 * The short forms written out: a row a short form (renamed when the field is left; never empty, never twice) and the word
 * it is written out as, in the order they are tried. A new row goes at the end.
 */
function ExpandTable({ item, values, base, disabled, onChange }: Props) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const raw = valueAt(values, "labels.street.expand");
  const entries: [string, string][] = raw && typeof raw === "object" ? Object.entries(raw as Record<string, unknown>).map(([k, v]) => [k, typeof v === "string" ? v : ""]) : [];
  const short = columnOf(item, "short"), long = columnOf(item, "long");
  const write = (next: [string, string][], key: string) => onChange(setAt(values, "labels.street.expand", Object.fromEntries(next)), key);
  const baseRaw = base ? valueAt(base, "labels.street.expand") : null;
  const baseEntries = baseRaw && typeof baseRaw === "object" ? (baseRaw as Record<string, unknown>) : null;
  const hasEmpty = entries.some(([k]) => k === "");
  return (
    <div className="style-table">
      <table className="style-table-grid">
        <thead>
          <tr>
            <Head column={short} lang={lang} />
            <Head column={long} lang={lang} />
            <HeaderCell help="help.styles.table.actions" className="style-table-actions" />
          </tr>
        </thead>
        <tbody>
          {entries.map(([k, v], i) => (
            <tr key={`${i}-${k}`}>
              <td className={baseEntries && !(k in baseEntries) ? "is-changed" : undefined}>
                <ShortCell
                  value={k}
                  others={entries.filter((_, j) => j !== i).map(([x]) => x)}
                  help={cellHelp(short, lang)}
                  disabled={disabled}
                  onRename={(next) => write(entries.map(([x, y], j) => (j === i ? [next, y] : [x, y])), `expand:${Date.now()}`)}
                />
              </td>
              <td className={baseEntries && baseEntries[k] !== v ? "is-changed" : undefined}>
                {disabled ? (
                  <span>{v}</span>
                ) : (
                  <TextField help={cellHelp(long, lang)} label="" value={v} onChange={(x) => write(entries.map(([a, b], j) => (j === i ? [a, x] : [a, b])), `expand.${i}`)} />
                )}
              </td>
              <td className="style-table-actions">
                <Button help="help.styles.expand.remove" variant="link" disabledReason={disabled} onClick={() => write(entries.filter((_, j) => j !== i), `expand:${Date.now()}`)}>
                  {t("styles.expand.remove")}
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="style-table-foot">
        <Button help="help.styles.expand.add" disabledReason={disabled ?? (hasEmpty ? "reason.styles.expand.empty" : null)} onClick={() => write([...entries, ["", ""]], `expand:${Date.now()}`)}>
          {t("styles.expand.add")}
        </Button>
      </div>
    </div>
  );
}

/** A short form: typed freely, taken when the field is left or Enter is pressed; empty or a short form there already is refused. */
function ShortCell({ value, others, help, disabled, onRename }: { value: string; others: string[]; help: { text: string }; disabled: Reason | null; onRename(v: string): void }) {
  const t = useT();
  const [draft, setDraft] = useState<string | null>(null);
  const text = draft ?? value;
  const problem = draft === null ? (value === "" ? "styles.expand.noShort" : null) : draft.trim() === "" ? "styles.expand.noShort" : others.includes(draft.trim()) ? "styles.expand.duplicate" : null;
  const commit = () => {
    if (draft === null) return;
    const next = draft.trim();
    if (next && !others.includes(next) && next !== value) onRename(next);
    if (next && !others.includes(next)) setDraft(null);
  };
  if (disabled) return <span>{value}</span>;
  return (
    <span className="style-short" onBlur={commit} onKeyDown={(e: KeyboardEvent) => e.key === "Enter" && commit()}>
      <TextField help={help} label="" value={text} onChange={setDraft} />
      {problem && (
        <Info help="help.styles.expand.problem" block className="text-error style-short-problem">
          {t(problem)}
        </Info>
      )}
    </span>
  );
}
