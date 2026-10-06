import type { ReactNode } from "react";
import { type Help, helpAttributes } from "./help";

interface Props {
  help: Help;
  children?: ReactNode;
  className?: string;
}

/** A table heading with hover help (what the column shows). */
export function HeaderCell({ help, children, className }: Props) {
  return (
    <th className={className} tabIndex={0} {...helpAttributes(help)}>
      {children}
    </th>
  );
}
