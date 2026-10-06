import L from "leaflet";
import { api } from "../../shared/api";
import { LEFT, TOP } from "../../shared/mapFrame";
import type { PoiIcons } from "../../shared/types";
import { metresPerPixel } from "../roads/pathsLayer";
import { ICON_BADGE, LABEL_GAP, labelIn, type Resolved } from "./model";

// The POI screen's map: the points drawn as the maps draw them (their size in metres at the map's scale: the text, the
// circle, the icon and the label beside), on canvas tiles; hidden points and points on no map faint, the selection with
// a yellow ring, a locked point with a small lock.

export const SELECTED = "#ffd400";

export interface PoiLayerState {
  points: Resolved[];
  selection: ReadonlySet<string>;
  /** Points being dragged: key -> position shown meanwhile. */
  drag: Map<string, { x: number; y: number }> | null;
  fonts: Record<string, string>;
  lang: string;
  icons: PoiIcons | null;
}

export interface PoiLayer extends L.GridLayer {
  state: PoiLayerState;
  repaint(): void;
}

interface Tile {
  canvas: HTMLCanvasElement;
  coords: L.Coords;
}

// ---------------------------------------------------------------- shapes (metres around the point)

const measure = document.createElement("canvas").getContext("2d")!;
const widths = new Map<string, number>();

/** A text's width in metres at a size (em, m), measured once at 100 px. */
export function textWidth(text: string, family: string, bold: boolean, em: number): number {
  const key = `${bold ? "b" : "n"}\u0001${family}\u0001${text}`;
  let w = widths.get(key);
  if (w === undefined) {
    measure.font = `${bold ? "bold " : ""}100px "${family}"`;
    w = measure.measureText(text).width / 100;
    widths.set(key, w);
  }
  return w * em;
}

const images = new Map<string, HTMLImageElement>();
let onImage: (() => void) | null = null;

/** A PNG a style names (loaded once; the layer repaints when it comes). */
export function iconImage(path: string): HTMLImageElement | null {
  let img = images.get(path);
  if (!img) {
    img = new Image();
    img.onload = () => onImage?.();
    img.src = api.poiImageUrl(path);
    images.set(path, img);
  }
  return img.complete && img.naturalWidth > 0 ? img : null;
}

/** Forgets the PNGs (after a PNG of the same name was replaced). */
export function forgetImages() {
  images.clear();
}

/** The text a point writes (its label in the language, else English) and the font family of that label's language. */
function textOf(r: Resolved, s: PoiLayerState): { text: string; family: string } {
  const own = r.point.label[s.lang];
  const lang = own && own.length > 0 ? s.lang : "en";
  return { text: labelIn(r.point.label, s.lang), family: s.fonts[lang] ?? s.fonts.en ?? "Bahnschrift" };
}

/** The mark of a dot or icon without its outline (m). */
function mark(r: Resolved, s: PoiLayerState): { w: number; h: number } {
  const st = r.style;
  if (!st || st.look === "dot") return { w: r.size, h: r.size };
  let w = r.size;
  if (st.image) {
    const img = iconImage(st.image);
    if (img) w = (r.size * img.naturalWidth) / img.naturalHeight;
  }
  if (st.badgeColor) {
    const d = ICON_BADGE * Math.max(w, r.size);
    return { w: d, h: d };
  }
  void s;
  return { w, h: r.size };
}

/** The box a point draws in (m from the point: west, east, north, south). */
export function extent(r: Resolved, s: PoiLayerState): { w: number; e: number; n: number; s: number } {
  const st = r.style;
  const ow = st?.outline ? st.outlineWidth : 0;
  if (!st || st.look === "dot" || st.look === "icon") {
    const m = mark(r, s);
    let e = m.w / 2 + ow / 2, half = m.h / 2 + ow / 2;
    if (st?.showLabel) {
      const { text, family } = textOf(r, s);
      if (text) {
        e = m.w / 2 + ow / 2 + LABEL_GAP * r.labelSize + textWidth(text, family, st.weight === "bold", r.labelSize) + ow / 2;
        half = Math.max(half, 0.5 * r.labelSize);
      }
    }
    return { w: m.w / 2 + ow / 2, e, n: half, s: half };
  }
  const { text, family } = textOf(r, s);
  const w = textWidth(text || " ", family, st.weight === "bold", r.size);
  const h = 0.8 * r.size;
  if (st.look === "badge") {
    const d = Math.max(w, h) + 0.4 * r.size;
    return { w: d / 2, e: d / 2, n: d / 2, s: d / 2 };
  }
  return { w: w / 2 + ow / 2, e: w / 2 + ow / 2, n: h / 2 + ow / 2, s: h / 2 + ow / 2 };
}

