import L from "leaflet";
import type { BlockGrid, MapFrame } from "./types";

// The map's grid (Core/World/WorldGrid.cs): game metres, x east, y north; a z8 tile is 70.3125 m, a block 4 tiles. LEFT and
// TOP, the standard frame's north-west corner, are the origin of every block, cell and tile number (a project's frame can
// add cells around the standard frame: the numbers go negative west and north of it).
export const TILE = 70.3125;
export const BLOCK = 4 * TILE;
export const LEFT = -4140;
export const TOP = 8400;
/** The standard frame's east and south edges and its blocks. */
export const RIGHT = 4860;
export const BOTTOM = -5100;
export const COLS = 32;
export const ROWS = 48;
/** Blocks per cell side. */
export const CELL = 8;
/** A minimap sheet: 4500 m, 2 x 3 over the map (Core/Minimap/MinimapSheets.cs). */
export const SHEET = 4500;

/** Leaflet's lat = game y, lng = game x; at zoom z a metre is 2^z / 70.3125 px (z8 = one tile per 70.3125 m). */
export const crs = L.extend({}, L.CRS.Simple, {
  transformation: new L.Transformation(1 / TILE, -LEFT / TILE, -1 / TILE, TOP / TILE),
}) as L.CRS;
/** The standard frame (a project without cells added around the map). */
export const STANDARD_FRAME: MapFrame = { bx0: 0, by0: 0, cols: COLS, rows: ROWS, west: LEFT, north: TOP, east: RIGHT, south: BOTTOM };

export function frameBounds(frame: MapFrame): L.LatLngBounds {
  return L.latLngBounds([frame.south, frame.west], [frame.north, frame.east]);
}

/** The standard frame's bounds: the PostalMap tiles cover it and no more. */
export const worldBounds = frameBounds(STANDARD_FRAME);

let openFrame: MapFrame = STANDARD_FRAME;

/** The open project's frame (the project store keeps it; the standard frame without a project). */
export function currentFrame(): MapFrame {
  return openFrame;
}

export function setCurrentFrame(frame: MapFrame | null | undefined): void {
  openFrame = frame ?? STANDARD_FRAME;
}

/** A frame as a short key, for effects that follow it. */
export function frameKey(frame: BlockGrid | null | undefined): string {
  return frame ? `${frame.bx0},${frame.by0},${frame.cols},${frame.rows}` : "";
}

/**
 * The postal-code map tiles share the frame but not the scale: their z6 tile is 375 m (24 x 36 tiles over the map)
 * where ours is 281.25 m, so each of their tiles is 4/3 of 256 px on the map. Tile pictures of that size do not sit on
 * whole pixels and the browser leaves lines between them, so the layer's tiles are ours (256 px canvases), each drawing
 * the parts of their tiles under it (up to 2 x 2) on whole pixels.
 */
const POSTAL_URL = "https://postal-code-map-tile.pages.dev/tiles";
const POSTAL_TILE = 1024 / 3;
/** Their tiles across and down the map at z6; half as many, rounded up, at each zoom below. */
const POSTAL_COLS = 24;
const POSTAL_ROWS = 36;
/** Their tile pictures kept at hand, the oldest dropped first: neighbouring tiles of ours and the maps of a screen share them. */
const POSTAL_KEPT = 100;
const postalPictures = new Map<string, Promise<HTMLImageElement | null>>();

/** One of their tiles, loaded (null when it cannot be loaded; asked for again the next time). */
function postalPicture(z: number, x: number, y: number): Promise<HTMLImageElement | null> {
  const url = `${POSTAL_URL}/${z}/${x}/${y}.png`;
  const kept = postalPictures.get(url);
  if (kept) {
    postalPictures.delete(url);
    postalPictures.set(url, kept);
    return kept;
  }
  const picture = new Promise<HTMLImageElement | null>((resolve) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => {
      if (postalPictures.get(url) === picture) postalPictures.delete(url);
      resolve(null);
    };
    img.src = url;
  });
  postalPictures.set(url, picture);
  if (postalPictures.size > POSTAL_KEPT) postalPictures.delete(postalPictures.keys().next().value!);
  return picture;
}

