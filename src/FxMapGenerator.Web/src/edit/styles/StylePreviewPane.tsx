import L from "leaflet";
import "leaflet/dist/leaflet.css";
import { type ReactNode, type RefObject, useEffect, useRef, useState } from "react";
import { api, ApiError } from "../../shared/api";
import { Button, Info, type Reason, Select, Slider, TextField } from "../../shared/controls";
import { helpAttributes } from "../../shared/controls/help";
import { type Lang, type MessageKey, useI18nStore, useT } from "../../shared/i18n";
import { blockAt, blockIndexOf, crs, currentFrame, frameBounds, frameKey, postalLayer, projectKey, projectLayer, replaceLayer } from "../../shared/mapFrame";
import { makesJapanese, type MapFrame, type PreviewSource, type ProjectPreviewStatus, type SampleStatus, type StylePick, type StylePickPart, type StylePickStep, type StyleSchema, type StyleValues } from "../../shared/types";
import { useProjectStore } from "../../project/store";
import { type FocusTarget, useFocusStore } from "./focus";
import { nameOf, shown, valueAt } from "./schema";

type View = { center: L.LatLng; zoom: number };

/** The map under the two on the project's data: none, PostalMap (the map screens' base), or the project's satellite map. */
type Under = "none" | "postal" | "satellite";

/**
 * What the preview shows, kept while the app is open: the source chosen, per source the window's middle and the view,
 * the window's side, the left map's values, the labels' language, the map shown under the two (on the project's data).
 */
const kept: {
  source: PreviewSource | null;
  place: Record<PreviewSource, [number, number] | null>;
  view: Record<PreviewSource, View | null>;
  size: number;
  left: "saved" | "base";
  language: "en" | "ja";
  under: Under;
} = {
  source: null,
  place: { sample: null, project: null },
  view: { sample: null, project: null },
  size: 500,
  left: "saved",
  language: "en",
  under: "none",
};

/** How long the right map waits after a change before it is drawn again. */
const WAIT_MS = 250;
/** The detail maps' zooms: out to a whole 4 km window, in to twice the drawing's pixels (z9; the tiles are z8). */
const MIN_ZOOM = 2;
const MAX_ZOOM = 9;
/**
 * The detail maps' options: whole zooms only (the z8 tiles made smaller by a power of two sit on whole pixels; at the
 * zooms between, the browser leaves light seams between them).
 */
const DETAIL: L.MapOptions = { crs, zoomControl: false, attributionControl: false, minZoom: MIN_ZOOM, maxZoom: MAX_ZOOM, zoomSnap: 1, zoomDelta: 1, zoomAnimation: false, doubleClickZoom: false, boxZoom: false };

interface Props {
  schema: StyleSchema;
  /** The values as saved (a bundled style's values). */
  saved: StyleValues;
  /** The base style's values (null for a bundled style: its own). */
  base: StyleValues | null;
  /** The values being edited. */
  edited: StyleValues;
}

type Side = "left" | "right";

/** What a source offers the preview: the frame it can draw, the place first shown, the windows' sides. */
interface Land {
  source: PreviewSource;
  frame: [number, number, number, number];
  place: [number, number];
  sizes: number[];
}

/**
 * The style editor's preview: a window (500 m to 4 km around the point chosen on the small map of the whole) of the
 * sample land or of the project's own data, drawn as the maps draw it, as z8 tiles the browser makes smaller from
 * further out; on the left with the saved values (or the base style's), on the right with the values being edited;
 * both maps move together (and PostalMap or the satellite map under them, on the project's data). A press on a map tells where the colour
 * of that point comes from. The sample land is made the first time it is shown (its steps without the game).
 */
