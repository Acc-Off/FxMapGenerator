import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Check, type Help, NumberField, type Reason, Select, Slider, TextField } from "../../shared/controls";
import { type Lang, useI18nStore, useT } from "../../shared/i18n";
import type { StyleItem, StyleUnit, StyleValues } from "../../shared/types";
import { ColorPicker } from "./ColorPicker";
import { colorsIn, nameOf, shownNumber, valueAt } from "./schema";
import { useStyleEditor } from "./store";

export const UNITS: Record<StyleUnit, string> = { m: "m", m2: "m²", deg: "°", px: "px", em: "em", percent: "%" };
/** The unit's place on a row without a unit (a no-break space), so the fields of the rows line up. */
const NO_UNIT = String.fromCharCode(0xa0);

/** Choices up to this many are buttons side by side; more are a list. */
const MAX_BUTTONS = 3;

interface Props {
  item: StyleItem;
  values: StyleValues;
  /** Why the value cannot be changed (a bundled style), or null. */
  disabled: Reason | null;
  /** Sets the row's value (undefined takes it away). */
  onSet(value: unknown): void;
}

/** The control of one row of the style editor, as its kind of value asks. */
export function ValueEditor(props: Props) {
  const lang = useI18nStore((s) => s.lang);
  switch (props.item.control) {
    case "color":
      return <ColorEdit {...props} lang={lang} />;
    case "number":
      return <NumberEdit {...props} lang={lang} />;
    case "choice":
      return <ChoiceEdit {...props} lang={lang} />;
    case "check":
      return <CheckEdit {...props} lang={lang} />;
    case "checks":
      return <ChecksEdit {...props} lang={lang} />;
    case "font":
      return <FontEdit {...props} lang={lang} />;
    case "text":
      return <TextEdit {...props} lang={lang} />;
    case "table":
      return null;
  }
}

type EditProps = Props & { lang: Lang };

function ColorEdit({ item, values, disabled, onSet, lang }: EditProps) {
  const v = valueAt(values, item.key);
  return <ColorBox value={typeof v === "string" ? v : null} values={values} disabled={disabled} help={{ text: nameOf(item.description, lang) }} onSet={onSet} />;
}

/** A colour box that opens the colour picker (the rows' colours and the tables' colour cells). */
export function ColorBox({ value, values, disabled, help, onSet }: { value: string | null; values: StyleValues; disabled: Reason | null; help: Help; onSet(hex: string): void }) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLSpanElement>(null);
  const hex = value ?? "#000000";
  const used = useMemo(() => (open ? colorsIn(values) : []), [open, values]);
  return (
    <span className="style-color" ref={anchor}>
      <Button help={help} className="style-swatch-btn" disabledReason={disabled} pressed={open} onClick={() => setOpen(!open)}>
        <span className="style-swatch" style={{ background: hex }} />
      </Button>
      <code>{value ?? "—"}</code>
      {open && anchor.current && <ColorPicker value={hex} onChange={onSet} onClose={() => setOpen(false)} anchor={anchor.current} used={used} help={help} />}
    </span>
  );
}

function NumberEdit({ item, values, disabled, onSet, lang }: EditProps) {
  const v = valueAt(values, item.key);
  // shown on the row's scale (a percent value as 0 to 100), stored as the style holds it
  const shown = shownNumber(item, { min: 0, max: 100, step: 1 });
  const n = shown.show(typeof v === "number" ? v : null);
  const help = { text: nameOf(item.description, lang) };
  return (
    <span className="style-number-edit">
      <Slider help={help} value={n ?? shown.min} min={shown.min} max={shown.max} step={shown.step} display="" disabledReason={disabled} onChange={(x) => onSet(shown.store(x))} />
      <NumberField
        help={help}
        label=""
        value={n}
        min={shown.min}
        max={shown.max}
        step={shown.step}
        unit={item.unit ? UNITS[item.unit] : NO_UNIT}
        disabledReason={disabled}
        onChange={(x) => {
          if (x !== null) onSet(shown.store(x));
        }}
      />
    </span>
  );
}

