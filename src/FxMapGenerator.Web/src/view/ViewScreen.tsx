import L from "leaflet";
import "leaflet/dist/leaflet.css";
import { useEffect, useRef, useState } from "react";
import { create } from "zustand";
import { api } from "../shared/api";
import { Button, Check, Info, Select, Slider } from "../shared/controls";
import { type MessageKey, useI18nStore, useT } from "../shared/i18n";
import { beforeLayer, blockAt, blockBounds, blockIndexOf, blockName, CELL, cellBounds, crs, currentFrame, frameBounds, frameKey, LEFT, postalLayer, projectKey, projectLayer, replaceLayer, setMapBackground, sheetBounds, SHEET, TILE, TOP, Z_BASE, Z_OVER } from "../shared/mapFrame";
import type { ChangedMap, PointDto, TileSetInfo } from "../shared/types";
import { mapName } from "../project/labels";
import { satelliteProvisional, useProjectStore } from "../project/store";

/** A layer choice: a map of the project (its id), the postal code map, or nothing. */
type LayerId = string;
const POSTAL = "postal";
const NONE = "none";
/** The layer of a map as it was before its tiles were written over: "before:<map>". */
const BEFORE = "before:";

/** The landcover's ground classes (the file's names) and their words on the screen. */
const GROUND_CLASSES: Record<string, MessageKey> = {
  none: "viewer.point.ground.none",
  urban: "viewer.point.ground.urban",
  grass: "viewer.point.ground.grass",
  dirt: "viewer.point.ground.dirt",
  sand: "viewer.point.ground.sand",
  beach: "viewer.point.ground.beach",
  rock: "viewer.point.ground.rock",
  vegetation: "viewer.point.ground.vegetation",
  snow: "viewer.point.ground.snow",
  waterMaterial: "viewer.point.ground.waterMaterial",
  defaultMaterial: "viewer.point.ground.defaultMaterial",
};

/** Places to jump to (game metres); names are the game's. */
const PLACES: readonly (readonly [string, number, number])[] = [
  ["Legion Square", 195, -934],
  ["Los Santos International Airport", -1037, -2738],
  ["Port of Los Santos", 800, -3000],
  ["Vinewood Sign", 711, 1198],
  ["Fort Zancudo", -2250, 3150],
  ["Sandy Shores", 1850, 3700],
  ["Grapeseed", 1700, 4800],
  ["Mount Chiliad", 501, 5604],
  ["Paleto Bay", -275, 6225],
];

interface ViewState {
  base: LayerId;
  overlay: LayerId;
  opacity: number;
  grid: { blocks: boolean; cells: boolean; sheets: boolean };
  /** Comparing a map with itself before its tiles were written over: on, which map, the before layer's opacity, outlines. */
  compare: { on: boolean; map: string | null; before: number; outline: boolean };
  center: [number, number] | null;
  zoom: number | null;
  set(patch: Partial<Omit<ViewState, "set">>): void;
}

/** Kept while the app is open, so going to another screen and back shows the same view. */
const useViewStore = create<ViewState>((set) => ({
  base: "satellite",
  overlay: NONE,
  opacity: 60,
  grid: { blocks: false, cells: false, sheets: false },
  compare: { on: false, map: null, before: 100, outline: true },
  center: null,
  zoom: null,
  set: (patch) => set(patch),
}));

interface Picked {
  x: number;
  y: number;
}

/** A tile layer on the map: which map, and for our own tiles the project and version it was loaded for. */
interface Shown {
  id: LayerId;
  layer: L.GridLayer;
  key: string;
  version: number;
  /** The project's frame the layer was made for (its tiles are bounded to it). */
  frame: string;
}

/**
 * The map view: a base map and a map laid over it with its
 * opacity, the block / cell / minimap-sheet grids, jumps to known places, and what lies at a clicked point.
 */
