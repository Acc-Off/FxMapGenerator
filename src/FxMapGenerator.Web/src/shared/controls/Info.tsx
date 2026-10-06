import type { ReactNode } from "react";
import { type Help, helpAttributes } from "./help";

interface Props {
  help: Help;
  children: ReactNode;
  className?: string;
  /** Block element instead of inline text. */
  block?: boolean;
}

/** Non-interactive text with hover help: status marks, values, headings of lists. */
export function Info({ help, children, className, block }: Props) {
  const Tag = block ? "div" : "span";
  return (
    <Tag className={className} tabIndex={0} {...helpAttributes(help)}>
      {children}
    </Tag>
  );
}