const PostalLayer = L.GridLayer.extend({
  createTile(coords: L.Coords, done: L.DoneCallback) {
    const canvas = document.createElement("canvas");
    canvas.width = 256;
    canvas.height = 256;
    const ctx = canvas.getContext("2d")!;
    const cols = Math.ceil(POSTAL_COLS / 2 ** (6 - coords.z)), rows = Math.ceil(POSTAL_ROWS / 2 ** (6 - coords.z));
    // our tile counted in their tiles: 3/4 of one, from (x0, y0)
    const x0 = coords.x * 0.75, y0 = coords.y * 0.75;
    const drawn: Promise<void>[] = [];
    for (let y = Math.floor(y0); y < y0 + 0.75; y++) {
      for (let x = Math.floor(x0); x < x0 + 0.75; x++) {
        if (x < 0 || y < 0 || x >= cols || y >= rows) continue;
        drawn.push(
          postalPicture(coords.z, x, y).then((picture) => {
            if (!picture) return;
            const left = Math.round((x - x0) * POSTAL_TILE), top = Math.round((y - y0) * POSTAL_TILE);
            ctx.drawImage(picture, left, top, Math.round((x + 1 - x0) * POSTAL_TILE) - left, Math.round((y + 1 - y0) * POSTAL_TILE) - top);
          }),
        );
      }
    }
    void Promise.all(drawn).then(() => done(undefined, canvas));
    return canvas;
  },
}) as unknown as new (options: L.GridLayerOptions) => L.GridLayer;

export function postalLayer(options?: L.GridLayerOptions): L.GridLayer {
  return new PostalLayer({ minNativeZoom: 0, maxNativeZoom: 6, maxZoom: 10, noWrap: true, bounds: worldBounds, className: "tiles-postal", ...options });
}

/** Tile layers of the map screens, bottom to top: the base, then a map laid over it, then a map being drawn in a run. */
export const Z_BASE = 1;
export const Z_OVER = 2;
export const Z_DRAWN = 3;

/**
 * A short key of a project's work folder. Tile addresses carry it, so the browser never takes one project's tiles for
 * another's (the address is otherwise the same for every project).
 */