/** The topmost point whose box holds (x, y) (at least 8 px across at the zoom), or null. */
export function pickPoint(s: PoiLayerState, x: number, y: number, zoom: number): Resolved | null {
  const min = 4 * metresPerPixel(zoom);
  for (let i = s.points.length - 1; i >= 0; i--) {
    const r = s.points[i];
    const p = s.drag?.get(r.key) ?? r.point;
    const b = extent(r, s);
    if (x >= p.x - Math.max(b.w, min) && x <= p.x + Math.max(b.e, min) && y <= p.y + Math.max(b.n, min) && y >= p.y - Math.max(b.s, min)) return r;
  }
  return null;
}

// ---------------------------------------------------------------- drawing

/** Draws a point with its centre at (cx, cy) px, ppm pixels a metre. */
export function drawPoint(ctx: CanvasRenderingContext2D, r: Resolved, s: PoiLayerState, cx: number, cy: number, ppm: number, icons: PoiIcons | null) {
  const st = r.style;
  if (!st) {
    // a point without a style (it cannot be drawn on the maps): a grey cross
    ctx.strokeStyle = "#888888";
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(cx - 5, cy - 5);
    ctx.lineTo(cx + 5, cy + 5);
    ctx.moveTo(cx + 5, cy - 5);
    ctx.lineTo(cx - 5, cy + 5);
    ctx.stroke();
    return;
  }
  const outline = st.outline && st.outlineWidth > 0 ? st.outline : null;
  const ow = st.outlineWidth * ppm;
  const size = r.size * ppm;
  const bold = st.weight === "bold";
  const { text, family } = textOf(r, s);
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  const writeText = (t: string, em: number, x: number, align: CanvasTextAlign, colour: string, halo: string | null) => {
    ctx.font = `${bold ? "bold " : ""}${em}px "${family}"`;
    ctx.textAlign = align;
    ctx.textBaseline = "alphabetic";
    const m = ctx.measureText(t);
    const base = cy + (m.actualBoundingBoxAscent - m.actualBoundingBoxDescent) / 2;
    if (halo) {
      ctx.strokeStyle = halo;
      ctx.lineWidth = ow;
      ctx.strokeText(t, x, base);
    }
    ctx.fillStyle = colour;
    ctx.fillText(t, x, base);
  };
  if (st.look === "text") {
    if (text) writeText(text, size, cx, "center", r.color, outline);
    return;
  }
  if (st.look === "badge") {
    ctx.font = `${bold ? "bold " : ""}${size}px "${family}"`;
    const m = ctx.measureText(text || " ");
    const h = Math.max(m.actualBoundingBoxAscent + m.actualBoundingBoxDescent, 0.6 * size);
    const d = Math.max(m.width, h) + 0.4 * size;
    ctx.fillStyle = st.badgeColor ?? r.color;
    ctx.beginPath();
    ctx.arc(cx, cy, d / 2, 0, 2 * Math.PI);
    ctx.fill();
    if (text) writeText(text, size, cx, "center", r.color, null);
    return;
  }
  // a dot or an icon, and the label beside it
  const m = mark(r, s);
  if (st.look === "dot") {
    ctx.fillStyle = r.color;
    ctx.beginPath();
    ctx.arc(cx, cy, size / 2, 0, 2 * Math.PI);
    ctx.fill();
    if (outline) {
      ctx.strokeStyle = outline;
      ctx.lineWidth = ow;
      ctx.stroke();
    }
  } else {
    if (st.badgeColor) {
      ctx.fillStyle = st.badgeColor;
      ctx.beginPath();
      ctx.arc(cx, cy, (m.w * ppm) / 2, 0, 2 * Math.PI);
      ctx.fill();
      if (outline) {
        ctx.strokeStyle = outline;
        ctx.lineWidth = ow;
        ctx.stroke();
      }
    }
    if (st.image) {
      const img = iconImage(st.image);
      if (img) {
        const w = (size * img.naturalWidth) / img.naturalHeight;
        ctx.drawImage(img, cx - w / 2, cy - size / 2, w, size);
      }
    } else if (st.icon && icons?.icons[st.icon]) {
      const path = iconPath(st.icon, icons);
      const k = size / 24;
      ctx.save();
      ctx.translate(cx - size / 2, cy - size / 2);
      ctx.scale(k, k);
      if (outline && !st.badgeColor) {
        ctx.strokeStyle = outline;
        ctx.lineWidth = ow / k;
        ctx.stroke(path);
      }
      ctx.fillStyle = r.color;
      ctx.fill(path);
      ctx.restore();
    }
  }
  if (st.showLabel && text) {
    // in the point's colour; an icon over a circle writes its label in the circle's
    const left = cx + (m.w * ppm) / 2 + ow / 2 + LABEL_GAP * r.labelSize * ppm;
    writeText(text, r.labelSize * ppm, left, "left", st.look === "icon" && st.badgeColor ? st.badgeColor : r.color, outline);
  }
}

/** Below this height (px) a point is shown as a small dot (the maps draw it as it is). */
const MIN_PX = 6;

