import { type Help, helpAttributes } from "./help";

interface Props {
  help: Help;
  label: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  /** Wide field for paths. */
  wide?: boolean;
}

/** A one-line text field with its label and hover help. */
export function TextField({ help, label, value, onChange, placeholder, wide }: Props) {
  return (
    <label className={`field${wide ? " field-wide" : ""}`} {...helpAttributes(help)}>
      <span className="field-label">{label}</span>
      <input type="text" value={value} placeholder={placeholder} spellCheck={false} onChange={(e) => onChange(e.target.value)} />
    </label>
  );
}
