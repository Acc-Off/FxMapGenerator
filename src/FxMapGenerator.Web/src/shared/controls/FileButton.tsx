import { type ReactNode, useRef } from "react";
import { type Help, helpAttributes, type Reason } from "./help";

interface Props {
  help: Help;
  children: ReactNode;
  /** The file chosen (one; nothing when the choice is cancelled). */
  onFile: (file: File) => void;
  /** File types offered first, as the file input's accept (e.g. ".json"). */
  accept?: string;
  disabledReason?: Reason | null;
  className?: string;
}

/**
 * A button that opens the browser's window for choosing a file, with hover help. The file is read in the page (it is
 * not uploaded anywhere by the choice itself).
 */
export function FileButton({ help, children, onFile, accept, disabledReason, className }: Props) {
  const input = useRef<HTMLInputElement>(null);
  const disabled = !!disabledReason;
  return (
    <>
      <button
        type="button"
        className={`btn btn-plain${className ? ` ${className}` : ""}`}
        aria-disabled={disabled || undefined}
        onClick={disabled ? undefined : () => input.current?.click()}
        {...helpAttributes(help, disabledReason)}
      >
        {children}
      </button>
      <input
        ref={input}
        type="file"
        accept={accept}
        hidden
        onChange={(e) => {
          const file = e.target.files?.[0];
          e.target.value = "";                                  // the same file chosen again fires again
          if (file) onFile(file);
        }}
      />
    </>
  );
}
