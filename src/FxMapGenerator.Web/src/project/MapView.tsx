import L from "leaflet";
import "leaflet/dist/leaflet.css";
import { useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { Button, Info, Select } from "../shared/controls";
import { useT } from "../shared/i18n";
import {
  BLOCK,
  blockAt,
  blockBounds,
  blockIndex,
  blockIndexOf,
  blockName,
  blockOfIndex,
  cellBounds,
  cellOf,
  crs,
  currentFrame,
  frameBounds,
  frameKey as frameKeyOf,
  LEFT,
  postalLayer,
  projectKey,
  projectLayer,
  replaceLayer,
  setMapBackground,
  sheetBounds,
  sheetOf,
  TOP,
  unitCenter,
  Z_BASE,
  Z_DRAWN,
  Z_OVER,
} from "../shared/mapFrame";
import type { BlocksDto, JobSnapshot, StageUnits } from "../shared/types";
import { INPUTS, mapName, phaseText } from "./labels";
import { useLock } from "./locks";
import { RangePresetSelect, type PresetArea } from "./RangePresets";
import { jobOfProject, RECT_TOOLS, satelliteProvisional, useProjectStore, type Base, type MapView as View, type Tool } from "./store";

// Block colours. Data: what the work folder holds; run: what the run did with the block.
const COLOR = {
  none: "#8a969c",
  /** The dark line under the light one of blocks without data. */
  halo: "#27313a",
  shot: "#2f7fd6",
  scan: "#e08a1e",
  both: "#8e5bd6",
  retake: "#d33c3c",
  out: "#8a969c",
  select: "#f2c400",
  done: "#2f9e5b",
  active: "#f0a020",
  failed: "#d33c3c",
  left: "#8a969c",
};

type Status = "none" | "shot" | "scan" | "both" | "retake";

function dataStatus(b: BlocksDto, i: number): Status {
  if (b.retake[i] === "1") return "retake";
  const m = b.items[i];
  const shot = (m & 3) === 3, scan = (m & 12) === 12;
  return shot && scan ? "both" : shot ? "shot" : scan ? "scan" : "none";
}

function dataStyle(b: BlocksDto, i: number): L.PathOptions {
  if (b.range[i] === ".") return { stroke: true, color: COLOR.out, weight: 0.5, opacity: 0.35, dashArray: "2 4", fill: false };
  const s = dataStatus(b, i);
  // without data: a light line over a dark one (haloStyle) and a grey tint, so the grid shows on light and dark maps
  if (s === "none") return { stroke: true, color: "#ffffff", weight: 1, opacity: 0.9, dashArray: undefined, fill: true, fillColor: COLOR.none, fillOpacity: 0.12 };
  return { stroke: true, color: COLOR[s], weight: 0.8, opacity: 0.85, dashArray: undefined, fill: true, fillColor: COLOR[s], fillOpacity: 0.2 };
}

/** The dark line drawn under the light line of a block without data (data view only). */
function haloStyle(b: BlocksDto, i: number): L.PathOptions {
  return b.range[i] !== "." && dataStatus(b, i) === "none"
    ? { stroke: true, color: COLOR.halo, weight: 3, opacity: 0.55, fill: false }
    : { stroke: false, fill: false };
}

const NO_HALO: L.PathOptions = { stroke: false, fill: false };

interface RunSets {
  targets: Set<string>;
  done: Set<string>;
  failed: Set<string>;
  active: Set<string>;
}

/** `imagery`: satellite tiles lie under the grid, so finished blocks are only outlined (the picture shows through). */
function runStyle(name: string, inRange: boolean, r: RunSets, imagery: boolean): L.PathOptions {
  if (r.active.has(name)) return { stroke: true, color: COLOR.active, weight: 3, opacity: 1, dashArray: undefined, fill: true, fillColor: COLOR.active, fillOpacity: 0.35 };
  if (r.failed.has(name)) return { stroke: true, color: COLOR.failed, weight: 1.5, opacity: 1, dashArray: undefined, fill: true, fillColor: COLOR.failed, fillOpacity: 0.45 };
  if (r.done.has(name))
    return imagery
      ? { stroke: true, color: COLOR.done, weight: 1.2, opacity: 0.9, dashArray: undefined, fill: false }
      : { stroke: true, color: COLOR.done, weight: 0.8, opacity: 0.9, dashArray: undefined, fill: true, fillColor: COLOR.done, fillOpacity: 0.35 };
  if (r.targets.has(name)) return { stroke: true, color: COLOR.left, weight: 0.8, opacity: 0.9, dashArray: undefined, fill: true, fillColor: COLOR.left, fillOpacity: 0.25 };
  return inRange
    ? { stroke: true, color: COLOR.out, weight: 0.5, opacity: 0.5, dashArray: undefined, fill: false }
    : { stroke: true, color: COLOR.out, weight: 0.5, opacity: 0.25, dashArray: "2 4", fill: false };
}

/** The blocks while the map shows the frames of cells or sheets: only the range, faintly. */
function quietStyle(inRange: boolean): L.PathOptions {
  return inRange
    ? { stroke: true, color: COLOR.out, weight: 0.4, opacity: 0.35, dashArray: undefined, fill: false }
    : { stroke: true, color: COLOR.out, weight: 0.4, opacity: 0.2, dashArray: "2 4", fill: false };
}

/** A frame of a cell or sheet: `drawn` when the finished ones show their tiles (only an outline then). */
function frameStyle(status: "done" | "active" | "failed" | "left" | "out", drawn: boolean): L.PathOptions {
  switch (status) {
    case "active":
      return { color: COLOR.active, weight: 4, opacity: 1, fill: true, fillColor: COLOR.active, fillOpacity: 0.12 };
    case "failed":
      return { color: COLOR.failed, weight: 2.5, opacity: 1, fill: true, fillColor: COLOR.failed, fillOpacity: 0.3 };
    case "done":
      return drawn ? { color: COLOR.done, weight: 2, opacity: 0.9, fill: false } : { color: COLOR.done, weight: 2, opacity: 0.9, fill: true, fillColor: COLOR.done, fillOpacity: 0.25 };
    case "left":
      return { color: COLOR.left, weight: 1.5, opacity: 0.9, dashArray: "6 4", fill: true, fillColor: COLOR.left, fillOpacity: 0.12 };
    case "out":
      return { color: COLOR.out, weight: 1, opacity: 0.35, dashArray: "2 4", fill: false };
  }
}

/** Fits the whole frame into the map below the tool box laid over its top (else the box hides the frame's first rows). */
function fitFrame(map: L.Map, tools: HTMLElement | null) {
  const top = tools ? Math.max(0, tools.getBoundingClientRect().bottom - map.getContainer().getBoundingClientRect().top) : 0;
  map.fitBounds(frameBounds(currentFrame()), { paddingTopLeft: [0, top] });
}

const BLOCKS = new Set(["block"]);
/** Units the map shows by their frame: the cells (their data and drawing) and the minimap sheets. */
const FRAMED = new Set(["cell", "sheet"]);
const CELLS = new Set(["cell"]);
const SHEETS_UNIT = new Set(["sheet"]);
const SHEETS: [number, number][] = [0, 1, 2].flatMap((r) => [0, 1].map((c) => [r, c] as [number, number]));

/** The stage of those units the map shows during (and after) a run: the running one when it is one, else the last one that had units. */
function lastStage(units: StageUnits[] | null, current: string | null, kinds: Set<string>): StageUnits | null {
  if (!units) return null;
  const running = units.find((u) => u.id === current && kinds.has(u.unit));
  if (running) return running;
  return [...units].reverse().find((u) => kinds.has(u.unit) && u.targets.length > 0) ?? null;
}

/** A cell or sheet as a key of its own: "r_c". */
function frameKey(unit: string): string | null {
  const at = cellOf(unit) ?? sheetOf(unit);
  return at ? `${at[0]}_${at[1]}` : null;
}

/**
 * The atlas or road map a run draws (or drew last). It is shown cell by cell, each drawn cell's own tiles only: the lower
 * zooms of the whole map paint the sea outside the range too, which would hide the base map.
 */
function drawnMap(job: JobSnapshot | null): string | null {
  if (!job) return null;
  const drawing = job.stages.filter((s) => s.row.startsWith("cells.") && s.row !== "cells.prep");
  const stage = drawing.find((s) => s.state === "running") ?? [...drawing].reverse().find((s) => s.state !== "waiting");
  return stage ? stage.row.slice(stage.row.indexOf(".") + 1) : null;
}

export function MapView() {
  const t = useT();
  const blocks = useProjectStore((s) => s.blocks);
  const plan = useProjectStore((s) => s.plan);
  const selectedRow = useProjectStore((s) => s.selectedRow);
  const tool = useProjectStore((s) => s.tool);
  const base = useProjectStore((s) => s.base);
  const tileVersion = useProjectStore((s) => s.tileVersion);
  const focus = useProjectStore((s) => s.focus);
  const view = useProjectStore((s) => s.view);
  const units = useProjectStore((s) => s.units);
  const job = useProjectStore((s) => s.job);
  const project = useProjectStore((s) => s.project);
  const { setTool, setBase, setView, edit, retake } = useProjectStore.getState();
  const rangeLock = useLock(INPUTS.range);

  const host = useRef<HTMLDivElement>(null);
  const tools = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const rects = useRef<L.Rectangle[]>([]);
  const halos = useRef<L.Rectangle[]>([]);
  const overlay = useRef<L.LayerGroup | null>(null);
  const frames = useRef<L.LayerGroup | null>(null);
  const postalRef = useRef<L.GridLayer | null>(null);
  const rendererRef = useRef<L.Canvas | null>(null);
  const satelliteRef = useRef<{ layer: L.TileLayer; key: string; version: number; frame: string } | null>(null);
  const cellLayers = useRef(new Map<string, L.TileLayer>());
  const toolRef = useRef<Tool>(tool);
  const blocksRef = useRef<BlocksDto | null>(blocks);
  const lockedRef = useRef(!!rangeLock);
  /** Where to fly once the grid follows a frame just widened (setting the map's bounds stops a flight under way). */
  const pendingFly = useRef<L.LatLngBounds | null>(null);
  const [hover, setHover] = useState<number | null>(null);
  toolRef.current = tool;
  blocksRef.current = blocks;
  lockedRef.current = !!rangeLock;

  // ---- the map, once
  useEffect(() => {
    if (!host.current) return;
    const map = L.map(host.current, {
      crs,
      zoomControl: false,
      attributionControl: false,
      minZoom: -1,
      maxZoom: 10,
      zoomSnap: 0.5,
      maxBounds: frameBounds(currentFrame()).pad(0.25),
      maxBoundsViscosity: 0.8,
    });
    fitFrame(map, tools.current);
    postalRef.current = postalLayer({ zIndex: Z_BASE });
    // the block grid's canvas (the grid follows the project's frame): put on the map first, under the outlines
    const renderer = L.canvas({ padding: 0.2 });
    map.addLayer(renderer);
    rendererRef.current = renderer;
    overlay.current = L.layerGroup().addTo(map);
    frames.current = L.layerGroup().addTo(map);

    // tools: a rectangle to add / take out / mark for retake, a click to switch one block or its retake mark
    let start: L.LatLng | null = null;
    let preview: L.Rectangle | null = null;
    const blocksIn = (a: L.LatLng, b: L.LatLng) => {
      const g = blocksRef.current;
      if (!g) return [];
      const x0 = Math.max(g.bx0, Math.floor((Math.min(a.lng, b.lng) - LEFT) / BLOCK)), x1 = Math.min(g.bx0 + g.cols - 1, Math.floor((Math.max(a.lng, b.lng) - LEFT) / BLOCK));
      const y0 = Math.max(g.by0, Math.floor((TOP - Math.max(a.lat, b.lat)) / BLOCK)), y1 = Math.min(g.by0 + g.rows - 1, Math.floor((TOP - Math.min(a.lat, b.lat)) / BLOCK));
      const names: string[] = [];
      for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) names.push(blockName(x, y));
      return names;
    };
    map.on("mousedown", (e: L.LeafletMouseEvent) => {
      const tl = toolRef.current;
      if (!RECT_TOOLS.has(tl) || lockedRef.current) return;
      start = e.latlng;
      const color = tl === "add" ? COLOR.done : tl === "retake" ? COLOR.retake : COLOR.failed;
      preview = L.rectangle(L.latLngBounds(start, start), { color, weight: 2, dashArray: "6 4", fillOpacity: 0.12, interactive: false }).addTo(map);
    });
    map.on("mousemove", (e: L.LeafletMouseEvent) => {
      const g = blocksRef.current;
      const at = g ? blockAt(e.latlng, g) : null;
      setHover(at && g ? blockIndexOf(at[0], at[1], g) : null);
      if (start && preview) preview.setBounds(L.latLngBounds(start, e.latlng));
    });
    map.on("mouseout", () => setHover(null));
    map.on("mouseup", (e: L.LeafletMouseEvent) => {
      if (!start) return;
      const names = blocksIn(start, e.latlng);
      const tl = toolRef.current;
      start = null;
      preview?.remove();
      preview = null;
      if (tl === "retake") {
        // only the blocks of the range can be taken again
        const b = blocksRef.current;
        const inRange = b ? names.filter((n) => {
          const i = blockIndex(n, b);
          return i >= 0 && b.range[i] !== ".";
        }) : [];
        if (inRange.length > 0) void useProjectStore.getState().retake({ blocks: inRange, retake: true });
      } else if (names.length > 0) void useProjectStore.getState().edit({ range: tl === "add" ? { include: names } : { exclude: names } });
    });
    map.on("click", (e: L.LeafletMouseEvent) => {
      const tl = toolRef.current;
      if ((tl !== "toggle" && tl !== "retakeToggle") || lockedRef.current) return;
      const b = blocksRef.current;
      const at = b ? blockAt(e.latlng, b) : null;
      if (!at || !b) return;
      const name = blockName(at[0], at[1]);
      const i = blockIndexOf(at[0], at[1], b);
      const inRange = b.range[i] !== ".";
      if (tl === "retakeToggle") {
        if (inRange) void useProjectStore.getState().retake({ blocks: [name], retake: b.retake[i] !== "1" });
        return;
      }
      void useProjectStore.getState().edit({ range: inRange ? { exclude: [name] } : { include: [name] } });
    });

    const resize = new ResizeObserver(() => map.invalidateSize());
    resize.observe(host.current);
    mapRef.current = map;
    const drawnCells = cellLayers.current;
    return () => {
      resize.disconnect();
      map.remove();
      mapRef.current = null;
      rendererRef.current = null;
      satelliteRef.current = null;
      halos.current = [];
      rects.current = [];
      drawnCells.clear();
    };
  }, []);

  // ---- the block grid over the project's frame, made again when the frame changes; the halos first (the canvas draws in
  // the order the shapes were added)
  const gridKey = frameKeyOf(blocks);
  useEffect(() => {
    const map = mapRef.current, renderer = rendererRef.current;
    if (!map || !renderer) return;
    for (const r of [...halos.current, ...rects.current]) r.remove();
    halos.current = [];
    rects.current = [];
    map.setMaxBounds(frameBounds(currentFrame()).pad(0.25));
    if (!gridKey) return;
    const [bx0, by0, cols, rows] = gridKey.split(",").map(Number);
    const under: L.Rectangle[] = [];
    const list: L.Rectangle[] = [];
    for (let by = by0; by < by0 + rows; by++)
      for (let bx = bx0; bx < bx0 + cols; bx++) under.push(L.rectangle(blockBounds(bx, by), { renderer, interactive: false, ...NO_HALO }).addTo(map));
    for (let by = by0; by < by0 + rows; by++)
      for (let bx = bx0; bx < bx0 + cols; bx++)
        list.push(L.rectangle(blockBounds(bx, by), { renderer, interactive: false, color: COLOR.out, weight: 0.5, opacity: 0.35, dashArray: "2 4", fill: false }).addTo(map));
    halos.current = under;
    rects.current = list;
    if (pendingFly.current) {
      map.flyToBounds(pendingFly.current, { padding: [40, 40], duration: 0.6 });
      pendingFly.current = null;
    }
  }, [gridKey]);

  // ---- tools switch the map's own dragging off
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (RECT_TOOLS.has(tool)) map.dragging.disable();
    else map.dragging.enable();
    host.current?.classList.toggle("map-drawing", tool !== "pan");
  }, [tool]);

  // ---- what the map shows of a run: the blocks of a block step (the visit, the orthorectification, the landcover), or
  // the frames of the cells or minimap sheets while those steps run (and after the run, the last of them)
  const mine = jobOfProject(job, project);
  const current = mine ? (job?.stage ?? null) : null;
  const currentUnits = mine ? (units?.find((u) => u.id === current) ?? null) : null;
  const blockRun = mine ? lastStage(units, current, BLOCKS) : null;
  // after the run: the cells last drawn rather than the minimap's sheets
  const frameRun = !mine ? null
    : currentUnits && FRAMED.has(currentUnits.unit) ? currentUnits
    : currentUnits?.unit === "block" ? null
    : current ? lastStage(units, null, FRAMED)
    : (lastStage(units, null, CELLS) ?? lastStage(units, null, SHEETS_UNIT));
  const runShown = frameRun ?? blockRun;
  const inRun = view === "run" && !!runShown;
  const stage = inRun && !frameRun ? blockRun : null;
  const runSets = useMemo<RunSets | null>(() => {
    if (!stage) return null;
    const active = new Set((job?.active ?? []).filter((a) => a.stage === stage.id).map((a) => a.unit));
    return { targets: new Set(stage.targets), done: new Set(stage.done), failed: new Set(stage.failed), active };
  }, [stage, job]);
  const drawn = inRun && mine ? drawnMap(job) : null;

  // Our satellite tiles: the base when chosen, and laid over the base while the map shows a run of a project that
  // makes the satellite map (blocks shot in the run appear as they are orthorectified).
  const key = project ? projectKey(project.workFolder) : "";
  const imagery = base === "satellite" || (inRun && !!project?.file.maps.satellite);
  const provisional = satelliteProvisional(plan, blocks);

  // ---- the tile layers; a new version (or project) of our tiles replaces the layer without blinking
  useEffect(() => {
    const map = mapRef.current, postal = postalRef.current;
    if (!map || !postal) return;
    if (base === "postal" && !map.hasLayer(postal)) postal.addTo(map);
    if (base !== "postal" && map.hasLayer(postal)) postal.remove();
    const current = satelliteRef.current;
    if (!imagery || !key) {
      current?.layer.remove();
      satelliteRef.current = null;
      return;
    }
    if (current && current.key === key && current.version === tileVersion && current.frame === gridKey) return;
    // A new layer rather than Leaflet's redraw(): at a fractional zoom (1.5) redraw() asks for tiles of zoom "1.5".
    const next = projectLayer("satellite", key, tileVersion, { zIndex: Z_OVER });
    replaceLayer(map, current?.layer ?? null, next, current?.key === key);
    satelliteRef.current = { layer: next, key, version: tileVersion, frame: gridKey };
  }, [base, imagery, key, tileVersion, gridKey]);

  // ---- behind the tiles: the satellite map's own sea colour while it is shown
  const sea = blocks?.satelliteSea ?? null;
  useEffect(() => {
    setMapBackground(host.current, imagery ? sea : null);
  }, [imagery, sea]);

  // ---- the map a run draws: each cell's tiles as soon as the cell is drawn, bounded to the cell (the cell drawing writes
  // z8 to z5, so a cell's layer takes z5 below that); a layer is added once per cell and run
  const drawnCells = drawn ? (units?.find((u) => u.row === "cells." + drawn)?.done ?? []) : [];
  const drawnCellsKey = drawnCells.join(",");
  const runId = job?.runId ?? "";
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    const layers = cellLayers.current;
    const wanted = new Map<string, [number, number]>();
    for (const u of drawnCellsKey ? drawnCellsKey.split(",") : []) {
      const at = cellOf(u);
      if (at && drawn) wanted.set(`${key}|${runId}|${drawn}|${at[0]}_${at[1]}`, at);
    }
    for (const [id, layer] of layers)
      if (!wanted.has(id)) {
        layer.remove();
        layers.delete(id);
      }
    for (const [id, [r, c]] of wanted)
      if (!layers.has(id)) layers.set(id, projectLayer(drawn!, key, tileVersion, { zIndex: Z_DRAWN, bounds: cellBounds(r, c), minNativeZoom: 5 }).addTo(map));
    // (the tile version is read, not followed: a cell's layer is made once, when the cell is done)
  }, [drawnCellsKey, drawn, key, runId]);

  // ---- block colours
  useEffect(() => {
    if (!blocks) return;
    const quiet = inRun && !!frameRun;
    rects.current.forEach((r, i) => {
      const [bx, by] = blockOfIndex(i, blocks);
      const name = blockName(bx, by);
      const inRange = blocks.range[i] !== ".";
      r.setStyle(quiet ? quietStyle(inRange) : runSets ? runStyle(name, inRange, runSets, imagery) : dataStyle(blocks, i));
    });
    halos.current.forEach((h, i) => h.setStyle(runSets || quiet ? NO_HALO : haloStyle(blocks, i)));
  }, [blocks, runSets, imagery, inRun, frameRun]);

  // ---- the frames of the cells or sheets of a run, with the step a unit being worked on is at
  useEffect(() => {
    const group = frames.current;
    if (!group) return;
    group.clearLayers();
    if (!inRun || !frameRun || !blocks) return;
    const targets = new Set(frameRun.targets.map(frameKey));
    const done = new Set(frameRun.done.map(frameKey));
    const failed = new Set(frameRun.failed.map(frameKey));
    const active = new Map<string, string | null>();
    for (const a of job?.active ?? []) if (a.stage === frameRun.id) active.set(frameKey(a.unit) ?? "", a.phase);
    const sheets = frameRun.unit === "sheet";
    const all: [number, number][] = sheets ? SHEETS : blocks.cells.map((c) => cellOf(c)).filter((c): c is [number, number] => !!c);
    // the finished cells of the drawing show their tiles: an outline is enough there
    const drawing = !sheets && frameRun.row !== "cells.prep";
    for (const [r, c] of all) {
      const k = `${r}_${c}`;
      const status = active.has(k) ? "active" : failed.has(k) ? "failed" : done.has(k) ? "done" : targets.has(k) ? "left" : "out";
      const bounds = sheets ? sheetBounds(r, c) : cellBounds(r, c);
      L.rectangle(bounds, { interactive: false, ...frameStyle(status, drawing || sheets) }).addTo(group);
      if (status === "active")
        L.tooltip({ permanent: true, direction: "center", className: "unit-label" })
          .setLatLng(L.latLngBounds(bounds).getCenter())
          .setContent(phaseText(t, active.get(k) ?? null) || t("legend.active"))
          .addTo(group);
    }
  }, [inRun, frameRun, blocks, job?.active, t]);

  // ---- the selected to-do row's blocks or cells
  useEffect(() => {
    const group = overlay.current;
    if (!group) return;
    group.clearLayers();
    if (!selectedRow || !plan || inRun) return;
    const row = plan.table.rows.flatMap((r) => [r, ...r.children]).find((r) => r.id === selectedRow);
    for (const target of row?.targets ?? []) {
      const b = /^z8_(-?\d+)_(-?\d+)$/.exec(target);
      const c = /^cell_(-?\d+)_(-?\d+)$/.exec(target);
      const sh = /^sheet_(\d+)_(\d+)$/.exec(target);
      if (b) L.rectangle(blockBounds(Number(b[1]) / 4, Number(b[2]) / 4), { color: COLOR.select, weight: 2.5, fill: false, interactive: false }).addTo(group);
      else if (c) L.rectangle(cellBounds(Number(c[1]), Number(c[2])), { color: COLOR.select, weight: 3, fill: false, interactive: false }).addTo(group);
      else if (sh) L.rectangle(sheetBounds(Number(sh[1]), Number(sh[2])), { color: COLOR.select, weight: 3, dashArray: "8 6", fill: false, interactive: false }).addTo(group);
    }
  }, [selectedRow, plan, inRun]);

  // ---- move to a block, cell or sheet (from the list of units being worked on): a cell or sheet fills the map
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !focus) return;
    const cell = cellOf(focus.name), sheet = sheetOf(focus.name);
    if (cell || sheet) {
      map.flyToBounds(L.latLngBounds(cell ? cellBounds(cell[0], cell[1]) : sheetBounds(sheet![0], sheet![1])), { padding: [40, 40], duration: 0.6 });
      return;
    }
    const at = unitCenter(focus.name);
    if (at) map.flyTo(at, Math.max(map.getZoom(), 6), { duration: 0.6 });
  }, [focus]);

  const toolButton = (id: Tool, label: string, help: Parameters<typeof Button>[0]["help"]) => (
    <Button help={help} pressed={tool === id} disabledReason={id === "pan" ? null : rangeLock} onClick={() => setTool(tool === id ? "pan" : id)}>
      {label}
    </Button>
  );

  const views: { value: View; label: string }[] = [
    { value: "data", label: t("view.data") },
    { value: "run", label: t("view.run") },
  ];
  const bases: { value: Base; label: string }[] = [
    { value: "postal", label: t("base.postal") },
    { value: "satellite", label: t("base.satellite") },
    { value: "none", label: t("base.none") },
  ];

  const marked = blocks ? [...blocks.retake].filter((c) => c === "1").length : 0;

  /** After a range preset went in: the map moves to its blocks (once the grid follows the frame, when it was widened). */
  const flyToPreset = (a: PresetArea) => {
    const target = L.latLngBounds([a.south, a.west], [a.north, a.east]);
    const f = currentFrame(), g = blocksRef.current;
    if (!g || g.bx0 !== f.bx0 || g.by0 !== f.by0 || g.cols !== f.cols || g.rows !== f.rows) pendingFly.current = target;
    else mapRef.current?.flyToBounds(target, { padding: [40, 40], duration: 0.6 });
  };

  const hoverText = (() => {
    if (hover === null || !blocks) return t("map.hover.none");
    const [hx, hy] = blockOfIndex(hover, blocks);
    const name = blockName(hx, hy);
    const cls = blocks.range[hover] === "L" ? t("cls.land") : blocks.range[hover] === "W" ? t("cls.water") : t("cls.out");
    const m = blocks.items[hover];
    const items = [
      [1, "item.shot"],
      [2, "item.height"],
      [4, "item.scanGround"],
      [8, "item.scanRoads"],
      [16, "item.scanCanopy"],
    ] as const;
    const have = items.filter(([bit]) => m & bit).map(([, key]) => t(key));
    const failure = runSets?.failed.has(name) ? job?.failures.find((f) => f.unit === name) : undefined;
    return (
      t("map.hover", {
        block: name,
        cls,
        items: blocks.retake[hover] === "1" ? t("map.hover.retake") : have.length ? have.join("・") : t("map.hover.nothing"),
        tiles: blocks.ortho[hover] === "1" ? t(provisional ? "map.hover.tilesProvisional" : "map.hover.tiles") : "",
      }) + (failure ? t("map.hover.failed", { message: failure.message }) : "")
    );
  })();

  return (
    <div className="map-wrap" data-guide="project.map">
      <div className="map-area">
        <div ref={host} className="map" />
        {imagery && provisional && (
          <Info help="help.map.provisional" className="map-badge">
            {t("map.provisional")}
          </Info>
        )}
        {drawn && (
          <Info help="help.map.drawn" className="map-badge map-badge-drawn">
            {t("map.drawn.cells", { map: mapName(t, drawn) })}
          </Info>
        )}
      </div>
      <div ref={tools} className="map-tools">
        {toolButton("pan", t("tool.pan"), "help.tool.pan")}
        {toolButton("add", t("tool.add"), "help.tool.add")}
        {toolButton("remove", t("tool.remove"), "help.tool.remove")}
        {toolButton("toggle", t("tool.toggle"), "help.tool.toggle")}
        <Button help="help.tool.reset" disabledReason={rangeLock} onClick={() => void edit({ range: { reset: true } })}>
          {t("tool.reset")}
        </Button>
        <RangePresetSelect onAdded={flyToPreset} />
        <span className="map-tools-break" />
        {toolButton("retake", t("tool.retake"), "help.tool.retake")}
        {toolButton("retakeToggle", t("tool.retakeToggle"), "help.tool.retakeToggle")}
        <Button help="help.tool.retakeClear" disabledReason={rangeLock ?? (marked === 0 ? "reason.noRetake" : null)} onClick={() => void retake({ all: true })}>
          {t("tool.retakeClear")}
        </Button>
        <span className="map-tools-gap" />
        <span className="map-tools-gap" />
        <Button help="help.tool.zoomIn" onClick={() => mapRef.current?.zoomIn()}>
          +
        </Button>
        <Button help="help.tool.zoomOut" onClick={() => mapRef.current?.zoomOut()}>
          −
        </Button>
        <Button help="help.tool.fit" onClick={() => mapRef.current && fitFrame(mapRef.current, tools.current)}>
          {t("tool.fit")}
        </Button>
        <Select help="help.map.base" label={t("base.label")} value={base} options={bases} onChange={setBase} />
        <Select
          help="help.map.view"
          label={t("view.label")}
          value={runShown ? view : "data"}
          options={views}
          disabledReason={runShown ? null : "reason.noRun"}
          onChange={setView}
        />
      </div>
      <div className="map-foot">
        <Info help="help.map.hover" className="map-hover">
          {hoverText}
        </Info>
        <Legend run={inRun} imagery={imagery || (!!frameRun && frameRun.row !== "cells.prep")} />
      </div>
    </div>
  );
}

