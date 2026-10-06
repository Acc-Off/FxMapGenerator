import L from "leaflet";
import "leaflet/dist/leaflet.css";
import { type RefObject, useEffect, useRef, useState } from "react";
import { create } from "zustand";
import { crs, currentFrame, frameBounds, postalLayer, projectLayer } from "../../shared/mapFrame";

// Parts every editing screen shares (roads now; points of interest later): two detail maps side by
// side that move and zoom together, and a small map of the whole world whose box shows (and moves) what they show.

/** What the detail maps show, kept while the app is open (going to another tab and back shows the same place). */
export const useEditView = create<{ center: [number, number] | null; zoom: number | null; set(center: [number, number], zoom: number): void }>((set) => ({
  center: null,
  zoom: null,
  set: (center, zoom) => set({ center, zoom }),
}));

export interface EditMaps {
  left: L.Map;
  right: L.Map;
  overview: L.Map;
}

const DETAIL: L.MapOptions = {
  crs,
  zoomControl: false,
  attributionControl: false,
  minZoom: -1,
  maxZoom: 10,
  zoomSnap: 0.5,
  zoomAnimation: false,
  doubleClickZoom: false,
  boxZoom: false,
  maxBoundsViscosity: 0.8,
};

/** The detail maps' options: they move within the open project's frame. */
function detail(): L.MapOptions {
  return { ...DETAIL, maxBounds: frameBounds(currentFrame()).pad(0.25) };
}

/**
 * Makes the two detail maps and the overview in the given hosts (once), keeps them together, and returns them once
 * made. The overview's box is the left map's view; pressing or dragging on the overview moves both maps there.
 */
export function useEditMaps(left: RefObject<HTMLDivElement | null>, right: RefObject<HTMLDivElement | null>, overview: RefObject<HTMLDivElement | null>): EditMaps | null {
  const [maps, setMaps] = useState<EditMaps | null>(null);
  useEffect(() => {
    if (!left.current || !right.current || !overview.current) return;
    const l = L.map(left.current, detail());
    const r = L.map(right.current, detail());
    const o = L.map(overview.current, {
      crs,
      zoomControl: false,
      attributionControl: false,
      dragging: false,
      scrollWheelZoom: false,
      doubleClickZoom: false,
      boxZoom: false,
      keyboard: false,
      touchZoom: false,
      zoomSnap: 0.25,
      minZoom: -3,
    });
    const v = useEditView.getState();
    if (v.center && v.zoom !== null) l.setView(v.center, v.zoom);
    else l.fitBounds(frameBounds(currentFrame()));
    r.setView(l.getCenter(), l.getZoom());
    o.fitBounds(frameBounds(currentFrame()));
    const box = L.rectangle(l.getBounds(), { color: "#ffd400", weight: 2, fillOpacity: 0.12, interactive: false }).addTo(o);

    let syncing = false;
    const follow = (from: L.Map, to: L.Map) => () => {
      if (syncing) return;
      syncing = true;
      to.setView(from.getCenter(), from.getZoom(), { animate: false });
      syncing = false;
      box.setBounds(l.getBounds());
    };
    l.on("move zoom", follow(l, r));
    r.on("move zoom", follow(r, l));
    l.on("moveend", () => {
      const c = l.getCenter();
      useEditView.getState().set([c.lat, c.lng], l.getZoom());
    });
    let dragging = false;
    const moveTo = (e: L.LeafletMouseEvent) => l.setView(e.latlng, l.getZoom(), { animate: false });
    o.on("mousedown", (e: L.LeafletMouseEvent) => {
      dragging = true;
      moveTo(e);
    });
    o.on("mousemove", (e: L.LeafletMouseEvent) => {
      if (dragging) moveTo(e);
    });
    const up = () => (dragging = false);
    window.addEventListener("mouseup", up);

    const resize = new ResizeObserver(() => {
      l.invalidateSize();
      r.invalidateSize();
      o.invalidateSize();
      box.setBounds(l.getBounds());
    });
    resize.observe(left.current);
    resize.observe(right.current);
    setMaps({ left: l, right: r, overview: o });
    return () => {
      window.removeEventListener("mouseup", up);
      resize.disconnect();
      l.remove();
      r.remove();
      o.remove();
      setMaps(null);
    };
  }, [left, right, overview]);
  return maps;
}

