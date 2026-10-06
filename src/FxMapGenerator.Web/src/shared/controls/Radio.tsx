import type { ReactNode } from "react";
import { type Help, helpAttributes, type Reason } from "./help";

interface Props {
  help: Help;
  /** The group the button belongs to (one of them is chosen). */
  group: string;
  checked: boolean;
  onChange: () => void;
  children: ReactNode;
  /** While set, the button cannot be chosen and its help also shows this reason. */
  disabledReason?: Reason | null;
}

/** One radio button of a group, with its label and hover help. */
export function Radio({ help, group, checked, onChange, children, disabledReason }: Props) {
  return (
    <label className={`check radio${disabledReason ? " is-disabled" : ""}`} {...helpAttributes(help, disabledReason)}>
      <input type="radio" name={group} checked={checked} disabled={!!disabledReason} onChange={() => onChange()} />
      <span>{children}</span>
    </label>
  );
}