/** Legend swatches: a filled square, or the outline the block gets. */
const FILLED = (color: string): CSSProperties => ({ background: color, borderColor: color });
const OUTLINE = (color: string): CSSProperties => ({ borderColor: color });

function Legend({ run, imagery }: { run: boolean; imagery: boolean }) {
  const t = useT();
  const item = (swatch: CSSProperties, text: string, help: Parameters<typeof Info>[0]["help"]) => (
    <Info help={help} className="legend-item">
      <span className="legend-swatch" style={swatch} />
      {text}
    </Info>
  );
  return run ? (
    <div className="legend">
      {item(imagery ? OUTLINE(COLOR.done) : FILLED(COLOR.done), t("legend.done"), "help.legend.done")}
      {item(FILLED(COLOR.active), t("legend.active"), "help.legend.active")}
      {item(FILLED(COLOR.failed), t("legend.failed"), "help.legend.failed")}
      {item(FILLED(COLOR.left), t("legend.left"), "help.legend.left")}
      {item(OUTLINE(COLOR.out), t("legend.notTarget"), "help.legend.notTarget")}
    </div>
  ) : (
    <div className="legend">
      {item({ background: "rgba(138, 150, 156, 0.3)", borderColor: "#ffffff", boxShadow: `0 0 0 1.5px ${COLOR.halo}` }, t("legend.none"), "help.legend.none")}
      {item(FILLED(COLOR.shot), t("legend.shot"), "help.legend.shot")}
      {item(FILLED(COLOR.scan), t("legend.scan"), "help.legend.scan")}
      {item(FILLED(COLOR.both), t("legend.both"), "help.legend.both")}
      {item(FILLED(COLOR.retake), t("legend.retake"), "help.legend.retake")}
      {item(OUTLINE(COLOR.out), t("legend.out"), "help.legend.out")}
    </div>
  );
}
