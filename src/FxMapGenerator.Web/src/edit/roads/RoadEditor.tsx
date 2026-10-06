import L from "leaflet";
import { useEffect, useMemo, useRef, useState } from "react";
import { api, ApiError } from "../../shared/api";
import { Button, Check, type HelpKey, Info } from "../../shared/controls";
import { useT } from "../../shared/i18n";
import { currentFrame, frameBounds, projectKey, setMapBackground, Z_BASE, Z_OVER } from "../../shared/mapFrame";
import { mapName } from "../../project/labels";
import { useProjectStore } from "../../project/store";
import { areaSelect, backgroundLayer, NONE, POSTAL, useEditMaps, useLayer } from "../shared/editMaps";
import { addItems, type Base, GAME_KINDS, hide, isAdded, linkHidden, moveNodes, nodePos, pairKey, removeAdded, sameEdits, type ShownKind, splitLink, type View } from "./model";
import { COLOURS, createPathsLayer, inPolygon, inRectangle, pick, type PathsLayer, type PathsState, type Picked, roadOf } from "./pathsLayer";
import { RoadPanel } from "./RoadPanel";
import { roadShapesLayer } from "./shapesLayer";
import { type RoadSelection, TOOLS, useRoadEditor } from "./store";

/** Settings of the road editor's maps, kept while the app is open. */
interface Display {
  background: string | null;
  shapesMap: string | null;
  shapesOpacity: number;
}
const display: Display = { background: null, shapesMap: null, shapesOpacity: 85 };

/** How long the right map waits after an edit before computing the road shapes again. */
const PREVIEW_WAIT_MS = 500;

/** New links: one lane each way, not narrow, the width from the lanes. */
const NEW_LINK = { lanesForward: 1, lanesBack: 1, narrow: false, width: null };

/** The ground height at a point, else a fallback. */
async function groundZ(x: number, y: number, fallback: number): Promise<number> {
  try {
    const g = await api.roadGround(x, y);
    return g.height ?? fallback;
  } catch {
    return fallback;
  }
}

/**
 * The road editor (the editing tab's "roads"): the game's road nodes and links on the left map, where they
 * are edited, and the road shapes the maps draw from them on the right map; the side panel holds the tools, the values
 * of what is selected and the list of edits.
 */
