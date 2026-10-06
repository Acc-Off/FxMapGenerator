import type { MouseEvent, ReactNode } from "react";
import { type Help, helpAttributes, type Reason } from "./help";

interface Props {
  help: Help;
  children: ReactNode;
  /** The click (its event, for the keys held with it). */
  onClick?: (e: MouseEvent<HTMLButtonElement>) => void;
  /** While set, the button is disabled and its help also shows this reason. */
  disabledReason?: Reason | null;
  variant?: "primary" | "plain" | "link" | "danger" | "row";
  className?: string;
  /** Shown pressed (a tool that is on). */
  pressed?: boolean;
}

/**
 * A button with hover help. Disabled buttons stay focusable and hoverable (aria-disabled instead of the disabled
 * attribute, which swallows pointer events) so their help can say why they are disabled.
 */
export function Button({ help, children, onClick, disabledReason, variant = "plain", className, pressed }: Props) {
  const disabled = !!disabledReason;
  return (
    <button
      type="button"
      className={`btn btn-${variant}${className ? ` ${className}` : ""}`}
      aria-disabled={disabled || undefined}
      aria-pressed={pressed}
      onClick={disabled ? undefined : onClick}
      {...helpAttributes(help, disabledReason)}
    >
      {children}
    </button>
  );
}
