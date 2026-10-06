import L from "leaflet";
import { currentFrame, frameBounds } from "../../shared/mapFrame";

/**
 * The road shapes in a map's colours, roads only (transparent elsewhere), drawn by the server for the road editor's
 * right map. `version` is the shapes' version: new shapes load new tiles.
 */
export function roadShapesLayer(map: string, key: string, version: string, options?: L.TileLayerOptions): L.TileLayer {
  return L.tileLayer(`/api/project/road-editor/shapes/${map}/{z}/{x}/{y}.png?p=${key}&v=${encodeURIComponent(version)}`, {
    tileSize: 256,
    minNativeZoom: 0,
    maxNativeZoom: 10,
    maxZoom: 10,
    noWrap: true,
    bounds: frameBounds(currentFrame()),
    ...options,
  });
}