export function RoadEditor() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const tileVersion = useProjectStore((s) => s.tileVersion);
  const st = useRoadEditor();
  const leftHost = useRef<HTMLDivElement>(null);
  const rightHost = useRef<HTMLDivElement>(null);
  const overviewHost = useRef<HTMLDivElement>(null);
  const maps = useEditMaps(leftHost, rightHost, overviewHost);
  const [tiles, setTiles] = useState<string[] | null>(null);
  const [background, setBackground] = useState<string | null>(display.background);
  const [shapesMap, setShapesMap] = useState<string | null>(display.shapesMap);
  const [shapesOpacity, setShapesOpacity] = useState(display.shapesOpacity);
  const [hover, setHover] = useState<Picked | null>(null);
  // Ctrl held: the road tool's hover and click take the whole street
  const [ctrl, setCtrl] = useState(false);
  const key = projectKey(project.workFolder);

  // after a run (new tiles, new road shapes) read the status again
  useEffect(() => {
    if (tileVersion > 0) void useRoadEditor.getState().refresh();
  }, [tileVersion]);
  useEffect(() => {
    void api.exportInfo().then((i) => setTiles(i.maps.map((m) => m.map)), () => setTiles([]));
  }, [project.path, tileVersion]);

  const backgrounds = useMemo(() => [...(tiles ?? []), POSTAL, NONE], [tiles]);
  const bg = background && backgrounds.includes(background) ? background : (tiles ?? []).includes("satellite") ? "satellite" : POSTAL;
  const maps2 = st.status?.maps ?? [];
  const sm = shapesMap && maps2.includes(shapesMap) ? shapesMap : (maps2[0] ?? null);
  display.background = background;
  display.shapesMap = shapesMap;
  display.shapesOpacity = shapesOpacity;

  useLayer(maps?.left ?? null, () => backgroundLayer(bg, key, tileVersion, { zIndex: Z_BASE }), [bg, key, tileVersion]);
  useLayer(maps?.right ?? null, () => backgroundLayer(bg, key, tileVersion, { zIndex: Z_BASE }), [bg, key, tileVersion]);
  useLayer(maps?.overview ?? null, () => backgroundLayer(bg === NONE ? POSTAL : bg, key, tileVersion, { zIndex: Z_BASE }), [bg, key, tileVersion]);
  // behind the tiles: the satellite map's own sea colour while it is the background
  const sea = useProjectStore((s) => s.blocks?.satelliteSea ?? null);
  useEffect(() => {
    for (const m of [maps?.left, maps?.right, maps?.overview]) setMapBackground(m, bg === "satellite" ? sea : null);
  }, [maps, bg, sea]);
  // ---- the right map follows the edits: the road shapes the next run would make, computed 0.5 s after the last change
  // (a newer change cancels one being computed); the shapes last made while the edits are those they were made from
  const [preview, setPreview] = useState<{ id: string; seconds: number; provisional: boolean } | null>(null);
  const [computing, setComputing] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const followsSaved = sameEdits(st.edits, st.saved) && !!st.status && !st.status.shapesLeft;
  useEffect(() => {
    if (!st.base || !st.status || st.status.unavailable) return;
    // a failure belongs to the edits it was computed for: the next edits (or the saved ones) start without it
    setPreviewError(null);
    if (followsSaved) {
      setPreview(null);
      setComputing(false);
      return;
    }
    let live = true;
    setComputing(true);
    const timer = window.setTimeout(() => {
      api.roadPreview(st.edits).then(
        (p) => {
          if (!live) return;
          setPreview(p);
          setComputing(false);
          setPreviewError(null);
        },
        (e) => {
          if (!live || (e instanceof ApiError && e.code === "SUPERSEDED")) return;
          setComputing(false);
          setPreviewError(e instanceof ApiError && e.code === "INVALID" ? t("roads.shapes.invalid", { message: e.message }) : e instanceof Error ? e.message : String(e));
        },
      );
    }, PREVIEW_WAIT_MS);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [st.edits, st.base, st.status, followsSaved]);
  const shapesVersion = followsSaved ? (st.status?.shapesVersion ?? null) : (preview?.id ?? st.status?.shapesVersion ?? null);
  const shapes = useLayer(
    maps?.right ?? null,
    () => (sm && shapesVersion ? roadShapesLayer(sm, key, shapesVersion, { zIndex: Z_OVER, opacity: shapesOpacity / 100 }) : null),
    [sm, key, shapesVersion],
  );
  useEffect(() => {
    shapes.current?.setOpacity(shapesOpacity / 100);
  }, [shapesOpacity, shapes]);

  // ---- the paths layer on the left map
  const layerRef = useRef<PathsLayer | null>(null);
  const pathsState = useRef<PathsState>({ base: null, view: null, selection: { nodes: new Set(), links: new Set() }, hover: null, hoverSet: null, drag: null, sketch: null, shown: st.shown });
  useEffect(() => {
    if (!maps) return;
    const layer = createPathsLayer(pathsState.current);
    layer.setZIndex(Z_OVER);
    layer.addTo(maps.left);
    layerRef.current = layer;
    return () => {
      layer.remove();
      layerRef.current = null;
    };
  }, [maps]);
  useEffect(() => {
    const s = pathsState.current;
    s.base = st.base;
    s.view = st.view;
    s.selection = { nodes: new Set(st.selection.nodes), links: new Set(st.selection.links) };
    s.hover = hover;
    s.shown = st.shown;
    // the road tool marks what a click would take: the road up to its branches, or with Ctrl the whole street
    const road = st.tool === "road" && hover && st.base && st.view ? roadOf(st.base, st.view, st.shown, hover, ctrl) : null;
    s.hoverSet = road ? { nodes: new Set(road.nodes), links: new Set(road.links) } : null;
    layerRef.current?.repaint();
  }, [st.base, st.view, st.selection, hover, st.shown, st.tool, ctrl]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => setCtrl(e.ctrlKey);
    const onBlur = () => setCtrl(false);
    window.addEventListener("keydown", onKey);
    window.addEventListener("keyup", onKey);
    window.addEventListener("blur", onBlur);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("keyup", onKey);
      window.removeEventListener("blur", onBlur);
    };
  }, []);

  // ---- the tools on the left map
  const toolRef = useRef(st.tool);
  toolRef.current = st.tool;
  useEffect(() => {
    const map = maps?.left;
    if (!map) return;
    const host = map.getContainer();
    host.classList.toggle("map-drawing", st.tool === "draw" || st.tool === "box");
    if (st.tool === "box") map.dragging.disable();
    else map.dragging.enable();
    if (st.tool !== "draw") {
      pathsState.current.sketch = null;
      layerRef.current?.repaint();
    }
  }, [maps, st.tool]);

  useEffect(() => {
    const map = maps?.left;
    if (!map) return;
    const state = () => useRoadEditor.getState();
    const at = (e: L.LeafletMouseEvent) => ({ x: e.latlng.lng, y: e.latlng.lat });
    const pickAt = (e: L.LeafletMouseEvent): Picked | null => {
      const { base, view, shown } = state();
      if (!base || !view) return null;
      const p = at(e);
      return pick(base, view, p.x, p.y, map.getZoom(), shown);
    };
    const selectPicked = (p: Picked | null, add: boolean) => {
      const cur = state().selection;
      if (!p) {
        if (!add) state().select({ nodes: [], links: [] });
        return;
      }
      const list = p.kind === "node" ? cur.nodes : cur.links;
      const has = list.includes(p.key);
      const next = add ? (has ? list.filter((k) => k !== p.key) : [...list, p.key]) : [p.key];
      state().select(p.kind === "node" ? { nodes: next, links: add ? cur.links : [] } : { nodes: add ? cur.nodes : [], links: next });
    };

    // hover
    let frame = 0;
    let lastHover = "";
    const onMove = (e: L.LeafletMouseEvent) => {
      setCtrl((e.originalEvent as MouseEvent).ctrlKey);
      const sketch = pathsState.current.sketch;
      if (toolRef.current === "draw" && state().chain) {
        pathsState.current.sketch = { from: state().chain!, ...at(e) };
      } else if (sketch) pathsState.current.sketch = null;
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        const p = drag ? null : pickAt(e);
        const k = p ? p.key : "";
        if (k !== lastHover) {
          lastHover = k;
          setHover(p);
        } else if (toolRef.current === "draw") layerRef.current?.repaint();
      });
    };

    const selectFound = (got: RoadSelection, add: boolean) => {
      const { selection } = state();
      state().select(add ? { nodes: [...new Set([...selection.nodes, ...got.nodes])], links: [...new Set([...selection.links, ...got.links])] } : got);
    };
    // select area: a rectangle, or with Ctrl the outline traced while dragging (the editing screens' shared part)
    const stopArea = areaSelect(
      map,
      () => toolRef.current === "box",
      COLOURS.selected,
      (west, south, east, north, add) => {
        const { base, view, shown } = state();
        if (base && view) selectFound(inRectangle(base, view, shown, west, south, east, north), add);
      },
      (corners, add) => {
        const { base, view, shown } = state();
        if (base && view) selectFound(inPolygon(base, view, shown, corners), add);
      },
    );
    // dragging nodes
    let drag: { start: L.LatLng; nodes: Map<string, { x: number; y: number }> } | null = null;

    const onDown = (e: L.LeafletMouseEvent) => {
      const tool = toolRef.current;
      const button = (e.originalEvent as MouseEvent).button;
      if (button !== 0 || tool === "box") return;
      if (tool === "move") {
        const p = pickAt(e);
        const { base, view, selection } = state();
        if (!p || !base || !view) return;
        // the whole selection moves when the item pressed is part of it
        const inSel = p.kind === "node" ? selection.nodes.includes(p.key) : selection.links.includes(p.key);
        const keys = new Set<string>();
        const addLink = (k: string) => k.split(" ").forEach((n) => keys.add(n));
        if (inSel) {
          selection.nodes.forEach((n) => keys.add(n));
          selection.links.forEach(addLink);
        } else if (p.kind === "node") keys.add(p.key);
        else addLink(p.key);
        const nodes = new Map<string, { x: number; y: number }>();
        for (const k of keys) {
          const n = nodePos(base, view, k);
          if (n && !n.hidden) nodes.set(k, { x: n.x, y: n.y });
        }
        if (nodes.size === 0) return;
        map.dragging.disable();
        drag = { start: e.latlng, nodes };
        if (!inSel) selectPicked(p, false);
      }
    };
    const onDragMove = (e: L.LeafletMouseEvent) => {
      if (drag) {
        const dx = e.latlng.lng - drag.start.lng, dy = e.latlng.lat - drag.start.lat;
        const shown = new Map<string, { x: number; y: number }>();
        for (const [k, p] of drag.nodes) shown.set(k, { x: p.x + dx, y: p.y + dy });
        pathsState.current.drag = shown;
        layerRef.current?.repaint();
      }
    };
    const onUp = async (e: L.LeafletMouseEvent) => {
      if (drag) {
        const d = drag;
        drag = null;
        map.dragging.enable();
        const dx = e.latlng.lng - d.start.lng, dy = e.latlng.lat - d.start.lat;
        const shown = pathsState.current.drag;
        if (Math.hypot(dx, dy) < 1e-6 || !shown) {
          pathsState.current.drag = null;
          layerRef.current?.repaint();
          return;
        }
        // an added node takes the ground's height where it lands; a node of the game keeps its own
        const moves = await Promise.all(
          [...shown].map(async ([k, p]) => ({ key: k, x: p.x, y: p.y, z: isAdded(k) ? await groundZ(p.x, p.y, nodePos(state().base!, state().view!, k)?.z ?? 0) : undefined })),
        );
        pathsState.current.drag = null;
        const { base, edits } = state();
        if (base) state().change(moveNodes(base, edits, moves));
      }
    };
    const onClick = async (e: L.LeafletMouseEvent) => {
      const tool = toolRef.current;
      const shift = (e.originalEvent as MouseEvent).shiftKey;
      if (tool === "select" || tool === "move") {
        selectPicked(pickAt(e), shift);
        return;
      }
      if (tool === "road") {
        const p = pickAt(e);
        const { base, view, shown } = state();
        if (!p || !base || !view) {
          if (!shift) state().select({ nodes: [], links: [] });
          return;
        }
        selectFound(roadOf(base, view, shown, p, (e.originalEvent as MouseEvent).ctrlKey), shift);
        return;
      }
      if (tool === "draw") await drawAt(e);
    };
    const drawAt = async (e: L.LeafletMouseEvent) => {
      const { base, view, edits, chain } = state();
      if (!base || !view) return;
      // hidden nodes and links are not joined to nor split: a click there places a node of its own
      let p = pickAt(e);
      if (p?.kind === "node" && nodePos(base, view, p.key)?.hidden) p = null;
      if (p?.kind === "link") {
        const j = base.pairs.get(p.key);
        if (j !== undefined && linkHidden(base, view, j)) p = null;
      }
      const point = at(e);
      const from = chain ? nodePos(base, view, chain) : null;
      if (p?.kind === "node") {
        const target = nodePos(base, view, p.key);
        if (!target || p.key === chain) {
          state().setChain(p.key === chain ? null : chain);
          return;
        }
        if (chain && from && !linked(base, view, chain, p.key)) {
          state().change(addItems(base, edits, [], [{ from: chain, to: p.key, ...NEW_LINK }]), { nodes: [p.key], links: [] });
        } else state().select({ nodes: [p.key], links: [] });
        state().setChain(p.key);
        return;
      }
      const n = state().takeNumber();
      const keyNew = `added:${n}`;
      if (p?.kind === "link") {
        // split the link at the point nearest to the click; the new node takes the link's height there
        const a = nodePos(base, view, p.from)!, b = nodePos(base, view, p.to)!;
        const dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
        const tt = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((point.x - a.x) * dx + (point.y - a.y) * dy) / len2));
        const node = { key: keyNew, x: a.x + tt * dx, y: a.y + tt * dy, z: a.z + tt * (b.z - a.z) };
        let next = splitLink(base, view, edits, p.key, node);
        // a link the chain's last node ends is joined to the new node by the split itself
        if (chain && from && chain !== p.from && chain !== p.to) next = addItems(base, next, [], [{ from: chain, to: keyNew, ...NEW_LINK }]);
        state().change(next, { nodes: [keyNew], links: [] });
        state().setChain(keyNew);
        return;
      }
      const z = await groundZ(point.x, point.y, from?.z ?? 0);
      const s2 = state();
      const next = addItems(base, s2.edits, [{ key: keyNew, x: point.x, y: point.y, z }], chain && from ? [{ from: chain, to: keyNew, ...NEW_LINK }] : []);
      s2.change(next, { nodes: [keyNew], links: [] });
      s2.setChain(keyNew);
    };
    const onContext = (e: L.LeafletMouseEvent) => {
      if (toolRef.current === "draw") {
        e.originalEvent.preventDefault();
        state().setChain(null);
        pathsState.current.sketch = null;
        layerRef.current?.repaint();
      }
    };
    const onLeave = () => {
      if (lastHover) {
        lastHover = "";
        setHover(null);
      }
    };
    map.on("mousemove", onMove);
    map.on("mousemove", onDragMove);
    map.on("mousedown", onDown);
    map.on("mouseup", onUp);
    map.on("click", onClick);
    map.on("contextmenu", onContext);
    map.on("mouseout", onLeave);
    return () => {
      stopArea();
      map.off("mousemove", onMove);
      map.off("mousemove", onDragMove);
      map.off("mousedown", onDown);
      map.off("mouseup", onUp);
      map.off("click", onClick);
      map.off("contextmenu", onContext);
      map.off("mouseout", onLeave);
      if (frame) cancelAnimationFrame(frame);
    };
  }, [maps]);

  // ---- keys: 1 to 5 choose the tool, Delete removes / hides the selection, Ctrl+Z / Ctrl+Y, Esc ends a chain (none
  // while typing into a field, while a window is open over the screen or while a guide is shown; a check box just
  // clicked keeps them)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (typing(e.target) || document.querySelector(".modal-backdrop, .driver-active")) return;
      const s = useRoadEditor.getState();
      const tool = /^[1-9]$/.test(e.key) ? TOOLS[Number(e.key) - 1] : undefined;
      if (tool && !e.ctrlKey && !e.metaKey && !e.altKey) {
        s.setTool(tool);
      } else if ((e.ctrlKey || e.metaKey) && (e.key === "z" || e.key === "Z")) {
        e.preventDefault();
        if (e.shiftKey) s.redo();
        else s.undo();
      } else if ((e.ctrlKey || e.metaKey) && (e.key === "y" || e.key === "Y")) {
        e.preventDefault();
        s.redo();
      } else if (e.key === "Escape") {
        if (s.chain) s.setChain(null);
        else s.select({ nodes: [], links: [] });
        pathsState.current.sketch = null;
        layerRef.current?.repaint();
      } else if (e.key === "Delete" || e.key === "Backspace") {
        if (s.selection.nodes.length + s.selection.links.length === 0) return;
        e.preventDefault();
        removeOrHide(s.selection);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const flyTo = (b: [number, number, number, number]) => {
    const map = maps?.left;
    if (!map || !Number.isFinite(b[0])) return;
    map.fitBounds(L.latLngBounds([b[1], b[0]], [b[3], b[2]]).pad(0.3), { maxZoom: 8, animate: false });
  };

  const hoverText = hover ? (hover.kind === "node" ? t("roads.hover.node", { key: hover.key }) : t("roads.hover.link", { from: hover.from, to: hover.to })) : t("roads.hover.none");
  const status = st.status;
  const shapesNote = !status ? null
    : previewError ? t("roads.shapes.failed", { message: previewError })
    : computing ? t("roads.shapes.computing")
    : !followsSaved && preview ? t(preview.provisional ? "roads.shapes.provisional" : "roads.shapes.preview", { s: preview.seconds.toFixed(1) })
    : !status.shapesVersion ? t("roads.shapes.none")
    : status.shapesLeft ? t("roads.shapes.old")
    : null;

  return (
    <div className="edit-roads">
      <RoadPanel
        backgrounds={backgrounds.map((b) => ({ value: b, label: b === POSTAL ? t("base.postal") : b === NONE ? t("base.none") : mapName(t, b) }))}
        background={bg}
        onBackground={setBackground}
        shapesMaps={maps2.map((m) => ({ value: m, label: mapName(t, m) }))}
        shapesMap={sm}
        onShapesMap={setShapesMap}
        shapesOpacity={shapesOpacity}
        onShapesOpacity={setShapesOpacity}
        onFly={flyTo}
        onRemoveOrHide={() => removeOrHide(useRoadEditor.getState().selection)}
      />
      <div className="edit-maps">
        <div className="edit-map" data-guide="roads.left">
          <div ref={leftHost} className="map" />
          <div className="map-tools">
            <Button help="help.tool.zoomIn" onClick={() => maps?.left.zoomIn()}>
              +
            </Button>
            <Button help="help.tool.zoomOut" onClick={() => maps?.left.zoomOut()}>
              −
            </Button>
            <Button help="help.tool.fit" onClick={() => maps?.left.fitBounds(frameBounds(currentFrame()))}>
              {t("tool.fit")}
            </Button>
          </div>
          <div className="edit-map-foot">
            <Legend />
            <Info help="help.roads.hover" className="map-hover">
              {hoverText}
            </Info>
          </div>
        </div>
        <div className="edit-map" data-guide="roads.right">
          <div ref={rightHost} className="map" />
          <div ref={overviewHost} className="edit-overview" data-help="help.roads.overview" />
          {shapesNote && (
            <Info help="help.roads.shapesNote" className="map-badge">
              {shapesNote}
            </Info>
          )}
        </div>
      </div>
    </div>
  );
}