function ChoiceEdit({ item, values, disabled, onSet, lang }: EditProps) {
  const v = valueAt(values, item.key);
  const choices = item.choices ?? [];
  const help = { text: nameOf(item.description, lang) };
  if (choices.length <= MAX_BUTTONS)
    return (
      <span className="style-choice-buttons">
        {choices.map((c) => (
          <Button key={c.value} help={help} pressed={v === c.value} disabledReason={disabled} onClick={() => onSet(c.value)}>
            {nameOf(c.name, lang)}
          </Button>
        ))}
      </span>
    );
  const options = choices.map((c) => ({ value: c.value, label: nameOf(c.name, lang) }));
  // a value the table does not list (a hand-made file) stays shown as it is
  if (typeof v === "string" && !choices.some((c) => c.value === v)) options.unshift({ value: v, label: v });
  return <Select help={help} value={typeof v === "string" ? v : ""} options={options} disabledReason={disabled} onChange={(x) => onSet(x)} className="style-choice-list" />;
}

function CheckEdit({ item, values, disabled, onSet, lang }: EditProps) {
  const t = useT();
  const v = valueAt(values, item.key) === true;
  return (
    <Check help={{ text: nameOf(item.description, lang) }} checked={v} disabledReason={disabled} onChange={(on) => onSet(on)}>
      {t(v ? "styles.value.on" : "styles.value.off")}
    </Check>
  );
}

function ChecksEdit({ item, values, disabled, onSet, lang }: EditProps) {
  const v = valueAt(values, item.key);
  const chosen = Array.isArray(v) ? (v as unknown[]).filter((x): x is string => typeof x === "string") : [];
  const help = { text: nameOf(item.description, lang) };
  const choices = item.choices ?? [];
  return (
    <span className="style-checks-edit">
      {choices.map((c) => (
        <Check
          key={c.value}
          help={help}
          checked={chosen.includes(c.value)}
          disabledReason={disabled}
          onChange={(on) => {
            // the table's order, whatever order they were ticked in
            const next = choices.map((x) => x.value).filter((x) => (x === c.value ? on : chosen.includes(x)));
            onSet(next);
          }}
        >
          {nameOf(c.name, lang)}
        </Check>
      ))}
    </span>
  );
}

function FontEdit({ item, values, disabled, onSet, lang }: EditProps) {
  const t = useT();
  const fonts = useStyleEditor((s) => s.fonts);
  useEffect(() => {
    void useStyleEditor.getState().loadFonts();
  }, []);
  const v = valueAt(values, item.key);
  const name = typeof v === "string" ? v : "";
  const options = (fonts ?? []).map((f) => ({ value: f, label: f }));
  // a font this PC does not have stays chosen, and says so (the drawing then takes the default font)
  if (name && fonts && !fonts.some((f) => f.toLowerCase() === name.toLowerCase())) options.unshift({ value: name, label: t("styles.font.missing", { name }) });
  if (!fonts && name) options.push({ value: name, label: name });
  const sample = item.key.endsWith(".ja") ? t("styles.font.sampleJa") : t("styles.font.sampleEn");
  return (
    <span className="style-font-edit">
      <Select help={{ text: nameOf(item.description, lang) }} value={name} options={options} disabledReason={disabled} onChange={(x) => onSet(x)} className="style-choice-list" />
      <span className="style-font-sample" style={{ fontFamily: name ? `"${name}"` : undefined }}>
        {sample}
      </span>
    </span>
  );
}

function TextEdit({ item, values, disabled, onSet, lang }: EditProps) {
  const t = useT();
  const v = valueAt(values, item.key);
  const help = { text: nameOf(item.description, lang) };
  if (disabled)
    return <span className="style-text">{typeof v === "string" && v ? v : t("styles.value.none")}</span>;
  return <TextField help={help} label="" value={typeof v === "string" ? v : ""} onChange={(x) => onSet(x === "" ? undefined : x)} wide />;
}