export function ViewScreen() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const blocks = useProjectStore((s) => s.blocks);
  const plan = useProjectStore((s) => s.plan);
  const tileVersion = useProjectStore((s) => s.tileVersion);
  const view = useViewStore();
  const [maps, setMaps] = useState<TileSetInfo[] | null>(null);
  const [picked, setPicked] = useState<Picked | null>(null);
  const [changes, setChanges] = useState<ChangedMap[] | null>(null);
  const host = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const layers = useRef<{ base: Shown | null; overlay: Shown | null; grids: L.LayerGroup; outline: L.LayerGroup; pin: L.CircleMarker | null }>(null);
  const key = projectKey(project.workFolder);

  useEffect(() => {
    setMaps(null);
    void api.exportInfo().then((info) => setMaps(info.maps), () => setMaps([]));
  }, [project.path]);

  // what changed since the maps' tiles were kept (a run that wrote over tiles makes the list anew)
  useEffect(() => {
    void api.before().then((b) => setChanges(b.maps), () => setChanges([]));
  }, [project.path, tileVersion]);

  // ---- the map, once
  useEffect(() => {
    if (!host.current) return;
    const s = useViewStore.getState();
    const map = L.map(host.current, { crs, zoomControl: false, attributionControl: false, minZoom: -1, maxZoom: 10, zoomSnap: 0.5, maxBounds: frameBounds(currentFrame()).pad(0.25), maxBoundsViscosity: 0.8 });
    if (s.center && s.zoom !== null) map.setView(s.center, s.zoom);
    else map.fitBounds(frameBounds(currentFrame()));
    layers.current = { base: null, overlay: null, grids: L.layerGroup().addTo(map), outline: L.layerGroup().addTo(map), pin: null };
    map.on("moveend", () => {
      const c = map.getCenter();
      useViewStore.getState().set({ center: [c.lat, c.lng], zoom: map.getZoom() });
    });
    map.on("click", (e: L.LeafletMouseEvent) => {
      const at = { x: e.latlng.lng, y: e.latlng.lat };
      setPicked(at);
      const l = layers.current!;
      l.pin?.remove();
      l.pin = L.circleMarker(e.latlng, { radius: 6, color: "#f2c400", weight: 3, fill: false, interactive: false }).addTo(map);
    });
    const resize = new ResizeObserver(() => map.invalidateSize());
    resize.observe(host.current);
    mapRef.current = map;
    return () => {
      resize.disconnect();
      map.remove();
      mapRef.current = null;
    };
  }, []);

  // ---- the project's frame: where the map may be moved (the tile layers follow it below)
  const fk = frameKey(project.frame);
  useEffect(() => {
    mapRef.current?.setMaxBounds(frameBounds(currentFrame()).pad(0.25));
  }, [fk]);

  // the lists offer the maps with tiles in this project: a choice kept from another project that has no tiles here shows
  // as PostalMap / none, and so do the layers once the list is known (before it, the choice kept: no PostalMap blinking
  // first in a project that has the map); the choice itself stays for the projects that have it
  const withTiles = maps ?? [];
  const layerOptions = [
    ...withTiles.map((m) => ({ value: m.map, label: mapName(t, m.map) })),
    { value: POSTAL, label: t("base.postal") },
    { value: NONE, label: t("base.none") },
  ];
  const baseValue = layerOptions.some((o) => o.value === view.base) ? view.base : POSTAL;
  const overlayValue = layerOptions.some((o) => o.value === view.overlay) ? view.overlay : NONE;
  const listed = maps !== null;

  // the comparison takes the base (the map now) and the overlay (the map before, over it)
  const changed = changes ?? [];
  const cmp = view.compare;
  const cmpMap = cmp.on ? (changed.find((c) => c.map === cmp.map) ?? changed[0] ?? null) : null;
  const shownBase = cmpMap ? cmpMap.map : listed ? baseValue : view.base;
  const shownOverlay = cmpMap ? BEFORE + cmpMap.map : listed ? overlayValue : view.overlay;
  const overlayOpacity = cmpMap ? cmp.before : view.opacity;
  const opacityRef = useRef(overlayOpacity);
  opacityRef.current = overlayOpacity;

  // ---- base and overlay; a new version of our tiles replaces the layer without blinking
  useEffect(() => {
    const map = mapRef.current, l = layers.current;
    if (!map || !l) return;
    const show = (current: Shown | null, id: LayerId, zIndex: number, opacity: number): Shown | null => {
      if (id === NONE) {
        current?.layer.remove();
        return null;
      }
      if (current && current.id === id && (id === POSTAL || (current.key === key && current.version === tileVersion && current.frame === fk))) return current;
      const layer = id === POSTAL ? postalLayer({ zIndex, opacity })
        : id.startsWith(BEFORE) ? beforeLayer(id.slice(BEFORE.length), key, tileVersion, { zIndex, opacity })
        : projectLayer(id, key, tileVersion, { zIndex, opacity });
      replaceLayer(map, current?.layer ?? null, layer, !!current && current.id === id && current.key === key);
      return { id, layer, key, version: tileVersion, frame: fk };
    };
    l.base = show(l.base, shownBase, Z_BASE, 1);
    l.overlay = show(l.overlay, shownOverlay, Z_OVER, opacityRef.current / 100);
  }, [shownBase, shownOverlay, tileVersion, key, fk]);

  useEffect(() => {
    layers.current?.overlay?.layer.setOpacity(overlayOpacity / 100);
  }, [overlayOpacity]);

  // ---- behind the tiles: the satellite map's own sea colour while it is the base
  const sea = blocks?.satelliteSea ?? null;
  useEffect(() => {
    setMapBackground(host.current, shownBase === "satellite" ? sea : null);
  }, [shownBase, sea]);

  // ---- the changed z8 tiles of the compared map, outlined: red, grey where the change does not show
  useEffect(() => {
    const group = layers.current?.outline;
    if (!group) return;
    group.clearLayers();
    if (!cmpMap || !cmp.outline) return;
    const renderer = L.canvas({ padding: 0.2 });
    const faint = new Set<string>();
    const f = cmpMap.faintZ8 ?? [];
    for (let i = 0; i + 1 < f.length; i += 2) faint.add(`${f[i]}_${f[i + 1]}`);
    for (let i = 0; i + 1 < cmpMap.z8.length; i += 2) {
      const x = LEFT + cmpMap.z8[i] * TILE, y = TOP - cmpMap.z8[i + 1] * TILE;
      const dim = faint.has(`${cmpMap.z8[i]}_${cmpMap.z8[i + 1]}`);
      L.rectangle([[y - TILE, x], [y, x + TILE]], { renderer, interactive: false, color: dim ? "#8b95a1" : "#ff3b30", weight: dim ? 1 : 1.5, opacity: dim ? 0.7 : 0.9, fill: false }).addTo(group);
    }
  }, [cmpMap, cmp.outline]);

  // ---- X swaps the map before and now while comparing (not while typing into a field or while a guide is shown)
  useEffect(() => {
    if (!cmpMap) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "x" && e.key !== "X") return;
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLSelectElement) return;
      if (document.body.classList.contains("driver-active")) return;
      const c = useViewStore.getState().compare;
      useViewStore.getState().set({ compare: { ...c, before: c.before >= 50 ? 0 : 100 } });
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [cmpMap]);

  // ---- grids
  useEffect(() => {
    const group = layers.current?.grids;
    if (!group) return;
    group.clearLayers();
    const renderer = L.canvas({ padding: 0.2 });
    const f = currentFrame();
    if (view.grid.blocks)
      for (let by = f.by0; by < f.by0 + f.rows; by++)
        for (let bx = f.bx0; bx < f.bx0 + f.cols; bx++)
          L.rectangle(blockBounds(bx, by), { renderer, interactive: false, color: "#ffffff", weight: 0.6, opacity: 0.55, fill: false }).addTo(group);
    if (view.grid.cells)
      for (let r = Math.floor(f.by0 / CELL); r < Math.ceil((f.by0 + f.rows) / CELL); r++)
        for (let c = Math.floor(f.bx0 / CELL); c < Math.ceil((f.bx0 + f.cols) / CELL); c++)
          L.rectangle(cellBounds(r, c), { renderer, interactive: false, color: "#f2c400", weight: 2, opacity: 0.9, fill: false }).addTo(group);
    if (view.grid.sheets)
      for (let r = 0; r < 3; r++)
        for (let c = 0; c < 2; c++) {
          L.rectangle(sheetBounds(r, c), { renderer, interactive: false, color: "#ff5ca8", weight: 3, dashArray: "10 6", fill: false }).addTo(group);
          L.tooltip({ permanent: true, direction: "center", className: "sheet-label" })
            .setLatLng([TOP - (r + 0.5) * SHEET, LEFT + (c + 0.5) * SHEET])
            .setContent(`minimap_${r}_${c}`)
            .addTo(group);
        }
  }, [view.grid, fk]);

  const jump = (x: number, y: number) => mapRef.current?.flyTo([y, x], 6, { duration: 0.6 });
  const jumpTo = (a: { x0: number; y0: number; x1: number; y1: number }) =>
    mapRef.current?.flyToBounds(L.latLngBounds([a.y1, a.x0], [a.y0, a.x1]), { padding: [60, 60], maxZoom: 7, duration: 0.6 });
  const setCompare = (patch: Partial<ViewState["compare"]>) => view.set({ compare: { ...view.compare, ...patch } });
  const provisional = (baseValue === "satellite" || overlayValue === "satellite") && satelliteProvisional(plan, blocks);

  return (
    <div className="view-screen">
      <aside className="side view-side">
        <section data-guide="view.layers">
          <Info help="help.viewer.layers" block className="section-title">
            {t("viewer.layers")}
          </Info>
          <Select help="help.viewer.base" label={t("viewer.base")} value={baseValue} options={layerOptions} disabledReason={cmpMap ? "reason.comparing" : null} onChange={(v) => view.set({ base: v })} />
          <Select help="help.viewer.overlay" label={t("viewer.overlay")} value={overlayValue} options={layerOptions} disabledReason={cmpMap ? "reason.comparing" : null} onChange={(v) => view.set({ overlay: v })} />
          <Slider
            help="help.viewer.opacity"
            label={t("viewer.opacity")}
            value={view.opacity}
            min={0}
            max={100}
            display={`${view.opacity} %`}
            disabledReason={cmpMap ? "reason.comparing" : overlayValue === NONE ? "reason.noOverlay" : null}
            onChange={(v) => view.set({ opacity: v })}
          />
          {maps !== null && withTiles.length === 0 && (
            <Info help="help.viewer.noTiles" block className="muted">
              {t("viewer.noTiles")}
            </Info>
          )}
        </section>
        <section data-guide="view.compare">
          <Info help="help.viewer.compare" block className="section-title">
            {t("viewer.compare")}
          </Info>
          {changed.length === 0 ? (
            <Info help="help.viewer.compare.none" block className="muted">
              {t("viewer.compare.none")}
            </Info>
          ) : (
            <>
              <Check help="help.viewer.compare.on" checked={cmp.on} onChange={(v) => setCompare({ on: v, map: cmp.map ?? changed[0].map })}>
                {t("viewer.compare.on")}
              </Check>
              <Select
                help="help.viewer.compare.map"
                label={t("viewer.compare.map")}
                value={cmpMap?.map ?? cmp.map ?? changed[0].map}
                options={changed.map((c) => ({ value: c.map, label: t("viewer.compare.mapTiles", { map: mapName(t, c.map), tiles: c.tiles }) }))}
                disabledReason={cmp.on ? null : "reason.notComparing"}
                onChange={(v) => setCompare({ map: v })}
              />
              <div className="compare-switch">
                <Button help="help.viewer.compare.before" pressed={!!cmpMap && cmp.before >= 50} disabledReason={cmp.on ? null : "reason.notComparing"} onClick={() => setCompare({ before: 100 })}>
                  {t("viewer.compare.before")}
                </Button>
                <Button help="help.viewer.compare.after" pressed={!!cmpMap && cmp.before < 50} disabledReason={cmp.on ? null : "reason.notComparing"} onClick={() => setCompare({ before: 0 })}>
                  {t("viewer.compare.after")}
                </Button>
              </div>
              <Slider
                help="help.viewer.compare.opacity"
                label={t("viewer.compare.opacity")}
                value={cmp.before}
                min={0}
                max={100}
                display={`${cmp.before} %`}
                disabledReason={cmp.on ? null : "reason.notComparing"}
                onChange={(v) => setCompare({ before: v })}
              />
              <Check help="help.viewer.compare.outline" checked={cmp.outline} disabledReason={cmp.on ? null : "reason.notComparing"} onChange={(v) => setCompare({ outline: v })}>
                {t("viewer.compare.outline")}
              </Check>
              <div className="places">
                {(cmpMap ?? changed[0]).areas.filter((a) => !a.faint).slice(0, 12).map((a, i) => (
                  <Button key={`${a.x0}_${a.y0}`} help="help.viewer.compare.area" variant="row" onClick={() => jumpTo(a)}>
                    {t("viewer.compare.area", { n: i + 1, tiles: a.tiles })}
                  </Button>
                ))}
              </div>
              <FaintPlaces areas={(cmpMap ?? changed[0]).areas.filter((a) => a.faint)} onJump={jumpTo} />
            </>
          )}
        </section>
        <section data-guide="view.grids">
          <Info help="help.viewer.grids" block className="section-title">
            {t("viewer.grids")}
          </Info>
          <Check help="help.viewer.grid.blocks" checked={view.grid.blocks} onChange={(v) => view.set({ grid: { ...view.grid, blocks: v } })}>
            {t("viewer.grid.blocks")}
          </Check>
          <Check help="help.viewer.grid.cells" checked={view.grid.cells} onChange={(v) => view.set({ grid: { ...view.grid, cells: v } })}>
            {t("viewer.grid.cells")}
          </Check>
          <Check help="help.viewer.grid.sheets" checked={view.grid.sheets} onChange={(v) => view.set({ grid: { ...view.grid, sheets: v } })}>
            {t("viewer.grid.sheets")}
          </Check>
        </section>
        <section data-guide="view.places">
          <Info help="help.viewer.places" block className="section-title">
            {t("viewer.places")}
          </Info>
          <div className="places">
            {PLACES.map(([name, x, y]) => (
              <Button key={name} help="help.viewer.place" variant="row" onClick={() => jump(x, y)}>
                {name}
              </Button>
            ))}
            <Button help="help.tool.fit" variant="row" onClick={() => mapRef.current?.fitBounds(frameBounds(currentFrame()))}>
              {t("tool.fit")}
            </Button>
          </div>
        </section>
        <section data-guide="view.point">
          <Info help="help.viewer.point" block className="section-title">
            {t("viewer.point")}
          </Info>
          {picked ? <PointInfo at={picked} blocks={blocks} /> : <p className="muted">{t("viewer.point.none")}</p>}
        </section>
      </aside>
      <div className="view-map" data-guide="view.map">
        <div ref={host} className="map" />
        <div className="map-tools">
          <Button help="help.tool.zoomIn" onClick={() => mapRef.current?.zoomIn()}>
            +
          </Button>
          <Button help="help.tool.zoomOut" onClick={() => mapRef.current?.zoomOut()}>
            −
          </Button>
        </div>
        {provisional && (
          <Info help="help.map.provisional" className="map-badge">
            {t("map.provisional")}
          </Info>
        )}
      </div>
    </div>
  );
}

