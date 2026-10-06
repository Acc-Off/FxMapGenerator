import { type PointerEvent as ReactPointerEvent, useEffect, useLayoutEffect, useRef, useState } from "react";
import { Button, type Help, Info, NumberField, TextField } from "../../shared/controls";
import { helpAttributes } from "../../shared/controls/help";
import { useT } from "../../shared/i18n";

/** The recent colours, kept in this browser (a convenience only: nothing depends on it). */
const RECENT_KEY = "fxmapgen.styles.recentColors";
const RECENT_MAX = 16;

export function recentColors(): string[] {
  try {
    const v = JSON.parse(localStorage.getItem(RECENT_KEY) ?? "[]") as unknown;
    return Array.isArray(v) ? v.filter((c): c is string => typeof c === "string" && /^#[0-9a-f]{6}$/.test(c)).slice(0, RECENT_MAX) : [];
  } catch {
    return [];
  }
}

function addRecent(hex: string) {
  try {
    const list = [hex, ...recentColors().filter((c) => c !== hex)].slice(0, RECENT_MAX);
    localStorage.setItem(RECENT_KEY, JSON.stringify(list));
  } catch {
    // storage blocked: the list is only a convenience
  }
}

type Rgb = [number, number, number];

function hexToRgb(hex: string): Rgb {
  const v = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  const n = v ? parseInt(v[1], 16) : 0;
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function rgbToHex([r, g, b]: Rgb): string {
  return "#" + [r, g, b].map((x) => Math.max(0, Math.min(255, Math.round(x))).toString(16).padStart(2, "0")).join("");
}

/** Hue 0-360, saturation and value 0-1. */
function rgbToHsv([r, g, b]: Rgb): [number, number, number] {
  const R = r / 255, G = g / 255, B = b / 255;
  const max = Math.max(R, G, B), min = Math.min(R, G, B), d = max - min;
  let h = 0;
  if (d > 0) {
    if (max === R) h = 60 * (((G - B) / d) % 6);
    else if (max === G) h = 60 * ((B - R) / d + 2);
    else h = 60 * ((R - G) / d + 4);
  }
  return [(h + 360) % 360, max === 0 ? 0 : d / max, max];
}

function hsvToRgb(h: number, s: number, v: number): Rgb {
  const c = v * s, x = c * (1 - Math.abs(((h / 60) % 2) - 1)), m = v - c;
  const [r, g, b] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
  return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
}

/** A colour as the eyedropper gives it (#rrggbb, or rgb(r, g, b) in some browsers), or null. */
function parseColor(s: string): string | null {
  if (/^#[0-9a-f]{6}$/i.test(s)) return s.toLowerCase();
  const m = /^rgba?\((\d+),\s*(\d+),\s*(\d+)/i.exec(s);
  return m ? rgbToHex([Number(m[1]), Number(m[2]), Number(m[3])]) : null;
}

interface EyeDropperResult {
  sRGBHex: string;
}
type EyeDropperCtor = new () => { open(): Promise<EyeDropperResult> };
const EyeDropper = (window as unknown as { EyeDropper?: EyeDropperCtor }).EyeDropper;

interface Props {
  value: string;
  /** Every colour chosen, while it is chosen (a drag gives many). */
  onChange(hex: string): void;
  onClose(): void;
  /** The element the picker opens from (it stays next to it, and a click on it is no click outside). */
  anchor: HTMLElement;
  /** The colours the style uses. */
  used: string[];
  help: Help;
}

const W = 276, H = 160;

/**
 * A colour picker next to its colour box: the saturation and brightness of the hue (a square), the hue (a bar), the
 * colour as #rrggbb and as R, G, B, the eyedropper (browsers that have one), the colours the style uses and the colours
 * used last. Every change goes out at once; Esc or a click outside closes it.
 */
export function ColorPicker({ value, onChange, onClose, anchor, used, help }: Props) {
  const t = useT();
  const box = useRef<HTMLDivElement>(null);
  const [hsv, setHsv] = useState(() => rgbToHsv(hexToRgb(value)));
  const [hexText, setHexText] = useState(value);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);
  const opened = useRef(value);
  const latest = useRef(value);
  latest.current = value;
  const recent = useRef(recentColors());

  // a colour set from outside (typed, picked, a swatch) moves the handles; the picker's own colour keeps its hue
  useEffect(() => {
    if (rgbToHex(hsvToRgb(...hsv)) !== value) setHsv(rgbToHsv(hexToRgb(value)));
    setHexText(value);
  }, [value]); // eslint-disable-line react-hooks/exhaustive-deps

  // next to the colour box: below it when there is room, else above; kept in the window (and when the list scrolls)
  useLayoutEffect(() => {
    const place = () => {
      const a = anchor.getBoundingClientRect(), b = box.current?.getBoundingClientRect();
      const h = b?.height ?? 360, w = b?.width ?? W + 24;
      const top = a.bottom + 6 + h <= window.innerHeight ? a.bottom + 6 : Math.max(6, a.top - 6 - h);
      setPos({ left: Math.min(Math.max(6, a.left), window.innerWidth - w - 6), top });
    };
    place();
    window.addEventListener("scroll", place, true);
    window.addEventListener("resize", place);
    return () => {
      window.removeEventListener("scroll", place, true);
      window.removeEventListener("resize", place);
    };
  }, [anchor]);

  // closing keeps the colour in the recent ones (when it changed)
  useEffect(() => {
    const close = () => {
      if (latest.current !== opened.current) addRecent(latest.current);
      onClose();
    };
    const onDown = (e: PointerEvent) => {
      const target = e.target as Node;
      if (box.current?.contains(target) || anchor.contains(target)) return;
      close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        close();
      }
    };
    document.addEventListener("pointerdown", onDown, true);
    window.addEventListener("keydown", onKey, true);
    return () => {
      document.removeEventListener("pointerdown", onDown, true);
      window.removeEventListener("keydown", onKey, true);
    };
  }, [anchor, onClose]);

  const setFromHsv = (h: number, s: number, v: number) => {
    setHsv([h, s, v]);
    onChange(rgbToHex(hsvToRgb(h, s, v)));
  };
  const setHex = (hex: string) => {
    const c = hex.toLowerCase();
    setHsv(rgbToHsv(hexToRgb(c)));
    onChange(c);
  };
  const drag = (e: ReactPointerEvent<HTMLDivElement>, at: (x: number, y: number) => void) => {
    const el = e.currentTarget;
    el.setPointerCapture(e.pointerId);
    const r = el.getBoundingClientRect();
    const move = (x: number, y: number) => at(Math.max(0, Math.min(1, (x - r.left) / r.width)), Math.max(0, Math.min(1, (y - r.top) / r.height)));
    move(e.clientX, e.clientY);
    const onMove = (ev: PointerEvent) => move(ev.clientX, ev.clientY);
    const onUp = () => {
      el.removeEventListener("pointermove", onMove);
      el.removeEventListener("pointerup", onUp);
      el.removeEventListener("pointercancel", onUp);
    };
    el.addEventListener("pointermove", onMove);
    el.addEventListener("pointerup", onUp);
    el.addEventListener("pointercancel", onUp);
  };
  const [h, s, v] = hsv;
  const rgb = hexToRgb(value);
  const swatches = (list: string[], helpKey: "help.styles.color.used" | "help.styles.color.recent") => (
    <div className="color-swatches">
      {list.map((c) => (
        <Button key={c} help={helpKey} className={`color-swatch${c === value ? " is-current" : ""}`} onClick={() => setHex(c)}>
          <span style={{ background: c }} />
        </Button>
      ))}
    </div>
  );
  return (
    <div ref={box} className="color-picker" style={pos ? { left: pos.left, top: pos.top } : { left: -9999, top: -9999 }} role="dialog" aria-label={t("styles.color.title")}>
      <div
        className="color-sv"
        style={{ width: W, height: H, background: `linear-gradient(to top, #000, transparent), linear-gradient(to right, #fff, hsl(${h}, 100%, 50%))` }}
        onPointerDown={(e) => drag(e, (x, y) => setFromHsv(h, x, 1 - y))}
        {...helpAttributes("help.styles.color.square")}
      >
        <span className="color-handle" style={{ left: s * W, top: (1 - v) * H, background: value }} />
      </div>
      <div className="color-hue" style={{ width: W }} onPointerDown={(e) => drag(e, (x) => setFromHsv(Math.min(359.9, x * 360), s, v))} {...helpAttributes("help.styles.color.hue")}>
        <span className="color-handle color-handle-hue" style={{ left: (h / 360) * W, background: `hsl(${h}, 100%, 50%)` }} />
      </div>
      <div className="color-fields">
        <TextField
          help="help.styles.color.hex"
          label="#"
          value={hexText}
          onChange={(text) => {
            setHexText(text);
            const c = /^#?([0-9a-f]{6})$/i.exec(text.trim());
            if (c) setHex("#" + c[1]);
          }}
        />
        {(["R", "G", "B"] as const).map((name, i) => (
          <NumberField
            key={name}
            help="help.styles.color.rgb"
            label={name}
            value={rgb[i]}
            min={0}
            max={255}
            onChange={(n) => {
              if (n === null) return;
              const next: Rgb = [...rgb];
              next[i] = n;
              setHex(rgbToHex(next));
            }}
          />
        ))}
      </div>
      {EyeDropper && (
        <Button
          help="help.styles.color.eyedropper"
          onClick={() => {
            void new EyeDropper()
              .open()
              .then((r) => {
                const c = parseColor(r.sRGBHex);
                if (c) setHex(c);
              })
              .catch(() => undefined);
          }}
        >
          {t("styles.color.eyedropper")}
        </Button>
      )}
      <Info help="help.styles.color.used" block className="color-title">
        {t("styles.color.used")}
      </Info>
      {swatches(used, "help.styles.color.used")}
      {recent.current.length > 0 && (
        <>
          <Info help="help.styles.color.recent" block className="color-title">
            {t("styles.color.recent")}
          </Info>
          {swatches(recent.current, "help.styles.color.recent")}
        </>
      )}
      <Info help={help} block className="muted color-help">
        {t("styles.color.hint")}
      </Info>
    </div>
  );
}
