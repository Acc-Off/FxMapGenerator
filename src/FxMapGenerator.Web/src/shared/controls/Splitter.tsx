import { useRef, type KeyboardEvent, type PointerEvent } from "react";
import { type Help, helpAttributes } from "./help";

interface Props {
  help: Help;
  /** The width of the panel the splitter sizes, in CSS pixels. */
  width: number;
  /** Where that panel is: right of the splitter (default) or left of it. */
  panel?: "right" | "left";
  /** While dragging: the width to show. */
  onResize: (width: number) => void;
  /** The width to keep (drag released, a key, a double-click for the default). */
  onCommit: (width: number) => void;
  defaultWidth: number;
  className?: string;
}

const STEP = 16;

/**
 * A vertical bar between two panels that resizes the panel on its right (or its left): drag it, move it with the arrow
 * keys, or double-click it for the default width. It has hover help like the other controls.
 */
export function Splitter({ help, width, panel = "right", onResize, onCommit, defaultWidth, className }: Props) {
  const drag = useRef<{ x: number; width: number } | null>(null);
  const sign = panel === "right" ? 1 : -1;
  const widthAt = (clientX: number) => drag.current!.width + sign * (drag.current!.x - clientX);

  const down = (e: PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    drag.current = { x: e.clientX, width };
    e.currentTarget.setPointerCapture(e.pointerId);
    e.currentTarget.classList.add("is-dragging");
    e.preventDefault();
  };
  const move = (e: PointerEvent<HTMLDivElement>) => {
    if (drag.current) onResize(widthAt(e.clientX));
  };
  const up = (e: PointerEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    const w = widthAt(e.clientX);
    drag.current = null;
    e.currentTarget.classList.remove("is-dragging");
    if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId);
    onCommit(w);
  };
  const key = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === "ArrowLeft") onCommit(width + sign * STEP);
    else if (e.key === "ArrowRight") onCommit(width - sign * STEP);
    else return;
    e.preventDefault();
  };

  return (
    <div
      role="separator"
      aria-orientation="vertical"
      aria-valuenow={width}
      tabIndex={0}
      className={`splitter${className ? ` ${className}` : ""}`}
      onPointerDown={down}
      onPointerMove={move}
      onPointerUp={up}
      onPointerCancel={up}
      onDoubleClick={() => onCommit(defaultWidth)}
      onKeyDown={key}
      {...helpAttributes(help)}
    />
  );
}
