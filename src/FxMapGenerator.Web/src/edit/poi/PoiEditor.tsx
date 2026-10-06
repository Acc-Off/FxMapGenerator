import L from "leaflet";
import { useEffect, useMemo, useRef, useState } from "react";
import { api } from "../../shared/api";
import { Button, Info } from "../../shared/controls";
import { t as translate, useI18nStore, useT } from "../../shared/i18n";
import { currentFrame, frameBounds, projectKey, setMapBackground, Z_BASE, Z_OVER } from "../../shared/mapFrame";
import { mapName } from "../../project/labels";
import { useProjectStore } from "../../project/store";
import { areaSelect, backgroundLayer, insidePolygon, NONE, POSTAL, useEditMap, useLayer } from "../shared/editMaps";
import { makesJapanese, type PoiLabel } from "../../shared/types";
import { addPoint, chainOf, groupName, parentOf, pointName, removePoints, type Resolved, resolveAll, round, splitKey, updatePoints } from "./model";
import { createPoiLayer, pickPoint, type PoiLayer, type PoiLayerState, SELECTED } from "./poiLayer";
import { PoiPanel } from "./PoiPanel";
import { PoiSide } from "./PoiSide";
import { NOTHING, POI_TOOLS, type PoiSelection, useLabelLang, usePoiEditor } from "./store";

/** Settings of the POI screen's map, kept while the app is open. */
const display: { background: string | null } = { background: null };

/** The label a point placed on the map starts with (then typed in the panel): English, and Japanese when the project makes Japanese maps. */
export function newPointLabel(japanese: boolean): PoiLabel {
  return japanese ? { en: "New POI", ja: "新しい POI" } : { en: "New POI" };
}

/** A field that takes typed keys (text, numbers, lists): the screen's keys are not for it. A check box or a slider is not. */
export function typing(target: EventTarget | null): boolean {
  if (target instanceof HTMLInputElement) return !["checkbox", "radio", "range", "button"].includes(target.type);
  return target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || (target instanceof HTMLElement && target.isContentEditable);
}

/** The group a point placed now goes into: the one chosen, or the group of the one point chosen. */
export function targetGroup(sel: PoiSelection): string | null {
  if (sel.groups.length === 1 && sel.points.length === 0 && sel.folders.length === 0) return sel.groups[0];
  if (sel.points.length > 0 && sel.groups.length === 0 && sel.folders.length === 0) {
    const groups = new Set(sel.points.map((k) => splitKey(k)[0]));
    if (groups.size === 1) return [...groups][0];
  }
  return null;
}

/**
 * The POI screen (the editing tab's "POI"): on the left the folders' tree (or the POI styles), in the middle
 * the map with the points drawn as the maps draw them, where they are placed, moved and chosen; on the right saving,
 * the tools, what the map shows and the values of what is chosen (or of the POI style chosen).
 */
export function PoiEditor() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const dto = usePoiEditor((s) => s.dto);
  const ready = usePoiEditor((s) => !!s.dto && !!s.set);
  const error = usePoiEditor((s) => s.error);
  useEffect(() => {
    void usePoiEditor.getState().load(project.path);
    void usePoiEditor.getState().loadIcons();
  }, [project.path]);
  // the screen (with its map) is made once the points are read
  if (!ready) {
    return (
      <div className="edit-unavailable">
        <Info help="help.poi.loading" block className={error || dto?.problem ? "text-error" : "muted"}>
          {dto?.problem ? t("poi.problem", { message: dto.problem }) : (error ?? t("poi.loading"))}
        </Info>
      </div>
    );
  }
  return <PoiScreen />;
}