/**
 * Makes one detail map and the overview in the given hosts (once), for an editing screen with a single map (the points
 * of interest): the overview's box is the map's view, pressing or dragging there moves the map; the view is the one the
 * other editing screens keep (going from the roads to the points shows the same place).
 */
export function useEditMap(host: RefObject<HTMLDivElement | null>, overview: RefObject<HTMLDivElement | null>): { map: L.Map; overview: L.Map } | null {
  const [maps, setMaps] = useState<{ map: L.Map; overview: L.Map } | null>(null);
  useEffect(() => {
    if (!host.current || !overview.current) return;
    const m = L.map(host.current, DETAIL);
    const o = L.map(overview.current, {
      crs,
      zoomControl: false,
      attributionControl: false,
      dragging: false,
      scrollWheelZoom: false,
      doubleClickZoom: false,
      boxZoom: false,
      keyboard: false,
      touchZoom: false,
      zoomSnap: 0.25,
      minZoom: -3,
    });
    const v = useEditView.getState();
    if (v.center && v.zoom !== null) m.setView(v.center, v.zoom);
    else m.fitBounds(frameBounds(currentFrame()));
    o.fitBounds(frameBounds(currentFrame()));
    const box = L.rectangle(m.getBounds(), { color: "#ffd400", weight: 2, fillOpacity: 0.12, interactive: false }).addTo(o);
    m.on("move zoom", () => box.setBounds(m.getBounds()));
    m.on("moveend", () => {
      const c = m.getCenter();
      useEditView.getState().set([c.lat, c.lng], m.getZoom());
    });
    let dragging = false;
    const moveTo = (e: L.LeafletMouseEvent) => m.setView(e.latlng, m.getZoom(), { animate: false });
    o.on("mousedown", (e: L.LeafletMouseEvent) => {
      dragging = true;
      moveTo(e);
    });
    o.on("mousemove", (e: L.LeafletMouseEvent) => {
      if (dragging) moveTo(e);
    });
    const up = () => (dragging = false);
    window.addEventListener("mouseup", up);
    const resize = new ResizeObserver(() => {
      m.invalidateSize();
      o.invalidateSize();
      box.setBounds(m.getBounds());
    });
    resize.observe(host.current);
    setMaps({ map: m, overview: o });
    return () => {
      window.removeEventListener("mouseup", up);
      resize.disconnect();
      m.remove();
      o.remove();
      setMaps(null);
    };
  }, [host, overview]);
  return maps;
}

/**
 * Selection in an area on a map (the tool "select area" of the editing screens): a rectangle dragged, or with Ctrl the
 * outline traced while dragging (Shift adds to the selection). The wheel button's drag moves the map meanwhile, as the
 * map's own dragging is off while the tool is on. Calls back with the rectangle (west, south, east, north) or the outline
 * (game x, y). Returns what takes the handlers away.
 */
