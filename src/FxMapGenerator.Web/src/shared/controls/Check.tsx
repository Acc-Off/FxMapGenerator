import { type ReactNode, useEffect, useRef } from "react";
import { type Help, helpAttributes, type Reason } from "./help";

interface Props {
  help: Help;
  checked: boolean;
  onChange: (checked: boolean) => void;
  children: ReactNode;
  /** While set, the box cannot be changed and its help also shows this reason. */
  disabledReason?: Reason | null;
  /** Neither checked nor not: some of what the box switches together are on (a click turns them all on). */
  mixed?: boolean;
}

/** A check box with its label and hover help. */
export function Check({ help, checked, onChange, children, disabledReason, mixed }: Props) {
  const box = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (box.current) box.current.indeterminate = !!mixed;
  }, [mixed]);
  return (
    <label className={`check${disabledReason ? " is-disabled" : ""}`} {...helpAttributes(help, disabledReason)}>
      <input ref={box} type="checkbox" checked={checked} disabled={!!disabledReason} onChange={(e) => onChange(e.target.checked)} />
      <span>{children}</span>
    </label>
  );
}
