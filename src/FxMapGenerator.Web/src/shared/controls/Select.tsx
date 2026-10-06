import { type Help, helpAttributes, type Reason } from "./help";

interface Option<T extends string> {
  value: T;
  label: string;
  /** A heading the option is listed under (options of one heading come one after another). */
  group?: string;
  /** Shown but not chosen. */
  disabled?: boolean;
}

interface Props<T extends string> {
  help: Help;
  value: T;
  options: readonly Option<T>[];
  onChange: (value: T) => void;
  /** Visible label in front of the list. */
  label?: string;
  /** While set, the list cannot be changed and its help also shows this reason. */
  disabledReason?: Reason | null;
  className?: string;
}

/** A drop-down list with hover help (on the label and the list together); options may come under headings. */
export function Select<T extends string>({ help, value, options, onChange, label, disabledReason, className }: Props<T>) {
  // consecutive options of one heading go into one group
  const runs: { group?: string; options: Option<T>[] }[] = [];
  for (const o of options) {
    const last = runs[runs.length - 1];
    if (last && last.group === o.group) last.options.push(o);
    else runs.push({ group: o.group, options: [o] });
  }
  const item = (o: Option<T>) => (
    <option key={o.value} value={o.value} disabled={o.disabled}>
      {o.label}
    </option>
  );
  return (
    <label className={`select${disabledReason ? " is-disabled" : ""}${className ? ` ${className}` : ""}`} {...helpAttributes(help, disabledReason)}>
      {label && <span className="select-label">{label}</span>}
      <select value={value} disabled={!!disabledReason} onChange={(e) => onChange(e.target.value as T)}>
        {runs.map((r, i) =>
          r.group === undefined ? (
            r.options.map(item)
          ) : (
            <optgroup key={`${i}-${r.group}`} label={r.group}>
              {r.options.map(item)}
            </optgroup>
          ),
        )}
      </select>
    </label>
  );
}
