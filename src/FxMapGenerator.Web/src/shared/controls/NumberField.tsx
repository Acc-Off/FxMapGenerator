import { useEffect, useState } from "react";
import { type Help, helpAttributes, type Reason } from "./help";

interface Props {
  help: Help;
  label: string;
  /** null = empty. */
  value: number | null;
  /** Called with the number typed (null when emptied), once it is a number within min / max. */
  onChange: (value: number | null) => void;
  min?: number;
  max?: number;
  step?: number;
  /** Shown while empty (e.g. what empty means). */
  placeholder?: string;
  /** Whether the field may be emptied (else an empty field goes back to the value). */
  allowEmpty?: boolean;
  /** Unit after the field. */
  unit?: string;
  disabledReason?: Reason | null;
}

/** A number field with its label, unit and hover help; the value is taken when the field loses focus or Enter is pressed. */
export function NumberField({ help, label, value, onChange, min, max, step = 1, placeholder, allowEmpty, unit, disabledReason }: Props) {
  const [text, setText] = useState(value === null ? "" : String(value));
  useEffect(() => setText(value === null ? "" : String(value)), [value]);
  const commit = () => {
    const t = text.trim();
    if (t === "") {
      if (allowEmpty) {
        if (value !== null) onChange(null);
      } else setText(value === null ? "" : String(value));
      return;
    }
    const n = Number(t);
    if (!Number.isFinite(n) || (min !== undefined && n < min) || (max !== undefined && n > max)) {
      setText(value === null ? "" : String(value));
      return;
    }
    if (n !== value) onChange(n);
  };
  return (
    <label className={`field field-number${disabledReason ? " is-disabled" : ""}`} {...helpAttributes(help, disabledReason)}>
      <span className="field-label">{label}</span>
      <input
        type="number"
        value={text}
        min={min}
        max={max}
        step={step}
        placeholder={placeholder}
        disabled={!!disabledReason}
        onChange={(e) => setText(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") commit();
        }}
      />
      {unit && <span className="field-unit">{unit}</span>}
    </label>
  );
}