function drawSmall(ctx: CanvasRenderingContext2D, cx: number, cy: number, colour: string) {
  ctx.beginPath();
  ctx.arc(cx, cy, 3, 0, 2 * Math.PI);
  ctx.fillStyle = colour;
  ctx.fill();
  ctx.strokeStyle = "#ffffff";
  ctx.lineWidth = 1.5;
  ctx.stroke();
}

const paths = new Map<string, Path2D>();

export function iconPath(name: string, icons: PoiIcons): Path2D {
  let p = paths.get(name);
  if (!p) paths.set(name, (p = new Path2D(icons.icons[name]?.path ?? "")));
  return p;
}

/** The MDI "lock" icon, drawn small at a locked point's corner. */
function drawLock(ctx: CanvasRenderingContext2D, x: number, y: number, icons: PoiIcons | null) {
  if (!icons?.icons.lock) return;
  ctx.save();
  ctx.translate(x, y);
  ctx.scale(12 / 24, 12 / 24);
  ctx.fillStyle = "#ffffff";
  ctx.strokeStyle = "#ffffff";
  ctx.lineWidth = 6;
  ctx.lineJoin = "round";
  const p = iconPath("lock", icons);
  ctx.stroke(p);
  ctx.fillStyle = "#333333";
  ctx.fill(p);
  ctx.restore();
}

export function createPoiLayer(state: PoiLayerState): PoiLayer {
  const tiles = new Map<string, Tile>();
  const Layer = L.GridLayer.extend({
    createTile(coords: L.Coords) {
      const canvas = document.createElement("canvas");
      const dpr = window.devicePixelRatio || 1;
      canvas.width = 256 * dpr;
      canvas.height = 256 * dpr;
      canvas.style.width = "256px";
      canvas.style.height = "256px";
      canvas.dataset.tile = `${coords.x}:${coords.y}:${coords.z}`;
      tiles.set(`${coords.x}:${coords.y}:${coords.z}`, { canvas, coords });
      draw(canvas, coords, (this as PoiLayer).state);
      return canvas;
    },
  });
  const Made = Layer as unknown as new (options: L.GridLayerOptions) => PoiLayer;
  const layer = new Made({ tileSize: 256, noWrap: true, updateWhenZooming: false, keepBuffer: 1 });
  layer.state = state;
  layer.on("tileunload", (e: L.TileEvent) => tiles.delete(`${e.coords.x}:${e.coords.y}:${e.coords.z}`));
  layer.repaint = () => {
    for (const t of tiles.values()) draw(t.canvas, t.coords, layer.state);
  };
  onImage = () => layer.repaint();
  return layer;
}

function draw(canvas: HTMLCanvasElement, coords: L.Coords, s: PoiLayerState) {
  const ctx = canvas.getContext("2d")!;
  const dpr = canvas.width / 256;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, 256, 256);
  const ppm = 1 / metresPerPixel(coords.z);
  const x0 = LEFT + (coords.x * 256) / ppm, y0 = TOP - (coords.y * 256) / ppm;
  const x1 = x0 + 256 / ppm, y1 = y0 - 256 / ppm;
  const pad = 16 / ppm;
  const marks: { x: number; y: number; r: Resolved }[] = [];
  for (const r of s.points) {
    const p = s.drag?.get(r.key) ?? r.point;
    const b = extent(r, s);
    if (p.x + b.e + pad < x0 || p.x - b.w - pad > x1 || p.y - b.s - pad > y0 || p.y + b.n + pad < y1) continue;
    const cx = (p.x - x0) * ppm, cy = (y0 - p.y) * ppm;
    ctx.globalAlpha = r.visible && (r.show.atlas || r.show.roadmap) ? 1 : 0.35;
    // a point drawn smaller than a few pixels (zoomed far out) is shown as a small dot of its colour, to be found
    if ((b.n + b.s) * ppm < MIN_PX) drawSmall(ctx, cx, cy, r.color);
    else drawPoint(ctx, r, s, cx, cy, ppm, s.icons);
    ctx.globalAlpha = 1;
    marks.push({ x: cx, y: cy, r });
  }
  // the selection and the locks over every point
  for (const { x, y, r } of marks) {
    const b = extent(r, s);
    if (s.selection.has(r.key)) {
      ctx.strokeStyle = SELECTED;
      ctx.lineWidth = 2;
      ctx.setLineDash([]);
      ctx.strokeRect(x - b.w * ppm - 4, y - b.n * ppm - 4, (b.w + b.e) * ppm + 8, (b.n + b.s) * ppm + 8);
    }
    // the lock only where the point is drawn big enough to carry it, or chosen
    if (r.locked && (s.selection.has(r.key) || (b.n + b.s) * ppm >= 12)) drawLock(ctx, x + Math.max(b.e * ppm, 4) - 2, y - Math.max(b.n * ppm, 4) - 10, s.icons);
  }
}