/** A field that takes typed keys (text, numbers, lists): the editor's keys are not for it. A check box or a slider is not. */
function typing(target: EventTarget | null): boolean {
  if (target instanceof HTMLInputElement) return !["checkbox", "radio", "range", "button"].includes(target.type);
  return target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || (target instanceof HTMLElement && target.isContentEditable);
}

/** A link between two nodes already (in the game's data or added). */
function linked(base: Base, view: View, a: string, b: string): boolean {
  if (base.pairs.has(pairKey(a, b))) return true;
  return view.addedLinks.some((l) => pairKey(l.from, l.to) === pairKey(a, b));
}

/** Delete: the added nodes and links of the selection go, those of the game are hidden. */
function removeOrHide(sel: RoadSelection) {
  const s = useRoadEditor.getState();
  const { base } = s;
  if (!base) return;
  const addedLinks = sel.links.filter((k) => s.edits.links.some((l) => !l.original && pairKey(l.from, l.to) === k));
  const gameLinks = sel.links.filter((k) => !addedLinks.includes(k) && base.pairs.has(k));
  let next = removeAdded(s.edits, sel.nodes.filter(isAdded), addedLinks);
  next = hide(base, next, sel.nodes.filter((k) => !isAdded(k)), gameLinks);
  s.change(next, { nodes: [], links: [] });
}