export function projectKey(workFolder: string): string {
  let h = 0x811c9dc5;
  for (const ch of workFolder.replace(/\//g, "\\").toLowerCase()) h = Math.imul(h ^ ch.codePointAt(0)!, 0x01000193) >>> 0;
  return h.toString(36);
}

/** The work folder's tiles of a map set; `key` is the project's, `version` makes the browser load them again. */
export function projectTilesUrl(set: string, key: string, version: number): string {
  return `/api/project/tiles/${set}/{z}/{x}/{y}.png?p=${key}&v=${version}`;
}

/** A map as it was before its tiles were written over since the last export (the others as they are). */
export function beforeLayer(set: string, key: string, version: number, options?: L.TileLayerOptions): L.TileLayer {
  return L.tileLayer(`/api/project/before-tiles/${set}/{z}/{x}/{y}.png?p=${key}&v=${version}`,
    { tileSize: 256, minNativeZoom: 0, maxNativeZoom: 8, maxZoom: 10, noWrap: true, bounds: frameBounds(currentFrame()), ...options });
}

/** A map of the open project over its frame (made again when the frame changes). */
export function projectLayer(set: string, key: string, version: number, options?: L.TileLayerOptions): L.TileLayer {
  return L.tileLayer(projectTilesUrl(set, key, version), { tileSize: 256, minNativeZoom: 0, maxNativeZoom: 8, maxZoom: 10, noWrap: true, bounds: frameBounds(currentFrame()), ...options });
}

/**
 * The colour behind a map's tiles (where it has none: beyond the frame, blocks not captured yet): the colour the
 * project's satellite map paints its open sea with while that map is shown, so the sea goes on past the tiles; null puts
 * the screens' own colour back (the style sheet's --map-sea).
 */
export function setMapBackground(map: L.Map | HTMLElement | null | undefined, colour: string | null): void {
  const el = map instanceof L.Map ? map.getContainer() : map;
  if (el) el.style.background = colour ?? "";
}

/** How long a layer being replaced may wait for its successor's tiles. */
const REPLACE_WAIT_MS = 4000;

/**
 * Puts `next` on the map in place of `previous`. When the same tiles are only loaded again (`reload`), the previous
 * layer stays under the new one until its tiles are in (or a few seconds passed), so the picture does not blink;
 * otherwise it goes at once. Returns `next`.
 */
export function replaceLayer<T extends L.GridLayer>(map: L.Map, previous: L.GridLayer | null, next: T | null, reload: boolean): T | null {
  next?.addTo(map);
  if (previous && previous !== next) {
    if (!reload || !next) previous.remove();
    else {
      let gone = false;
      const drop = () => {
        if (gone) return;
        gone = true;
        previous.remove();
      };
      next.once("load", drop);
      window.setTimeout(drop, REPLACE_WAIT_MS);
    }
  }
  return next;
}

export const blockName = (bx: number, by: number) => `z8_${bx * 4}_${by * 4}`;

/** A block's place in the row-by-row lists of a frame's blocks (BlocksDto), or -1 off the frame. */
export function blockIndexOf(bx: number, by: number, grid: BlockGrid): number {
  const x = bx - grid.bx0, y = by - grid.by0;
  return x >= 0 && x < grid.cols && y >= 0 && y < grid.rows ? y * grid.cols + x : -1;
}

/** The block at a place of the row-by-row lists of a frame's blocks: [bx, by]. */
export function blockOfIndex(i: number, grid: BlockGrid): [number, number] {
  return [grid.bx0 + (i % grid.cols), grid.by0 + Math.floor(i / grid.cols)];
}

/** A block name's place in the row-by-row lists of a frame's blocks (-1 when it is not a block name or off the frame). */
export function blockIndex(name: string, grid: BlockGrid): number {
  const m = /^z8_(-?\d+)_(-?\d+)$/.exec(name);
  return m ? blockIndexOf(Number(m[1]) / 4, Number(m[2]) / 4, grid) : -1;
}

/** The block of the frame at a point, or null off the frame. */
export function blockAt(ll: L.LatLng, grid: BlockGrid): [number, number] | null {
  const bx = Math.floor((ll.lng - LEFT) / BLOCK);
  const by = Math.floor((TOP - ll.lat) / BLOCK);
  return blockIndexOf(bx, by, grid) >= 0 ? [bx, by] : null;
}

export function blockBounds(bx: number, by: number): L.LatLngBoundsLiteral {
  return [
    [TOP - (by + 1) * BLOCK, LEFT + bx * BLOCK],
    [TOP - by * BLOCK, LEFT + (bx + 1) * BLOCK],
  ];
}

export function blockCenter(name: string): L.LatLng | null {
  const m = /^z8_(-?\d+)_(-?\d+)$/.exec(name);
  if (!m) return null;
  const bx = Number(m[1]) / 4, by = Number(m[2]) / 4;
  return L.latLng(TOP - (by + 0.5) * BLOCK, LEFT + (bx + 0.5) * BLOCK);
}

export function cellBounds(r: number, c: number): L.LatLngBoundsLiteral {
  const r0 = r * CELL, c0 = c * CELL;
  return [
    [TOP - (r0 + CELL) * BLOCK, LEFT + c0 * BLOCK],
    [TOP - r0 * BLOCK, LEFT + (c0 + CELL) * BLOCK],
  ];
}

export function sheetBounds(r: number, c: number): L.LatLngBoundsLiteral {
  return [
    [TOP - (r + 1) * SHEET, LEFT + c * SHEET],
    [TOP - r * SHEET, LEFT + (c + 1) * SHEET],
  ];
}

/** The cell of a unit (<c>cell_&lt;r&gt;_&lt;c&gt;</c>, also a map's cell <c>&lt;map&gt;/cell_&lt;r&gt;_&lt;c&gt;</c>): [r, c], or null. */
export function cellOf(unit: string): [number, number] | null {
  const m = /(?:^|\/)cell_(-?\d+)_(-?\d+)$/.exec(unit);
  return m ? [Number(m[1]), Number(m[2])] : null;
}

/** The minimap sheet of a unit (<c>&lt;map&gt;@&lt;size&gt;/&lt;r&gt;_&lt;c&gt;</c>): [r, c], or null. */
export function sheetOf(unit: string): [number, number] | null {
  const m = /@\d+\/(\d+)_(\d+)$/.exec(unit);
  return m ? [Number(m[1]), Number(m[2])] : null;
}

/** The middle of a unit of a run: a block, a cell or a minimap sheet; null for other units (a whole map). */
export function unitCenter(unit: string): L.LatLng | null {
  const cell = cellOf(unit);
  if (cell) return L.latLngBounds(cellBounds(cell[0], cell[1])).getCenter();
  const sheet = sheetOf(unit);
  if (sheet) return L.latLngBounds(sheetBounds(sheet[0], sheet[1])).getCenter();
  return blockCenter(unit);
}