function PoiScreen() {
  const t = useT();
  const uiLang = useI18nStore((s) => s.lang);
  const project = useProjectStore((s) => s.project)!;
  const tileVersion = useProjectStore((s) => s.tileVersion);
  const st = usePoiEditor();
  const host = useRef<HTMLDivElement>(null);
  const overviewHost = useRef<HTMLDivElement>(null);
  const maps = useEditMap(host, overviewHost);
  const [tiles, setTiles] = useState<string[] | null>(null);
  const [background, setBackground] = useState<string | null>(display.background);
  const labelLang = useLabelLang();
  const japanese = makesJapanese(project.file);
  const japaneseRef = useRef(japanese);
  japaneseRef.current = japanese;
  const [hover, setHover] = useState<Resolved | null>(null);
  const key = projectKey(project.workFolder);
  display.background = background;

  useEffect(() => {
    void api.exportInfo().then((i) => setTiles(i.maps.map((m) => m.map)), () => setTiles([]));
  }, [project.path, tileVersion]);

  const backgrounds = useMemo(() => [...(tiles ?? []), POSTAL, NONE], [tiles]);
  const bg = background && backgrounds.includes(background) ? background : (tiles ?? []).includes("satellite") ? "satellite" : POSTAL;
  useLayer(maps?.map ?? null, () => backgroundLayer(bg, key, tileVersion, { zIndex: Z_BASE }), [bg, key, tileVersion]);
  useLayer(maps?.overview ?? null, () => backgroundLayer(bg === NONE ? POSTAL : bg, key, tileVersion, { zIndex: Z_BASE }), [bg, key, tileVersion]);
  // behind the tiles: the satellite map's own sea colour while it is the background
  const sea = useProjectStore((s) => s.blocks?.satelliteSea ?? null);
  useEffect(() => {
    for (const m of [maps?.map, maps?.overview]) setMapBackground(m, bg === "satellite" ? sea : null);
  }, [maps, bg, sea]);

  const bundled = st.dto?.bundledStyles;
  const points = useMemo(() => (st.set && bundled ? resolveAll(st.set, bundled) : []), [st.set, bundled]);

  // ---- the points' layer
  const layerRef = useRef<PoiLayer | null>(null);
  const layerState = useRef<PoiLayerState>({ points: [], selection: new Set(), drag: null, fonts: {}, lang: labelLang, icons: null });
  useEffect(() => {
    if (!maps) return;
    const layer = createPoiLayer(layerState.current);
    layer.setZIndex(Z_OVER);
    layer.addTo(maps.map);
    layerRef.current = layer;
    return () => {
      layer.remove();
      layerRef.current = null;
    };
  }, [maps]);
  useEffect(() => {
    const s = layerState.current;
    s.points = points;
    s.selection = new Set(st.selection.points);
    s.fonts = st.dto?.fonts ?? {};
    s.lang = labelLang;
    s.icons = st.icons;
    layerRef.current?.repaint();
  }, [points, st.selection, st.dto, labelLang, st.icons]);

  // ---- the tools on the map
  const toolRef = useRef(st.tool);
  toolRef.current = st.tool;
  useEffect(() => {
    const map = maps?.map;
    if (!map) return;
    map.getContainer().classList.toggle("map-drawing", st.tool === "place" || st.tool === "box");
    if (st.tool === "box") map.dragging.disable();
    else map.dragging.enable();
  }, [maps, st.tool]);

  useEffect(() => {
    const map = maps?.map;
    if (!map) return;
    const state = () => usePoiEditor.getState();
    const pickAt = (e: L.LeafletMouseEvent) => pickPoint(layerState.current, e.latlng.lng, e.latlng.lat, map.getZoom());
    const choose = (r: Resolved | null, add: boolean) => {
      const cur = state().selection;
      if (!r) {
        if (!add) state().select(NOTHING);
        return;
      }
      const has = cur.points.includes(r.key);
      state().select({ folders: [], groups: [], points: add ? (has ? cur.points.filter((k) => k !== r.key) : [...cur.points, r.key]) : [r.key] });
      // the tree shows where it is
      for (const f of chainOf(state().set!, parentOf(r.group.path))) state().toggle(f.path, true);
    };
    const chooseMany = (keys: string[], add: boolean) => {
      const cur = state().selection;
      state().select({ folders: [], groups: [], points: add ? [...new Set([...cur.points, ...keys])] : keys });
    };
    const stopArea = areaSelect(
      map,
      () => toolRef.current === "box",
      SELECTED,
      (west, south, east, north, add) =>
        chooseMany(layerState.current.points.filter((r) => r.point.x >= west && r.point.x <= east && r.point.y >= south && r.point.y <= north).map((r) => r.key), add),
      (corners, add) => chooseMany(layerState.current.points.filter((r) => insidePolygon(r.point.x, r.point.y, corners)).map((r) => r.key), add),
    );

    // dragging points
    let drag: { start: L.LatLng; points: Map<string, { x: number; y: number }> } | null = null;
    let frame = 0;
    let lastHover = "";
    const onMove = (e: L.LeafletMouseEvent) => {
      if (drag) {
        const dx = e.latlng.lng - drag.start.lng, dy = e.latlng.lat - drag.start.lat;
        const shown = new Map<string, { x: number; y: number }>();
        for (const [k, p] of drag.points) shown.set(k, { x: p.x + dx, y: p.y + dy });
        layerState.current.drag = shown;
        layerRef.current?.repaint();
        return;
      }
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        const r = pickAt(e);
        const k = r?.key ?? "";
        if (k !== lastHover) {
          lastHover = k;
          setHover(r);
        }
      });
    };
    const onDown = (e: L.LeafletMouseEvent) => {
      if ((e.originalEvent as MouseEvent).button !== 0 || toolRef.current !== "move") return;
      const r = pickAt(e);
      if (!r) return;
      const { selection } = state();
      const keys = selection.points.includes(r.key) ? selection.points : [r.key];
      const moving = new Map<string, { x: number; y: number }>();
      for (const p of layerState.current.points) if (keys.includes(p.key) && !p.locked) moving.set(p.key, { x: p.point.x, y: p.point.y });
      if (moving.size === 0) {
        state().setNotice(t("poi.move.locked"));
        return;
      }
      map.dragging.disable();
      drag = { start: e.latlng, points: moving };
      if (!selection.points.includes(r.key)) choose(r, false);
    };
    const onUp = (e: L.LeafletMouseEvent) => {
      if (!drag) return;
      const d = drag;
      drag = null;
      map.dragging.enable();
      const dx = e.latlng.lng - d.start.lng, dy = e.latlng.lat - d.start.lat;
      layerState.current.drag = null;
      if (Math.hypot(dx, dy) < 1e-6) {
        layerRef.current?.repaint();
        return;
      }
      const s = state();
      s.change(updatePoints(s.set!, [...d.points.keys()], (p) => ({ ...p, x: round(p.x + dx), y: round(p.y + dy) })));
    };
    const onClick = (e: L.LeafletMouseEvent) => {
      const tool = toolRef.current;
      const add = (e.originalEvent as MouseEvent).shiftKey;
      if (tool === "select" || tool === "move") {
        choose(pickAt(e), add);
        return;
      }
      if (tool === "place") {
        const s = state();
        const group = targetGroup(s.selection);
        if (!s.set || !group) {
          s.setNotice(t("poi.place.noGroup"));
          return;
        }
        const g = s.set.groups.find((x) => x.path === group)!;
        if (g.locked || chainOf(s.set, parentOf(g.path)).some((f) => f.locked)) {
          s.setNotice(t("poi.place.locked", { name: groupName(g, uiLang) }));
          return;
        }
        const made = addPoint(s.set, group, e.latlng.lng, e.latlng.lat, newPointLabel(japaneseRef.current));
        // the group stays the place of the next point: the new point is chosen with it
        s.change(made.set, { folders: [], groups: [], points: [made.key] });
      }
    };
    const onLeave = () => {
      if (lastHover) {
        lastHover = "";
        setHover(null);
      }
    };
    map.on("mousemove", onMove);
    map.on("mousedown", onDown);
    map.on("mouseup", onUp);
    map.on("click", onClick);
    map.on("mouseout", onLeave);
    return () => {
      stopArea();
      map.off("mousemove", onMove);
      map.off("mousedown", onDown);
      map.off("mouseup", onUp);
      map.off("click", onClick);
      map.off("mouseout", onLeave);
      if (frame) cancelAnimationFrame(frame);
    };
  }, [maps, t, uiLang]);

  // ---- keys: 1 to 4 choose the tool, Delete removes the chosen points, Ctrl+Z / Ctrl+Y, Esc chooses nothing (none
  // while typing into a field, while a window is open over the screen or while a guide is shown)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (typing(e.target) || document.querySelector(".modal-backdrop, .color-picker, .driver-active")) return;
      const s = usePoiEditor.getState();
      const tool = /^[1-4]$/.test(e.key) ? POI_TOOLS[Number(e.key) - 1] : undefined;
      if (tool && !e.ctrlKey && !e.metaKey && !e.altKey) s.setTool(tool);
      else if ((e.ctrlKey || e.metaKey) && (e.key === "z" || e.key === "Z")) {
        e.preventDefault();
        if (e.shiftKey) s.redo();
        else s.undo();
      } else if ((e.ctrlKey || e.metaKey) && (e.key === "y" || e.key === "Y")) {
        e.preventDefault();
        s.redo();
      } else if (e.key === "Escape") s.select(NOTHING);
      else if ((e.key === "Delete" || e.key === "Backspace") && s.selection.points.length > 0 && s.set) {
        e.preventDefault();
        removeChosenPoints();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const flyTo = (x: number, y: number) => {
    const map = maps?.map;
    if (!map) return;
    map.setView([y, x], Math.max(map.getZoom(), 6), { animate: false });
  };

  const hoverText = hover ? t("poi.hover", { name: pointName(hover.point, labelLang) || t("poi.noName"), group: groupName(hover.group, uiLang) }) : t("poi.hover.none");
  return (
    <div className="edit-poi">
      <PoiSide onFly={flyTo} />
      <div className="edit-maps poi-map-area">
        <div className="edit-map" data-guide="poi.map">
          <div ref={host} className="map" />
          <div className="map-tools">
            <Button help="help.tool.zoomIn" onClick={() => maps?.map.zoomIn()}>
              +
            </Button>
            <Button help="help.tool.zoomOut" onClick={() => maps?.map.zoomOut()}>
              −
            </Button>
            <Button help="help.tool.fit" onClick={() => maps?.map.fitBounds(frameBounds(currentFrame()))}>
              {t("tool.fit")}
            </Button>
          </div>
          <div ref={overviewHost} className="edit-overview" data-help="help.poi.overview" />
          <div className="edit-map-foot">
            <Info help="help.poi.hover" className="map-hover">
              {hoverText}
            </Info>
          </div>
        </div>
      </div>
      <PoiPanel
        backgrounds={backgrounds.map((b) => ({ value: b, label: b === POSTAL ? t("base.postal") : b === NONE ? t("base.none") : mapName(t, b) }))}
        background={bg}
        onBackground={setBackground}
        onRemovePoints={removeChosenPoints}
      />
    </div>
  );
}

/** Delete: the chosen points go, but those locked (by themselves, their group or a folder). */
export function removeChosenPoints() {
  const s = usePoiEditor.getState();
  if (!s.set || !s.dto) return;
  const locked = new Set(resolveAll(s.set, s.dto.bundledStyles).filter((r) => r.locked).map((r) => r.key));
  const keys = s.selection.points.filter((k) => !locked.has(k));
  if (keys.length === 0) {
    if (s.selection.points.length > 0) s.setNotice(translate("poi.delete.locked"));
    return;
  }
  s.change(removePoints(s.set, keys), NOTHING);
}