export function areaSelect(
  map: L.Map,
  active: () => boolean,
  colour: string,
  onRect: (west: number, south: number, east: number, north: number, add: boolean) => void,
  onOutline: (corners: { x: number; y: number }[], add: boolean) => void,
): () => void {
  let boxStart: L.LatLng | null = null;
  let boxShape: L.Rectangle | null = null;
  let traced: L.LatLng[] | null = null;
  let tracedShape: L.Polygon | null = null;
  let pan: L.Point | null = null;
  const endPan = () => {
    pan = null;
  };
  const onDown = (e: L.LeafletMouseEvent) => {
    if (!active()) return;
    const button = (e.originalEvent as MouseEvent).button;
    if (button === 1) {
      e.originalEvent.preventDefault();
      pan = e.containerPoint;
      return;
    }
    if (button !== 0) return;
    if ((e.originalEvent as MouseEvent).ctrlKey) {
      traced = [e.latlng];
      tracedShape = L.polygon(traced, { color: colour, weight: 2, dashArray: "6 4", fillOpacity: 0.1, interactive: false }).addTo(map);
      return;
    }
    boxStart = e.latlng;
    boxShape = L.rectangle(L.latLngBounds(e.latlng, e.latlng), { color: colour, weight: 2, dashArray: "6 4", fillOpacity: 0.1, interactive: false }).addTo(map);
  };
  const onMove = (e: L.LeafletMouseEvent) => {
    if (pan) {
      map.panBy(pan.subtract(e.containerPoint), { animate: false });
      pan = e.containerPoint;
      return;
    }
    if (traced && tracedShape) {
      const last = map.latLngToContainerPoint(traced[traced.length - 1]);
      if (last.distanceTo(e.containerPoint) >= 4) {
        traced.push(e.latlng);
        tracedShape.setLatLngs(traced);
      }
      return;
    }
    if (boxStart && boxShape) boxShape.setBounds(L.latLngBounds(boxStart, e.latlng));
  };
  const onUp = (e: L.LeafletMouseEvent) => {
    if (pan) {
      pan = null;
      return;
    }
    const add = (e.originalEvent as MouseEvent).shiftKey;
    if (traced && tracedShape) {
      const corners = [...traced, e.latlng].map((p) => ({ x: p.lng, y: p.lat }));
      traced = null;
      tracedShape.remove();
      tracedShape = null;
      onOutline(corners, add);
      return;
    }
    if (boxStart && boxShape) {
      const b = L.latLngBounds(boxStart, e.latlng);
      boxShape.remove();
      boxShape = null;
      boxStart = null;
      onRect(b.getWest(), b.getSouth(), b.getEast(), b.getNorth(), add);
    }
  };
  map.on("mousedown", onDown);
  map.on("mousemove", onMove);
  map.on("mouseup", onUp);
  window.addEventListener("mouseup", endPan);
  return () => {
    window.removeEventListener("mouseup", endPan);
    map.off("mousedown", onDown);
    map.off("mousemove", onMove);
    map.off("mouseup", onUp);
    boxShape?.remove();
    tracedShape?.remove();
  };
}

/** True when a point lies in a polygon (even-odd). */
export function insidePolygon(x: number, y: number, corners: readonly { x: number; y: number }[]): boolean {
  let inside = false;
  for (let i = 0, j = corners.length - 1; i < corners.length; j = i++) {
    const a = corners[i], b = corners[j];
    if (a.y > y !== b.y > y && x < ((b.x - a.x) * (y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
  }
  return inside;
}

/** A background choice: a map of the project (its id), the postal code map, or nothing. */
export const POSTAL = "postal";
export const NONE = "none";

export function backgroundLayer(id: string, key: string, version: number, options?: L.TileLayerOptions): L.GridLayer | null {
  if (id === NONE) return null;
  if (id === POSTAL) return postalLayer(options);
  return projectLayer(id, key, version, options);
}

/**
 * Keeps one tile layer on a map for a choice: replaces it when the choice (or the tiles' version) changes. Returns the
 * layer shown.
 */
export function useLayer(map: L.Map | null, make: () => L.GridLayer | null, deps: unknown[]): RefObject<L.GridLayer | null> {
  const shown = useRef<L.GridLayer | null>(null);
  useEffect(() => {
    if (!map) return;
    const next = make();
    next?.addTo(map);
    shown.current?.remove();
    shown.current = next;
    return () => {
      next?.remove();
      if (shown.current === next) shown.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [map, ...deps]);
  return shown;
}