export function StylePreviewPane({ schema, saved, base, edited }: Props) {
  const t = useT();
  const project = useProjectStore((s) => s.project);
  const jobState = useProjectStore((s) => s.job?.state);
  const [own, setOwn] = useState<ProjectPreviewStatus | null>(null);
  const [status, setStatus] = useState<SampleStatus | null>(null);
  const [chosen, setChosen] = useState<PreviewSource | null>(kept.source);
  const [place, setPlace] = useState<Record<PreviewSource, [number, number] | null>>(kept.place);
  const [size, setSize] = useState(kept.size);
  const [left, setLeft] = useState(kept.left);
  const [chosenLanguage, setLanguage] = useState(kept.language);
  // the labels in Japanese only when the project makes Japanese maps (else its maps are all English)
  const japanese = project ? makesJapanese(project.file) : false;
  const language = japanese ? chosenLanguage : "en";
  const [under, setUnder] = useState<Under>(kept.under);
  const tileVersion = useProjectStore((s) => s.tileVersion);
  const [satellite, setSatellite] = useState(false);
  kept.source = chosen;
  kept.place = place;
  kept.size = size;
  kept.left = left;
  kept.language = chosenLanguage;
  kept.under = under;

  // whether the project has satellite tiles to show under the maps (the map view's list of maps with tiles)
  useEffect(() => {
    let live = true;
    api.exportInfo().then(
      (info) => live && setSatellite(info.maps.some((m) => m.map === "satellite" && m.tiles > 0)),
      () => live && setSatellite(false),
    );
    return () => {
      live = false;
    };
  }, [project, tileVersion]);

  // the preview on the project's data: asked again when the project changes or its run starts or ends
  useEffect(() => {
    let live = true;
    api.projectPreview().then(
      (s) => live && setOwn(s),
      (e) => live && setOwn({ state: "none", waiting: e instanceof Error ? e.message : String(e), frame: [], blocks: "", recommended: [], places: [], sizes: [500], map: currentFrame() }),
    );
    return () => {
      live = false;
    };
  }, [project, jobState]);
  // the project's data when there is some, unless the sample land was chosen
  const source: PreviewSource | null = !own ? null : chosen === "sample" || own.state !== "ready" ? "sample" : "project";

  // the sample land: made when first shown; followed while it is being made
  useEffect(() => {
    if (source !== "sample") return;
    let live = true;
    let timer = 0;
    const follow = async () => {
      try {
        let s = await api.sampleStatus();
        if (s.state === "none") s = await api.makeSample();
        if (!live) return;
        setStatus(s);
        if (s.state === "making") timer = window.setTimeout(() => void follow(), 700);
      } catch (e) {
        if (live) setStatus({ state: "failed", progress: 0, message: e instanceof Error ? e.message : String(e), frame: [0, 0, 0, 0], place: [0, 0], sizes: [500] });
      }
    };
    void follow();
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [source]);

  const sources = <SourceButtons source={source} own={own} onChoose={setChosen} />;
  const land: Land | null =
    source === "project" && own
      ? { source, frame: own.frame as Land["frame"], place: firstPlace(own), sizes: own.sizes }
      : source === "sample" && status?.state === "ready"
        ? { source, frame: status.frame, place: status.place, sizes: status.sizes }
        : null;
  if (!land) {
    return (
      <div className="style-preview">
        <div className="style-preview-bar">
          <Info help="help.styles.preview" className="style-preview-title">
            {t("styles.preview.title")}
          </Info>
          {sources}
        </div>
        <div className="style-preview-wait">
          {source === "sample" && status?.state === "failed" ? (
            <>
              <Info help="help.styles.preview.failed" block className="text-error">
                {t("styles.preview.failed", { message: status.message ?? "" })}
              </Info>
              <Button
                help="help.styles.preview.retry"
                onClick={() => {
                  setStatus(null);
                  void api.makeSample().then(setStatus, () => undefined);
                }}
              >
                {t("styles.preview.retry")}
              </Button>
            </>
          ) : source === "sample" ? (
            <Info help="help.styles.preview.making" block className="muted">
              {t("styles.preview.making", { n: Math.round((status?.progress ?? 0) * 100) })}
            </Info>
          ) : null}
        </div>
      </div>
    );
  }
  const at = place[land.source] ?? land.place;
  return (
    <Ready
      key={land.source}
      schema={schema}
      land={land}
      own={land.source === "project" ? own : null}
      onOwn={setOwn}
      sources={sources}
      place={at}
      onPlace={(p) => setPlace((all) => ({ ...all, [land.source]: p }))}
      size={land.sizes.includes(size) ? size : land.sizes[0]}
      onSize={setSize}
      leftValues={left === "base" && base ? base : saved}
      edited={edited}
      left={left}
      onLeft={setLeft}
      hasBase={!!base}
      language={language}
      japanese={japanese}
      onLanguage={setLanguage}
      under={land.source !== "project" || (under === "satellite" && !satellite) ? "none" : under}
      onUnder={setUnder}
      satellite={satellite}
    />
  );
}

/** The place first shown on the project's data: the first recommended place with data, else the middle of its blocks' frame. */
function firstPlace(own: ProjectPreviewStatus): [number, number] {
  const p = own.recommended.find((r) => r.ready) ?? own.places.find((r) => r.ready);
  if (p) return [p.x, p.y];
  const [x0, y0, x1, y1] = own.frame;
  return [Math.round((x0 + x1) / 2), Math.round((y0 + y1) / 2)];
}

/** The sample land or the project's data. */
function SourceButtons({ source, own, onChoose }: { source: PreviewSource | null; own: ProjectPreviewStatus | null; onChoose(s: PreviewSource): void }) {
  const t = useT();
  return (
    <span className="edit-buttons" data-guide="styles.source">
      <Button help="help.styles.preview.source.sample" pressed={source === "sample"} onClick={() => onChoose("sample")}>
        {t("styles.preview.source.sample")}
      </Button>
      <Button
        help="help.styles.preview.source.project"
        pressed={source === "project"}
        disabledReason={own?.state === "ready" ? null : "reason.styles.preview.noData"}
        onClick={() => onChoose("project")}
      >
        {t("styles.preview.source.project")}
      </Button>
    </span>
  );
}

interface ReadyProps {
  schema: StyleSchema;
  land: Land;
  /** The preview's state on the project's data (its places), when that is shown. */
  own: ProjectPreviewStatus | null;
  onOwn(s: ProjectPreviewStatus): void;
  sources: ReactNode;
  place: [number, number];
  onPlace(p: [number, number]): void;
  size: number;
  onSize(s: number): void;
  leftValues: StyleValues;
  edited: StyleValues;
  left: "saved" | "base";
  onLeft(v: "saved" | "base"): void;
  hasBase: boolean;
  language: "en" | "ja";
  /** The project makes Japanese maps: the labels' language can be chosen. */
  japanese: boolean;
  onLanguage(v: "en" | "ja"): void;
  /** The map under the two (none on the sample land). */
  under: Under;
  onUnder(v: Under): void;
  /** Whether the project has satellite tiles. */
  satellite: boolean;
}

/** A window's side as text: 500 m, 1 km, ... */
function sizeText(m: number): string {
  return m >= 1000 ? `${m / 1000} km` : `${m} m`;
}

function Ready({ schema, land, own, onOwn, sources, place, onPlace, size, onSize, leftValues, edited, left, onLeft, hasBase, language, japanese, onLanguage, under, onUnder, satellite }: ReadyProps) {
  const t = useT();
  const leftHost = useRef<HTMLDivElement>(null);
  const rightHost = useRef<HTMLDivElement>(null);
  const overviewHost = useRef<HTMLDivElement>(null);
  const underHost = useRef<HTMLDivElement>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const choose = (p: [number, number]) => {
    setNotice(null);
    onPlace(p);
  };
  const maps = usePreviewMaps(leftHost, rightHost, overviewHost, land, own?.blocks ?? null, own?.map ?? null, place, size, choose, () => setNotice(t("styles.preview.noCells")));
  useUnderMap(underHost, maps, under);
  const leftState = useDrawn(maps?.left ?? null, land.source, "left", leftValues, place, size, language, 0);
  const rightState = useDrawn(maps?.right ?? null, land.source, "right", edited, place, size, language, WAIT_MS);
  const [picked, setPicked] = useState<{ side: Side; at: L.LatLng } | null>(null);
  // a press on a detail map tells where the colour of that point comes from
  useEffect(() => {
    if (!maps) return;
    const on = (side: Side) => (e: L.LeafletMouseEvent) => setPicked({ side, at: e.latlng });
    const l = on("left"),
      r = on("right");
    maps.left.on("click", l);
    maps.right.on("click", r);
    return () => {
      maps.left.off("click", l);
      maps.right.off("click", r);
    };
  }, [maps]);
  const note = (s: DrawState) => (s.error ? t("styles.preview.error", { message: s.error }) : s.busy ? t("styles.preview.drawing") : s.seconds !== null ? t("styles.preview.seconds", { s: s.seconds.toFixed(2) }) : "");
  const sizeIndex = Math.max(0, land.sizes.indexOf(size));
  const pickBox = (side: Side) =>
    picked?.side === side && maps ? (
      <PickBox
        schema={schema}
        map={side === "left" ? maps.left : maps.right}
        at={picked.at}
        source={land.source}
        values={side === "left" ? leftValues : edited}
        place={place}
        size={size}
        language={language}
        onClose={() => setPicked(null)}
      />
    ) : null;
  return (
    <div className="style-preview" data-guide="styles.preview">
      <div className="style-preview-bar">
        <Info help="help.styles.preview" className="style-preview-title">
          {t("styles.preview.title")}
        </Info>
        {sources}
        <span className="edit-buttons">
          <Button help="help.styles.preview.saved" pressed={left === "saved"} onClick={() => onLeft("saved")}>
            {t("styles.preview.saved")}
          </Button>
          <Button help="help.styles.preview.base" pressed={left === "base"} disabledReason={hasBase ? null : "reason.styles.preview.bundled"} onClick={() => onLeft("base")}>
            {t("styles.preview.base")}
          </Button>
        </span>
        {japanese && (
          <Select
            help="help.styles.preview.language"
            label={t("styles.preview.language")}
            value={language}
            options={[
              { value: "en", label: t("lang.en") },
              { value: "ja", label: t("lang.ja") },
            ]}
            onChange={onLanguage}
          />
        )}
        <Slider
          help="help.styles.preview.size"
          label={t("styles.preview.size")}
          value={sizeIndex}
          min={0}
          max={land.sizes.length - 1}
          step={1}
          marks={land.sizes.map((_, i) => i)}
          display={sizeText(land.sizes[sizeIndex])}
          onChange={(i) => onSize(land.sizes[i])}
        />
        {own && <Places own={own} place={place} onPlace={choose} onOwn={onOwn} onAdd={() => setAdding(true)} />}
        {own && (
          // labelled like the lists of the bar
          <span className="select" {...helpAttributes("help.styles.preview.under")}>
            <span className="select-label">{t("styles.preview.under")}</span>
            <span className="edit-buttons">
              <Button help="help.styles.preview.under.none" pressed={under === "none"} onClick={() => onUnder("none")}>
                {t("styles.preview.under.none")}
              </Button>
              <Button help="help.styles.preview.under.postal" pressed={under === "postal"} onClick={() => onUnder("postal")}>
                {t("base.postal")}
              </Button>
              <Button
                help="help.styles.preview.under.satellite"
                pressed={under === "satellite"}
                disabledReason={satellite ? null : "reason.styles.preview.noSatellite"}
                onClick={() => onUnder("satellite")}
              >
                {t("map.satellite")}
              </Button>
            </span>
          </span>
        )}
        {notice && (
          <Info help="help.styles.preview.noCells" className="text-error">
            {notice}
          </Info>
        )}
      </div>
      <div className={`style-preview-maps${under !== "none" ? " with-postal" : ""}`}>
        <div className="style-preview-map">
          <div ref={leftHost} className="map" {...helpAttributes("help.styles.preview.map")} />
          <Info help="help.styles.preview.leftMap" className="style-preview-caption">
            {left === "base" && hasBase ? t("styles.preview.base") : t("styles.preview.saved")}
            <span className="muted"> {note(leftState)}</span>
          </Info>
          {pickBox("left")}
        </div>
        <div className="style-preview-map">
          <div ref={rightHost} className="map" {...helpAttributes("help.styles.preview.map")} />
          <Info help="help.styles.preview.rightMap" className="style-preview-caption">
            {t("styles.preview.editing")}
            <span className="muted"> {note(rightState)}</span>
          </Info>
          {pickBox("right")}
        </div>
        {under !== "none" && (
          <div className="style-preview-map style-preview-postal">
            <div ref={underHost} className="map" {...helpAttributes("help.styles.preview.underMap")} />
            <Info help="help.styles.preview.underMap" className="style-preview-caption">
              {under === "postal" ? t("base.postal") : t("map.satellite")}
            </Info>
          </div>
        )}
        <div
          ref={overviewHost}
          className={`style-preview-overview${land.source === "project" ? " is-project" : ""}`}
          {...helpAttributes(land.source === "project" ? "help.styles.preview.overview.project" : "help.styles.preview.overview")}
        />
      </div>
      {adding && own && (
        <AddPlaceDialog
          own={own}
          place={place}
          values={edited}
          size={size}
          language={language}
          onDone={(s) => {
            if (s) onOwn(s);
            setAdding(false);
          }}
        />
      )}
    </div>
  );
}

/** Where a place of the project's data is kept: a recommended one (r + its index), one of the project's (p + its index), or none of them (""). */
function placeKey(own: ProjectPreviewStatus, place: [number, number]): string {
  const same = (p: { x: number; y: number }) => Math.round(p.x) === Math.round(place[0]) && Math.round(p.y) === Math.round(place[1]);
  const r = own.recommended.findIndex(same);
  if (r >= 0) return `r${r}`;
  const p = own.places.findIndex(same);
  return p >= 0 ? `p${p}` : "";
}

/** The places of the project's data: the recommended ones and the project's own; add the window's middle, delete one of the project's. */
function Places({ own, place, onPlace, onOwn, onAdd }: { own: ProjectPreviewStatus; place: [number, number]; onPlace(p: [number, number]): void; onOwn(s: ProjectPreviewStatus): void; onAdd(): void }) {
  const t = useT();
  const key = placeKey(own, place);
  const label = (p: { name: string; ready: boolean }) => (p.ready ? p.name : t("styles.preview.place.noData", { name: p.name }));
  const options = [
    ...(key === "" ? [{ value: "", label: t("styles.preview.place.picked") }] : []),
    ...own.recommended.map((p, i) => ({ value: `r${i}`, label: label(p), group: t("styles.preview.place.group.recommended"), disabled: !p.ready })),
    ...own.places.map((p, i) => ({ value: `p${i}`, label: label(p), group: t("styles.preview.place.group.project"), disabled: !p.ready })),
  ];
  const go = (k: string) => {
    const p = k.startsWith("r") ? own.recommended[Number(k.slice(1))] : k.startsWith("p") ? own.places[Number(k.slice(1))] : undefined;
    if (p?.ready) onPlace([p.x, p.y]);
  };
  const removeReason: Reason | null = key.startsWith("p") ? null : "reason.styles.preview.place.notOwn";
  const addReason: Reason | null = key === "" ? null : "reason.styles.preview.place.listed";
  const remove = async () => {
    const i = Number(key.slice(1));
    const gone = own.places[i];
    if (!gone || !window.confirm(t("styles.preview.place.remove.confirm", { name: gone.name }))) return;
    onOwn(await api.savePreviewPlaces(own.places.filter((_, j) => j !== i).map(({ name, x, y }) => ({ name, x, y }))));
  };
  return (
    <>
      <Select help="help.styles.preview.place" label={t("styles.preview.place")} value={key} options={options} onChange={go} />
      <Button help="help.styles.preview.place.add" disabledReason={addReason} onClick={onAdd}>
        {t("styles.preview.place.add")}
      </Button>
      <Button help="help.styles.preview.place.remove" disabledReason={removeReason} onClick={() => void remove()}>
        {t("styles.preview.place.remove")}
      </Button>
    </>
  );
}

/** Keeps the window's middle as a place of the project, under a name (at first the name of the zone there). */
function AddPlaceDialog({ own, place, values, size, language, onDone }: { own: ProjectPreviewStatus; place: [number, number]; values: StyleValues; size: number; language: string; onDone(s: ProjectPreviewStatus | null): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const typed = useRef(false);
  // the zone's name at the middle, as the name to start from
  useEffect(() => {
    const ctl = new AbortController();
    api.stylePick("project", values, place[0], place[1], place[0], place[1], size, language, ctl.signal).then(
      (p) => {
        if (!typed.current) setName((lang === "ja" ? (p.point.zoneJa ?? p.point.zoneEn) : p.point.zoneEn) ?? "");
      },
      () => undefined,
    );
    return () => ctl.abort();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onDone(null);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onDone]);
  const title = t("styles.preview.place.dialog.title");
  const ok = async () => {
    try {
      const list = [...own.places.map(({ name: n, x, y }) => ({ name: n, x, y })), { name: name.trim(), x: Math.round(place[0]), y: Math.round(place[1]) }];
      onDone(await api.savePreviewPlaces(list));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form style-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.styles.preview.place.dialog" block className="muted">
            {t("styles.preview.place.dialog.intro", { x: Math.round(place[0]), y: Math.round(place[1]) })}
          </Info>
          <TextField
            help="help.styles.preview.place.name"
            label={t("styles.preview.place.name")}
            value={name}
            onChange={(v) => {
              typed.current = true;
              setName(v);
            }}
          />
          {error && (
            <Info help="help.styles.preview.place.dialog" block className="text-error">
              {error}
            </Info>
          )}
        </div>
        <div className="modal-foot">
          <Button help="help.styles.dialog.cancel" onClick={() => onDone(null)}>
            {t("common.cancel")}
          </Button>
          <Button help="help.styles.preview.place.dialog.ok" variant="primary" disabledReason={name.trim() ? null : "reason.styles.preview.place.noName"} onClick={() => void ok()}>
            {t("styles.preview.place.dialog.ok")}
          </Button>
        </div>
      </div>
    </div>
  );
}

interface PreviewMaps {
  left: L.Map;
  right: L.Map;
  overview: L.Map;
}

/** A window's bounds (a square of `size` m around its middle). */
function windowBounds(place: [number, number], size: number): L.LatLngBounds {
  const h = size / 2;
  return L.latLngBounds([place[1] - h, place[0] - h], [place[1] + h, place[0] + h]);
}

/** The project's blocks the preview cannot draw, grey, as a picture of a pixel per block of the project's frame. */
function blocksPicture(blocks: string, frame: MapFrame): string {
  const canvas = document.createElement("canvas");
  canvas.width = frame.cols;
  canvas.height = frame.rows;
  const g = canvas.getContext("2d")!;
  const img = g.createImageData(frame.cols, frame.rows);
  for (let i = 0; i < frame.cols * frame.rows; i++)
    if (blocks[i] !== "1") img.data.set([110, 110, 110, 170], i * 4);
  g.putImageData(img, 0, 0);
  return canvas.toDataURL();
}

/**
 * The preview's two maps (moving together) and the small map of the whole: the sample land's picture, or PostalMap
 * with the project's blocks the preview cannot draw in grey; the window's square; a press moving the window there
 * (inside the land's frame; on the project's data, on a block it can draw). A new window (its place or its side) fits
 * the maps to it.
 */
function usePreviewMaps(leftHost: RefObject<HTMLDivElement | null>, rightHost: RefObject<HTMLDivElement | null>, overviewHost: RefObject<HTMLDivElement | null>,
  land: Land, blocks: string | null, grid: MapFrame | null, place: [number, number], size: number, onPlace: (p: [number, number]) => void, onNoData: () => void): PreviewMaps | null {
  const [maps, setMaps] = useState<PreviewMaps | null>(null);
  const box = useRef<L.Rectangle | null>(null);
  const placeRef = useRef(onPlace);
  placeRef.current = onPlace;
  const noDataRef = useRef(onNoData);
  noDataRef.current = onNoData;
  const blocksRef = useRef(blocks);
  blocksRef.current = blocks;
  const gridRef = useRef(grid);
  gridRef.current = grid;
  const blocksLayer = useRef<L.ImageOverlay | null>(null);
  useEffect(() => {
    if (!leftHost.current || !rightHost.current || !overviewHost.current) return;
    const l = L.map(leftHost.current, DETAIL);
    const r = L.map(rightHost.current, DETAIL);
    const [x0, y0, x1, y1] = land.frame;
    const frame = L.latLngBounds([y1, x0], [y0, x1]);
    const o = L.map(overviewHost.current, {
      crs, zoomControl: false, attributionControl: false, dragging: false, scrollWheelZoom: false, doubleClickZoom: false, boxZoom: false,
      keyboard: false, touchZoom: false, zoomSnap: 0.1, minZoom: -4,
    });
    if (land.source === "sample") L.imageOverlay("/api/styles/sample/overview.png", frame).addTo(o);
    else postalLayer().addTo(o);
    o.fitBounds(frame, { padding: [2, 2] });
    box.current = L.rectangle(windowBounds(place, size), { color: "#ffd400", weight: 2, fillOpacity: 0.15, interactive: false }).addTo(o);
    const view = kept.view[land.source];
    if (view) l.setView(view.center, view.zoom);
    else l.fitBounds(windowBounds(place, size));
    r.setView(l.getCenter(), l.getZoom());
    let syncing = false;
    const follow = (from: L.Map, to: L.Map) => () => {
      if (syncing) return;
      syncing = true;
      to.setView(from.getCenter(), from.getZoom(), { animate: false });
      syncing = false;
      kept.view[land.source] = { center: l.getCenter(), zoom: l.getZoom() };
    };
    l.on("move zoom", follow(l, r));
    r.on("move zoom", follow(r, l));
    o.on("click", (e: L.LeafletMouseEvent) => {
      if (land.source === "project") {
        const g = gridRef.current;
        const b = g ? blockAt(e.latlng, g) : null;
        if (!b || !g || blocksRef.current?.[blockIndexOf(b[0], b[1], g)] !== "1") {
          noDataRef.current();
          return;
        }
        placeRef.current([Math.round(e.latlng.lng), Math.round(e.latlng.lat)]);
        return;
      }
      const x = Math.min(x1, Math.max(x0, e.latlng.lng)),
        y = Math.min(y0, Math.max(y1, e.latlng.lat));
      placeRef.current([Math.round(x), Math.round(y)]);
    });
    const resize = new ResizeObserver(() => {
      l.invalidateSize();
      r.invalidateSize();
      o.invalidateSize();
    });
    resize.observe(leftHost.current);
    resize.observe(rightHost.current);
    setMaps({ left: l, right: r, overview: o });
    return () => {
      resize.disconnect();
      l.remove();
      r.remove();
      o.remove();
      blocksLayer.current = null;
      setMaps(null);
    };
    // the maps are made once per land; the window moves them below
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [leftHost, rightHost, overviewHost, land.source, land.frame[0], land.frame[1], land.frame[2], land.frame[3]]);

  // the project's blocks the preview cannot draw, grey on the small map
  const gk = frameKey(grid);
  useEffect(() => {
    if (!maps || land.source !== "project" || blocks === null || !grid) return;
    blocksLayer.current?.remove();
    blocksLayer.current = L.imageOverlay(blocksPicture(blocks, grid), frameBounds(grid), { className: "style-preview-blocks", interactive: false }).addTo(maps.overview);
    box.current?.bringToFront();
    // the grid is followed by its key
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [maps, land.source, blocks, gk]);

  // a new window: its square on the small map, the detail maps fitted to it
  const first = useRef(true);
  useEffect(() => {
    if (!maps) return;
    const b = windowBounds(place, size);
    box.current?.setBounds(b);
    if (first.current) {
      first.current = false;
      return;
    }
    maps.left.fitBounds(b, { animate: false });
  }, [maps, place[0], place[1], size]); // eslint-disable-line react-hooks/exhaustive-deps
  return maps;
}

/**
 * The map under the two, moving with them: PostalMap (the map screens' base) or the project's satellite tiles (the
 * satellite map of the last run; a run writing tiles loads them again).
 */
function useUnderMap(host: RefObject<HTMLDivElement | null>, maps: PreviewMaps | null, under: Under) {
  const project = useProjectStore((s) => s.project);
  const tileVersion = useProjectStore((s) => s.tileVersion);
  useEffect(() => {
    if (under === "none" || !maps || !host.current || !project) return;
    const p = L.map(host.current, DETAIL);
    (under === "postal" ? postalLayer() : projectLayer("satellite", projectKey(project.workFolder), tileVersion)).addTo(p);
    p.setView(maps.left.getCenter(), maps.left.getZoom());
    let syncing = false;
    const fromLeft = () => {
      if (syncing) return;
      syncing = true;
      p.setView(maps.left.getCenter(), maps.left.getZoom(), { animate: false });
      syncing = false;
    };
    const fromPostal = () => {
      if (syncing) return;
      syncing = true;
      maps.left.setView(p.getCenter(), p.getZoom(), { animate: false });
      syncing = false;
    };
    maps.left.on("move zoom", fromLeft);
    p.on("move zoom", fromPostal);
    const resize = new ResizeObserver(() => p.invalidateSize());
    resize.observe(host.current);
    return () => {
      resize.disconnect();
      maps.left.off("move zoom", fromLeft);
      p.remove();
    };
  }, [host, maps, under, project?.workFolder, tileVersion]); // eslint-disable-line react-hooks/exhaustive-deps
}

interface DrawState {
  busy: boolean;
  seconds: number | null;
  error: string | null;
}

/**
 * Keeps a map's tiles of the window drawn with some values: drawn again (after `wait` ms, a newer change cancelling an
 * older one) when the values, the window or the language change; the new tiles take the old ones' place once they are in.
 */
function useDrawn(map: L.Map | null, source: PreviewSource, side: Side, values: StyleValues, place: [number, number], size: number, language: string, wait: number): DrawState {
  const [state, setState] = useState<DrawState>({ busy: false, seconds: null, error: null });
  const layer = useRef<L.TileLayer | null>(null);
  useEffect(() => {
    if (!map) return;
    const ctl = new AbortController();
    setState((s) => ({ ...s, busy: true, error: null }));
    const timer = window.setTimeout(() => {
      api.stylePreview(source, values, place[0], place[1], size, language, side, ctl.signal).then(
        (d) => {
          if (ctl.signal.aborted) return;
          const [x0, y0, x1, y1] = d.bounds;
          const next = L.tileLayer(`/api/project/styles/preview/tiles/${d.id}/{x}/{y}.png`, {
            tileSize: 256,
            minNativeZoom: 8,
            maxNativeZoom: 8,
            minZoom: MIN_ZOOM,
            maxZoom: MAX_ZOOM,
            noWrap: true,
            bounds: L.latLngBounds([y1, x0], [y0, x1]),
          });
          layer.current = replaceLayer(map, layer.current, next, true);
          setState({ busy: false, seconds: d.seconds, error: null });
        },
        (e) => {
          if (ctl.signal.aborted || (e instanceof ApiError && e.code === "SUPERSEDED") || (e instanceof DOMException && e.name === "AbortError")) return;
          setState({ busy: false, seconds: null, error: e instanceof Error ? e.message : String(e) });
        },
      );
    }, wait);
    return () => {
      ctl.abort();
      window.clearTimeout(timer);
    };
  }, [map, source, values, place[0], place[1], size, language, side, wait]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(
    () => () => {
      layer.current?.remove();
      layer.current = null;
    },
    [map],
  );
  return state;
}

// ---------------------------------------------------------------- where a colour comes from

interface PickBoxProps {
  schema: StyleSchema;
  map: L.Map;
  at: L.LatLng;
  source: PreviewSource;
  values: StyleValues;
  place: [number, number];
  size: number;
  language: string;
  onClose(): void;
}

/** Where the box sits: in the half of the map away from the point pressed (under the caption, or at the bottom), never over the point. */
function boxPlace(map: L.Map, at: L.LatLng): { left: number; top?: number; bottom?: number; maxHeight: number } {
  const p = map.latLngToContainerPoint(at);
  const s = map.getSize();
  return p.y >= s.y / 2 ? { left: 8, top: 40, maxHeight: Math.max(120, p.y - 12 - 40) } : { left: 8, bottom: 8, maxHeight: Math.max(120, s.y - 8 - (p.y + 12)) };
}

/**
 * The box of a point pressed on a preview map: the colour there, the steps of the drawing that give it from the top
 * (each with the rows of the style list it comes from; a press on one shows that row), what the ground picture mixes
 * there, and the point's zone and ground. Asked again when the map's values or window change; Esc closes it.
 */
function PickBox({ schema, map, at, source, values, place, size, language, onClose }: PickBoxProps) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const [pick, setPick] = useState<StylePick | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [, moved] = useState(0);
  const show = useFocusStore((s) => s.show);
  useEffect(() => {
    const ctl = new AbortController();
    setError(null);
    api.stylePick(source, values, at.lng, at.lat, place[0], place[1], size, language, ctl.signal).then(
      (p) => {
        if (!ctl.signal.aborted) setPick(p);
      },
      (e) => {
        if (!ctl.signal.aborted && !(e instanceof DOMException && e.name === "AbortError")) setError(e instanceof Error ? e.message : String(e));
      },
    );
    return () => ctl.abort();
  }, [source, values, at, place[0], place[1], size, language]); // eslint-disable-line react-hooks/exhaustive-deps
  // the point's mark, the box following the map, Esc
  useEffect(() => {
    const mark = L.circleMarker(at, { radius: 6, color: "#ffd400", weight: 2, fill: false, interactive: false }).addTo(map);
    const redraw = () => moved((n) => n + 1);
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    map.on("move zoom", redraw);
    window.addEventListener("keydown", key);
    return () => {
      mark.remove();
      map.off("move zoom", redraw);
      window.removeEventListener("keydown", key);
    };
  }, [map, at]); // eslint-disable-line react-hooks/exhaustive-deps
  const names = namesOf(schema, values, lang);
  const pos = boxPlace(map, at);
  return (
    <div className="style-pick" style={pos}>
      <div className="style-pick-head">
        <Info help="help.styles.pick" className="style-pick-title">
          {t("styles.pick.title")}
        </Info>
        {pick && <Swatch color={pick.color} />}
        {pick && <code className="muted">{pick.color}</code>}
        <Button help="help.styles.pick.close" variant="link" className="style-pick-close" onClick={onClose}>
          ×
        </Button>
      </div>
      {error && (
        <Info help="help.styles.pick" block className="text-error">
          {t("styles.pick.error", { message: error })}
        </Info>
      )}
      {!pick && !error && (
        <Info help="help.styles.pick" block className="muted">
          {t("styles.pick.busy")}
        </Info>
      )}
      {pick && (
        <>
          <ol className="style-pick-steps">
            {pick.steps.map((s, i) => (
              <li key={i}>
                <div className="style-pick-step">
                  {s.kind !== "shade" && s.color && <Swatch color={s.color} />}
                  <Info help="help.styles.pick.step" className="style-pick-what">
                    {stepText(t, s, names, values)}
                    {s.cover < 0.99 && s.kind !== "shade" && <span className="muted"> {t("styles.pick.partial")}</span>}
                  </Info>
                </div>
                <Targets targets={stepTargets(s, pick.point.zone, pick.ground)} names={names} onShow={show} />
                {s.kind === "ground" && pick.ground.length > 0 && (
                  <ul className="style-pick-parts">
                    {pick.ground.map((g, j) => (
                      <li key={j}>
                        <div className="style-pick-step">
                          {g.color && <Swatch color={g.color} />}
                          <Info help="help.styles.pick.part" className="style-pick-what">
                            {partText(t, g, names)}
                          </Info>
                        </div>
                        <Targets targets={partTargets(g, values)} names={names} onShow={show} />
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            ))}
          </ol>
          <Info help="help.styles.pick.point" block className="style-pick-point muted">
            {t("styles.pick.point", { zone: pointZone(pick, lang), ground: t(`viewer.point.ground.${pick.point.ground}` as MessageKey) })}
            {pick.point.water && t("styles.pick.point.water")}
            {pick.point.building && t("styles.pick.point.building")}
          </Info>
        </>
      )}
    </div>
  );
}

function Swatch({ color }: { color: string }) {
  return <span className="style-pick-swatch" style={{ background: color }} />;
}

function pointZone(pick: StylePick, lang: Lang): string {
  const name = lang === "ja" ? (pick.point.zoneJa ?? pick.point.zoneEn) : pick.point.zoneEn;
  return name ? `${name}（${pick.point.zone}）` : pick.point.zone;
}

/** The names the box shows: a row's name by its key (the row shown for the values), a table's column and a column's choice. */
interface RowNames {
  row(key: string): string;
  column(key: string, id: string): string;
  choice(key: string, column: string, value: string): string;
}

function namesOf(schema: StyleSchema, values: StyleValues, lang: Lang): RowNames {
  const item = (key: string) => {
    const all = schema.groups.flatMap((g) => g.items).filter((i) => i.key === key);
    return all.find((i) => shown(i, values)) ?? all[0];
  };
  return {
    row: (key) => nameOf(item(key)?.name, lang) || key,
    column: (key, id) => nameOf(item(key)?.columns?.find((c) => c.id === id)?.name, lang) || id,
    choice: (key, column, value) => nameOf(item(key)?.columns?.find((c) => c.id === column)?.choices?.find((c) => c.value === value)?.name, lang) || value,
  };
}

type Translate = ReturnType<typeof useT>;

/** A step of the drawing in words. */
function stepText(t: Translate, s: StylePickStep, names: RowNames, values: StyleValues): string {
  switch (s.kind) {
    case "shade":
      return t("styles.pick.kind.shade", { f: s.factor.toFixed(2) });
    case "sea": {
      const bands = valueAt(values, "sea.bands");
      const list = Array.isArray(bands) ? (bands as number[]) : [];
      const from = s.band === 0 ? 0 : (list[s.band - 1] ?? 0);
      const bed = names.column("sea.bands", s.bed ?? "sand");
      return s.band < list.length ? t("styles.pick.kind.sea", { from, to: list[s.band], bed }) : t("styles.pick.kind.seaDeep", { from, bed });
    }
    case "building":
      return t("styles.pick.kind.building", { color: names.choice("regions.zones", "building", s.paint ?? "") });
    case "casing":
    case "road":
      return t(`styles.pick.kind.${s.kind}.${s.class === 1 ? 1 : 0}` as MessageKey);
    case "postal":
    case "poi":
    case "zone":
    case "street":
      return t(`styles.pick.kind.${s.kind}` as MessageKey, { text: s.text ?? "" });
    case "groundLayer":
      return t("styles.pick.kind.groundLayer", { paint: s.paint ?? "" });
    default:
      return t(`styles.pick.kind.${s.kind}` as MessageKey);
  }
}

/** The rows of the style list a step comes from (the ground picture: the zone's region, when it mixes regions). */
function stepTargets(s: StylePickStep, zone: string, ground: StylePickPart[]): FocusTarget[] {
  const road = s.class === 1 ? "highway" : "road";
  switch (s.kind) {
    case "background":
      return [{ key: "background" }];
    case "ground":
      return ground.some((g) => g.kind === "region") ? [{ key: "regions.zones", row: zone, column: "region" }] : [];
    case "groundLayer":
      return [{ key: `paint.ground.${s.paint}` }];
    case "shade":
      return [{ key: "shade.strength" }, { key: "shade.lights" }];
    case "water":
      return [{ key: "paint.water" }];
    case "sea":
      return [{ key: "sea.bands", row: String(s.band), column: s.bed ?? "sand" }];
    case "building": {
      const rule: FocusTarget =
        s.rule === "zone" ? { key: "regions.zones", row: zone, column: "building" } : s.rule === "region" ? { key: `buildings.byRegion.${s.region}` } : { key: "buildings.default" };
      return [{ key: `paint.buildings.${s.paint}` }, rule];
    }
    case "rail":
      return [{ key: "paint.rail" }];
    case "tunnel":
      return [{ key: "paint.roads.tunnel.fill" }, { key: "paint.roads.tunnel.color" }];
    case "track":
      return [{ key: "paint.roads.track.color" }];
    case "casing":
      return [{ key: `paint.roads.${road}.casing` }, { key: "paint.roads.casingWidth" }];
    case "road":
      return [{ key: `paint.roads.${road}.fill` }];
    case "postal":
      return [{ key: "labels.postal.color" }, { key: "labels.postal.size" }];
    case "zone":
      return [{ key: "labels.zone.color" }, { key: "labels.zone.outline" }];
    case "street":
      return [{ key: "labels.street.color" }];
    default:
      return [];
  }
}

/** A part of the ground picture in words. */
function partText(t: Translate, g: StylePickPart, names: RowNames): string {
  switch (g.kind) {
    case "ground":
      return t("styles.pick.part", { name: t(`viewer.point.ground.${g.id}` as MessageKey), share: Math.round(g.share * 100) });
    case "region":
      return t("styles.pick.part", { name: names.choice("regions.zones", "region", g.id), share: Math.round(g.share * 100) });
    case "tone":
      return t("styles.pick.part.weight", { name: names.row(`groundRaster.tones.${g.id}.color`), w: g.share.toFixed(2) });
    default:
      return t("styles.pick.part.weight", { name: names.row("groundRaster.treeDarkening"), w: g.share.toFixed(2) });
  }
}

/** The rows of the style list a part of the ground picture comes from. */
function partTargets(g: StylePickPart, values: StyleValues): FocusTarget[] {
  switch (g.kind) {
    case "ground": {
      const paint = valueAt(values, `groundPaints.${g.id}`);
      const colour = paint === "water" ? { key: "paint.water" } : { key: `paint.ground.${typeof paint === "string" ? paint : "ground"}` };
      return [{ key: `groundPaints.${g.id}` }, colour];
    }
    case "region":
      return [{ key: `regions.colors.${g.id}` }];
    case "tone":
      return [{ key: `groundRaster.tones.${g.id}.color` }, { key: `groundRaster.tones.${g.id}.strength` }];
    default:
      return [{ key: "groundRaster.treeDarkening" }];
  }
}

/** Buttons naming rows of the style list; a press shows the row. */
function Targets({ targets, names, onShow }: { targets: FocusTarget[]; names: RowNames; onShow(t: FocusTarget): void }) {
  const t = useT();
  if (targets.length === 0) return null;
  return (
    <span className="style-pick-targets">
      {targets.map((g, i) => {
        const cell = g.key === "sea.bands" ? t("styles.pick.band", { n: Number(g.row) + 1 }) : (g.row ?? "");
        const label = g.row !== undefined ? t("styles.pick.cell", { table: names.row(g.key), cell, column: names.column(g.key, g.column ?? "") }) : names.row(g.key);
        return (
          <Button key={i} help={{ text: t("help.styles.pick.target", { name: label }) }} variant="link" className="style-pick-target" onClick={() => onShow(g)}>
            {label}
          </Button>
        );
      })}
    </span>
  );
}
