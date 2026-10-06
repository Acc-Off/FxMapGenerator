import { useId } from "react";
import { type Help, helpAttributes, type Reason } from "./help";

interface Props {
  help: Help;
  value: number;
  min: number;
  max: number;
  /** The step between values (default 1). */
  step?: number;
  onChange: (value: number) => void;
  /** Visible label in front of the slider. */
  label?: string;
  /** The value as shown after the slider (e.g. "12 / 24"). */
  display?: string;
  /** Tick marks. */
  marks?: number[];
  disabledReason?: Reason | null;
}

/** A slider (whole numbers unless a step is given) with its label, the value and hover help. */
export function Slider({ help, value, min, max, step = 1, onChange, label, display, marks, disabledReason }: Props) {
  const list = useId();
  return (
    <label className={`slider${disabledReason ? " is-disabled" : ""}`} {...helpAttributes(help, disabledReason)}>
      {label && <span className="select-label">{label}</span>}
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        list={marks ? list : undefined}
        disabled={!!disabledReason}
        onChange={(e) => onChange(Number(e.target.value))}
      />
      {marks && (
        <datalist id={list}>
          {marks.map((m) => (
            <option key={m} value={m} />
          ))}
        </datalist>
      )}
      <span className="slider-value">{display ?? value}</span>
    </label>
  );
}