/** The legend of the left map, a check box a kind: what it shows (only the screen; the edits stay as they are). */
function Legend() {
  const t = useT();
  const shown = useRoadEditor((s) => s.shown);
  const setShown = useRoadEditor((s) => s.setShown);
  const item = (kind: ShownKind, help: HelpKey, colour: string, text: string, dashed = false, alpha = 1) => (
    <Check key={kind} help={help} checked={shown[kind]} onChange={(v) => setShown({ ...shown, [kind]: v })}>
      <span className="legend-line" style={{ borderColor: colour, borderTopStyle: dashed ? "dashed" : "solid", opacity: alpha }} />
      {text}
    </Check>
  );
  const game = GAME_KINDS.filter((k) => shown[k]).length;
  return (
    <div className="edit-legend" data-guide="roads.legend">
      <Info help="help.roads.legend" className="edit-legend-title">
        {t("roads.legend.title")}
      </Info>
      <Check help="help.roads.shown.game" checked={game === GAME_KINDS.length} mixed={game > 0 && game < GAME_KINDS.length}
        onChange={(v) => setShown({ ...shown, ...Object.fromEntries(GAME_KINDS.map((k) => [k, v])) })}>
        {t("roads.legend.game")}
      </Check>
      <span className="edit-legend-group">
        {item("normal", "help.roads.shown.normal", COLOURS.normal, t("roads.legend.normal"))}
        {item("highway", "help.roads.shown.highway", COLOURS.highway, t("roads.legend.highway"))}
        {item("tunnel", "help.roads.shown.tunnel", COLOURS.tunnel, t("roads.legend.tunnel"))}
        {item("unpaved", "help.roads.shown.unpaved", COLOURS.unpaved, t("roads.legend.unpaved"))}
        {item("switchedOff", "help.roads.shown.switchedOff", COLOURS.switchedOff, t("roads.legend.switchedOff"))}
      </span>
      {item("added", "help.roads.shown.added", COLOURS.added, t("roads.legend.added"))}
      {item("edited", "help.roads.shown.edited", COLOURS.edited, t("roads.legend.edited"))}
      {item("hidden", "help.roads.shown.hidden", COLOURS.normal, t("roads.legend.hidden"), true, 0.4)}
    </div>
  );
}