/**
 * The places whose change does not show (every pixel of their changed tiles within a few steps of before), in a part of
 * their own that opens and closes, closed at first.
 */
function FaintPlaces({ areas, onJump }: { areas: ChangedMap["areas"]; onJump: (a: ChangedMap["areas"][number]) => void }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  if (areas.length === 0) return null;
  return (
    <div className={`acc${open ? " acc-open" : ""}`}>
      <Button help="help.viewer.compare.faint" variant="row" className="acc-head" onClick={() => setOpen((o) => !o)}>
        <span className="acc-mark">{open ? "▾" : "▸"}</span>
        {t("viewer.compare.faint", { n: areas.length })}
      </Button>
      {open && (
        <div className="acc-body places">
          {areas.slice(0, 30).map((a, i) => (
            <Button key={`${a.x0}_${a.y0}`} help="help.viewer.compare.faintArea" variant="row" onClick={() => onJump(a)}>
              {t("viewer.compare.faintArea", { n: i + 1, tiles: a.tiles })}
            </Button>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * What lies at a point: coordinates, block (class, data), cell, minimap sheet, z8 tile; from the block's scans the
 * material, zone and street, from its landcover the ground class and a building.
 */
function PointInfo({ at, blocks }: { at: Picked; blocks: ReturnType<typeof useProjectStore.getState>["blocks"] }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const [values, setValues] = useState<PointDto | null>(null);
  useEffect(() => {
    let live = true;
    setValues(null);
    void api.point(at.x, at.y).then((v) => live && setValues(v), () => live && setValues(null));
    return () => {
      live = false;
    };
  }, [at.x, at.y]);
  const grid = blocks ?? currentFrame();
  const b = blockAt(L.latLng(at.y, at.x), grid);
  const rows: [string, string][] = [[t("viewer.point.xy"), `${at.x.toFixed(1)}, ${at.y.toFixed(1)}`]];
  if (b) {
    const i = blockIndexOf(b[0], b[1], grid);
    const cls = blocks?.range[i] === "L" ? t("cls.land") : blocks?.range[i] === "W" ? t("cls.water") : t("cls.out");
    const m = blocks?.items[i] ?? 0;
    const have = ([[1, "item.shot"], [2, "item.height"], [4, "item.scanGround"], [8, "item.scanRoads"], [16, "item.scanCanopy"]] as const)
      .filter(([bit]) => m & bit)
      .map(([, key]) => t(key));
    rows.push([t("viewer.point.block"), `${blockName(b[0], b[1])}（${cls}）`]);
    rows.push([t("viewer.point.data"), have.length ? have.join("・") : t("map.hover.nothing")]);
    rows.push([t("viewer.point.tiles"), blocks?.ortho[i] === "1" ? t("viewer.point.tiles.yes") : t("viewer.point.tiles.no")]);
    rows.push([t("viewer.point.cell"), `cell_${Math.floor(b[1] / CELL)}_${Math.floor(b[0] / CELL)}`]);
    // the minimap's 2 x 3 sheets cover the standard frame only
    const sr = Math.floor((TOP - at.y) / SHEET), sc = Math.floor((at.x - LEFT) / SHEET);
    rows.push([t("viewer.point.sheet"), sr >= 0 && sr < 3 && sc >= 0 && sc < 2 ? `minimap_${sr}_${sc}` : "—"]);
    rows.push([t("viewer.point.tile"), `8 / ${Math.floor((at.x - LEFT) / TILE)} / ${Math.floor((TOP - at.y) / TILE)}`]);
    if (values?.scanned === false) rows.push([t("viewer.point.scan"), t("viewer.point.scan.none")]);
    if (values?.material != null) {
      const cls = t("viewer.point.material.class", { cls: values.materialClass ?? "" });
      rows.push([t("viewer.point.material"), values.material === "" ? t("viewer.point.material.none") : `${values.material}${cls}`]);
      const h = values.height != null ? t("viewer.point.height.value", { m: values.height.toFixed(1) }) : t("viewer.point.height.none");
      rows.push([t("viewer.point.height"), values.water != null ? `${h}${t("viewer.point.water", { m: values.water.toFixed(1) })}` : h]);
    }
    if (values?.zone != null) {
      const zone = (lang === "ja" && values.zoneJa) || values.zoneName || values.zone;
      rows.push([t("viewer.point.zone"), `${zone}（${values.zone}）`]);
    }
    if (values?.street != null)
      rows.push([t("viewer.point.street"), values.street === "" ? t("viewer.point.street.none") : (lang === "ja" && values.streetJa) || values.street]);
    if (values?.onRoad != null) rows.push([t("viewer.point.onRoad"), values.onRoad ? t("viewer.point.yes") : t("viewer.point.no")]);
    if (values?.landcover != null) {
      const key = GROUND_CLASSES[values.landcover];
      const cls = key ? t(key) : values.landcover;
      rows.push([t("viewer.point.landcover"), values.building ? t("viewer.point.landcover.building", { cls }) : cls]);
    }
  } else rows.push([t("viewer.point.block"), t("viewer.point.offMap")]);
  return (
    <Info help="help.viewer.point.values" block>
      <table className="point-table">
        <tbody>
          {rows.map(([k, v]) => (
            <tr key={k}>
              <td className="muted">{k}</td>
              <td>{v}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Info>
  );
}
