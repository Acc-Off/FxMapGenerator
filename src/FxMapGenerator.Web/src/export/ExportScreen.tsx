import { useEffect, useRef, useState } from "react";
import { api, ApiError } from "../shared/api";
import { Button, Check, Info, Select, TextField, type Reason } from "../shared/controls";
import { gigabytes } from "../shared/format";
import { errorText, useI18nStore, useT, type MessageKey } from "../shared/i18n";
import { useAppStore } from "../shared/store";
import type { ExportChoices, ExportInfo, ExportProblem, TileSetInfo } from "../shared/types";
import { mapName, phaseText } from "../project/labels";
import { isActive, jobOfProject, useProjectStore } from "../project/store";
import { ConvertPicture } from "./ConvertPicture";

const PROBLEMS: Record<ExportProblem["code"], MessageKey> = {
  NOTHING: "export.problem.NOTHING",
  NO_TILES: "export.problem.NO_TILES",
  MINIMAP_NOT_READY: "export.problem.MINIMAP_NOT_READY",
  NO_MINIMAP: "export.problem.NO_MINIMAP",
  MINIMAP_NO_GTA: "export.problem.MINIMAP_NO_GTA",
  MINIMAP_NO_KEYS: "export.problem.MINIMAP_NO_KEYS",
  NOT_EMPTY: "export.problem.NOT_EMPTY",
  BAD_NAME: "export.problem.BAD_NAME",
  BAD_URL: "export.problem.BAD_URL",
  BAD_ZOOM: "export.problem.BAD_ZOOM",
  NO_FOLDER: "export.problem.NO_FOLDER",
  EDITABLE_NOT_READY: "export.problem.EDITABLE_NOT_READY",
  BAD_EDITABLE: "export.problem.BAD_EDITABLE",
  NO_EDITABLE_FORMAT: "export.problem.NO_EDITABLE_FORMAT",
  BAD_PICTURE: "export.problem.BAD_PICTURE",
  BAD_MAP: "export.problem.BAD_MAP",
};

/** The steps of an export (Core/Export/EditableStages.cs) and of a conversion (ConvertStages.cs), by the id of their stage. */
const STAGES: Record<string, MessageKey> = {
  export: "export.run.stage.export",
  "export.layers": "export.run.stage.layers",
  "export.files": "export.run.stage.files",
  "convert.tiles": "export.run.stage.convertTiles",
  "convert.textures": "export.run.stage.convertTextures",
  "convert.files": "export.run.stage.convertFiles",
};

const CHECK_DELAY_MS = 300;

/** The choices of the finest zoom (Core/Export/ExportOptions.cs), finest first. */
const MAX_ZOOMS = [8, 7, 6];

/** The zoom levels of the layered files (Core/Export/ExportOptions.cs, EditableChoice). */
const EDITABLE_ZOOMS = [6, 7];

/** The formats of the layered files, in the order they are kept (EditableChoice.AllFormats). */
const EDITABLE_FORMATS = ["psd", "svg"];

/** A map's tiles and bytes up to a zoom. */
function upTo(m: TileSetInfo, zoom: number): { tiles: number; bytes: number } {
  const sum = (a: number[]) => a.slice(0, zoom + 1).reduce((s, v) => s + v, 0);
  return { tiles: sum(m.tilesPerZoom), bytes: sum(m.bytesPerZoom) };
}

/** A pixel of a zoom level on the ground: 27 cm at z8, 55 cm at z7, 1.1 m at z6. */
function pixelSize(zoom: number): string {
  const m = (70.3125 * 2 ** (8 - zoom)) / 256;
  return m < 1 ? `${Math.round(m * 100)} cm` : `${m.toFixed(1)} m`;
}

function when(utc: string): string {
  return new Date(utc).toLocaleString();
}

/**
 * The export screen: where to, the web tiles of which maps (a folder or a zip, with the viewer and the
 * lb-phone example; the address they will be served from), the minimap resource and its name, the layered files for
 * editing (chosen anew for every export); what stops the export, the export as a run with its progress, and what the
 * chosen folder holds from earlier exports. Under the export's button, the conversion of an edited picture
 * (ConvertPicture), a run of its own shown with the export's.
 */
export function ExportScreen() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const job = useProjectStore((s) => s.job);
  const log = useProjectStore((s) => s.log);
  const { stop, setScreen } = useProjectStore.getState();
  const setSettingsOpen = useAppStore((s) => s.setSettingsOpen);
  const [info, setInfo] = useState<ExportInfo | null>(null);
  const [choices, setChoices] = useState<ExportChoices | null>(null);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef(0);

  const mine = jobOfProject(job, project);
  const converting = !!job?.stages.some((s) => s.id.startsWith("convert."));
  const exportJob = mine && (converting || !!job?.stages.some((s) => s.id === "export")) ? job : null;
  const running = isActive(job);

  // the saved choices, then again whenever an export of this project ends (a new record in the folder)
  const exportEnded = exportJob && !converting && !isActive(exportJob) ? exportJob.runId : null;
  useEffect(() => {
    void api.exportInfo().then((i) => {
      setInfo(i);
      setChoices(i.options);
    }, (e) => setError(message(e)));
  }, [project.path, exportEnded]);

  // a conversion that ends leaves the choices on the screen as they are: only what the folder holds is read again
  const convertEnded = exportJob && converting && !isActive(exportJob) ? exportJob.runId : null;
  useEffect(() => {
    if (convertEnded && choices) void api.checkExport(choices).then(setInfo, (e) => setError(message(e)));
    // only when a conversion ends
  }, [convertEnded]);

  const change = (patch: Partial<ExportChoices>) => {
    const next = { ...choices!, ...patch };
    setChoices(next);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => void api.checkExport(next).then(setInfo, (e) => setError(message(e))), CHECK_DELAY_MS);
  };

  const start = async () => {
    setError(null);
    try {
      useProjectStore.getState().onJob(await api.startExport({ ...choices!, language: useI18nStore.getState().lang }));
    } catch (e) {
      setError(message(e));
    }
  };

  const pick = async () => {
    const r = await api.pickFolder(choices?.folder ?? undefined, t("export.folder"));
    if (r.path) change({ folder: r.path });
  };

  if (!info || !choices) return <div className="screen">{error ? <div className="banner banner-error">{error}</div> : <p className="muted">{t("common.loading")}</p>}</div>;

  const maps = choices.maps ?? [];
  const toggleMap = (id: string, on: boolean) => change({ maps: on ? [...maps.filter((m) => m !== id), id] : maps.filter((m) => m !== id) });
  const startReason: Reason | null = running ? (mine ? "reason.running" : "reason.jobElsewhere") : info.problems.length > 0 ? "reason.exportProblems" : null;
  const maxZoom = choices.maxZoom ?? 8;
  const layered = choices.editableMaps ?? [];
  const toggleLayered = (id: string, on: boolean) => change({ editableMaps: on ? [...layered.filter((m) => m !== id), id] : layered.filter((m) => m !== id) });
  const layeredFormats = choices.editableFormats ?? [];
  const toggleFormat = (id: string, on: boolean) => change({ editableFormats: EDITABLE_FORMATS.filter((f) => (f === id ? on : layeredFormats.includes(f))) });
  const layeredZooms = EDITABLE_ZOOMS.map((z) => ({
    value: String(z),
    label: t("export.editable.zoom.option", {
      z,
      size: pixelSize(z),
      width: (info.editable.blocksX * 256 * 2 ** (z - 6)).toLocaleString(),
      height: (info.editable.blocksY * 256 * 2 ** (z - 6)).toLocaleString(),
    }),
  }));
  const tiles = info.maps.filter((m) => maps.includes(m.map));
  const bytes = tiles.reduce((s, m) => s + upTo(m, maxZoom).bytes, 0) + (choices.minimap ? info.minimap.bytes : 0);
  const formats = [
    { value: "folder", label: t("export.format.folder") },
    { value: "zip", label: t("export.format.zip") },
  ];
  const zooms = MAX_ZOOMS.map((z) => ({
    value: String(z),
    label: t("export.maxZoom.option", { z, size: pixelSize(z), tiles: tiles.reduce((s, m) => s + upTo(m, z).tiles, 0).toLocaleString() }),
  }));

  return (
    <div className="screen export">
      <div className="screen-head">
        <Info help="help.export.intro" className="screen-intro">
          {t("export.intro")}
        </Info>
      </div>
      {error && <div className="banner banner-error">{error}</div>}
      <div className="export-body">
        <section className="export-form">
          <div className="export-group" data-guide="export.where">
            <Info help="help.export.where" block className="section-title">
              {t("export.where")}
            </Info>
            <div className="field-row">
              <TextField help="help.export.folder" label={t("export.folder")} value={choices.folder ?? ""} wide onChange={(v) => change({ folder: v })} />
              <Button help="help.export.browse" onClick={() => void pick()}>
                {t("common.browse")}
              </Button>
              <Button help="help.export.open" disabledReason={info.last ? null : "reason.noExportYet"} onClick={() => void api.showFolder(choices.folder!)}>
                {t("export.open")}
              </Button>
            </div>
          </div>

          <div className="export-group" data-guide="export.web">
            <Info help="help.export.web" block className="section-title">
              {t("export.web")}
            </Info>
            {info.maps.length === 0 && (
              <Info help="help.export.web.none" block className="muted">
                {t("export.web.none")}
              </Info>
            )}
            {info.maps.map((m) => {
              const part = upTo(m, maxZoom);
              const shown = m.zooms.filter((z) => z <= maxZoom);
              return (
              <div key={m.map} className="export-map">
                <Check help="help.export.map" checked={maps.includes(m.map)} onChange={(on) => toggleMap(m.map, on)}>
                  {t("export.map", {
                    map: mapName(t, m.map),
                    tiles: part.tiles.toLocaleString(),
                    size: gigabytes(part.bytes),
                    zooms: shown.length > 0 ? t("export.zooms", { from: shown[0], to: shown[shown.length - 1] }) : "—",
                  })}
                </Check>
                <Info help={m.upToDate ? "help.export.map.upToDate" : "help.export.map.behind"} className={`tag${m.upToDate ? "" : " tag-warn"}`}>
                  {m.upToDate ? t("export.map.upToDate") : t("export.map.behind")}
                </Info>
              </div>
              );
            })}
            <div className="field-row">
              <Select help="help.export.maxZoom" label={t("export.maxZoom")} value={String(maxZoom)} options={zooms}
                disabledReason={maps.length === 0 ? "reason.noWebTiles" : null} onChange={(v) => change({ maxZoom: Number(v) })} />
            </div>
            <div className="field-row">
              <Select help="help.export.format" label={t("export.format")} value={choices.zip ? "zip" : "folder"} options={formats}
                disabledReason={maps.length === 0 ? "reason.noWebTiles" : null} onChange={(v) => change({ zip: v === "zip" })} />
            </div>
            <TextField help="help.export.baseUrl" label={t("export.baseUrl")} value={choices.baseUrl ?? ""} placeholder="https://example.pages.dev" wide onChange={(v) => change({ baseUrl: v })} />
          </div>

          <div className="export-group" data-guide="export.minimap">
            <Info help="help.export.minimap" block className="section-title">
              {t("export.minimap")}
            </Info>
            <Check
              help="help.export.minimap.on"
              checked={!!choices.minimap}
              disabledReason={info.minimap.map ? null : "reason.noMinimap"}
              onChange={(on) => change({ minimap: on })}
            >
              {!info.minimap.map
                ? t("export.minimap.none")
                : info.minimap.extra > 0
                  ? t("export.minimap.whatExtra", {
                      map: mapName(t, info.minimap.map),
                      size: info.minimap.size,
                      made: info.minimap.made,
                      total: info.minimap.total,
                      extra: info.minimap.extra,
                    })
                  : t("export.minimap.what", { map: mapName(t, info.minimap.map), size: info.minimap.size, made: info.minimap.made })}
            </Check>
            {info.minimap.map && info.minimap.islandMap && (
              <Info help="help.export.minimap.island" block className="muted">
                {t("export.minimap.island")}
              </Info>
            )}
            {info.minimap.map && info.minimap.islandMap && info.minimap.islandLandMissing > 0 && (
              <Info help="help.export.minimap.islandLand" block className="text-warn">
                {t("export.minimap.islandLand", { n: info.minimap.islandLandMissing })}
              </Info>
            )}
            <TextField help="help.export.resourceName" label={t("export.resourceName")} value={choices.resourceName ?? ""} wide onChange={(v) => change({ resourceName: v })} />
          </div>

          <div className="export-group" data-guide="export.editable">
            <Info help="help.export.editable" block className="section-title">
              {t("export.editable")}
            </Info>
            {info.editable.maps.length === 0 && (
              <Info help="help.export.editable.none" block className="muted">
                {t("export.editable.none")}
              </Info>
            )}
            {info.editable.maps.map((m) => (
              <div key={m.map} className="export-map">
                <Check
                  help="help.export.editable.map"
                  checked={layered.includes(m.map)}
                  disabledReason={m.ready ? null : "reason.editableNotReady"}
                  onChange={(on) => toggleLayered(m.map, on)}
                >
                  {mapName(t, m.map)}
                </Check>
                {m.ready && (
                  <Info help={m.upToDate ? "help.export.map.upToDate" : "help.export.editable.behind"} className={`tag${m.upToDate ? "" : " tag-warn"}`}>
                    {m.upToDate ? t("export.map.upToDate") : t("export.map.behind")}
                  </Info>
                )}
              </div>
            ))}
            <div className="field-row">
              <Select help="help.export.editable.zoom" label={t("export.editable.zoom")} value={String(choices.editableZoom ?? 6)} options={layeredZooms}
                disabledReason={layered.length === 0 ? "reason.noEditableMaps" : null} onChange={(v) => change({ editableZoom: Number(v) })} />
            </div>
            <Info help="help.export.editable.format" block className="field-label">
              {t("export.editable.format")}
            </Info>
            <div className="export-outputs">
              <Check help="help.export.editable.format.psd" checked={layeredFormats.includes("psd")}
                disabledReason={layered.length === 0 ? "reason.noEditableMaps" : null} onChange={(on) => toggleFormat("psd", on)}>
                {t("export.editable.format.psd")}
              </Check>
              <Check help="help.export.editable.format.svg" checked={layeredFormats.includes("svg")}
                disabledReason={layered.length === 0 ? "reason.noEditableMaps" : null} onChange={(on) => toggleFormat("svg", on)}>
                {t("export.editable.format.svg")}
              </Check>
            </div>
          </div>

          {info.problems.length > 0 && (
            <ul className="export-problems">
              {info.problems.map((p) => (
                <li key={p.code + p.message}>
                  <Info help="help.export.problem" className="text-error">
                    {PROBLEMS[p.code] ? t(PROBLEMS[p.code]) : p.message}
                  </Info>
                  {(p.code === "NO_TILES" || p.code === "MINIMAP_NOT_READY" || p.code === "EDITABLE_NOT_READY") && (
                    <Button help="help.export.toProject" variant="link" onClick={() => setScreen("project")}>
                      {t("export.toProject")}
                    </Button>
                  )}
                  {(p.code === "MINIMAP_NO_GTA" || p.code === "MINIMAP_NO_KEYS") && (
                    <Button help="help.appSettings.open" variant="link" onClick={() => setSettingsOpen(true)}>
                      {t("appSettings.open")}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
          <div className="export-go" data-guide="export.go">
            <Info help="help.export.size" className="muted">
              {t("export.size", { size: gigabytes(bytes) })}
            </Info>
            <span className="spacer" />
            <Button help="help.export.start" variant="primary" disabledReason={startReason} onClick={() => void start()}>
              {t("export.start")}
            </Button>
          </div>

          <ConvertPicture
            key={project.path}
            info={info}
            folder={choices.folder}
            baseUrl={choices.baseUrl}
            busy={running ? (mine ? "reason.running" : "reason.jobElsewhere") : null}
            problemText={(p) => (PROBLEMS[p.code] ? t(PROBLEMS[p.code]) : p.message)}
            onError={setError}
          />
        </section>

        <section className="export-side">
          {exportJob && (
            <div className="export-run" data-guide="export.run">
              <Info help="help.export.run" block className="section-title">
                {t("export.run")}
              </Info>
              {exportJob.stages.filter((s) => s.total > 0).map((s) => (
                <Info key={s.id} help="help.export.run.stage" block className="run-progress">
                  <span className="bar">
                    <span style={{ width: `${(100 * s.done) / s.total}%` }} />
                  </span>
                  {t(STAGES[s.id] ?? "export.run.stage.export", { done: s.done, total: s.total })}
                </Info>
              ))}
              <Info help="help.export.run.state" block>
                {isActive(exportJob) ? t("status.state.running")
                  : exportJob.state === "done" ? t(converting ? "export.run.convertDone" : "export.run.done")
                    : exportJob.state === "failed" ? t("status.state.failedWith", { error: exportJob.error ?? "" }) : t("status.state.stopped")}
              </Info>
              {isActive(exportJob) && (
                <>
                  {exportJob.active.map((a) => (
                    <Info key={a.unit} help="help.export.run.part" block className="muted">
                      {a.unit} {phaseText(t, a.phase)} {Math.round(a.fraction * 100)} %
                    </Info>
                  ))}
                  <div className="export-go">
                    <Button help="help.run.stopBoundary" disabledReason={exportJob.stopMode !== "none" ? "reason.stopping" : null} onClick={() => void stop("boundary")}>
                      {t("run.stopBoundary")}
                    </Button>
                    <Button help="help.run.stopNow" variant="danger" disabledReason={exportJob.stopMode === "now" ? "reason.stopping" : null} onClick={() => void stop("now")}>
                      {t("run.stopNow")}
                    </Button>
                  </div>
                </>
              )}
              <div className="run-log export-log">
                {log.slice(-12).map((l, i) => (
                  <div key={i}>{l.line}</div>
                ))}
              </div>
            </div>
          )}

          <div className="export-group" data-guide="export.record">
            <Info help="help.export.record" block className="section-title">
              {t("export.record")}
            </Info>
            {info.last ? (
              <div className="export-record">
                <Info help="help.export.record.web" block>
                  {info.last.web
                    ? t("export.record.web", {
                        at: when(info.last.web.atUtc),
                        maps: info.last.web.maps.map((m) => mapName(t, m)).join(t("list.comma")),
                        zooms: t("export.zooms", { from: 0, to: info.last.web.maxZoom }),
                        format: info.last.web.zip ? "web.zip" : "web/",
                        tiles: info.last.web.tiles.toLocaleString(),
                      }) + (info.last.web.complete ? "" : ` ${t("export.record.partial")}`)
                    : t("export.record.noWeb")}
                </Info>
                {info.last.resources.length === 0 ? (
                  <Info help="help.export.record.resource" block className="muted">
                    {t("export.record.noResource")}
                  </Info>
                ) : (
                  info.last.resources.map((r) => (
                    <Info key={r.name} help="help.export.record.resource" block>
                      {t("export.record.resource", {
                        name: r.name,
                        map: r.fromPicture ? t("export.record.fromPicture", { map: mapName(t, r.map) }) : mapName(t, r.map),
                        size: r.size,
                        at: when(r.atUtc),
                      })}
                    </Info>
                  ))
                )}
                {(info.last.webEdited ?? []).map((e) => (
                  <Info key={e.name} help="help.export.record.webEdited" block>
                    {t("export.record.webEdited", { name: e.name, zoom: e.zoom, tiles: e.tiles.toLocaleString(), at: when(e.atUtc) }) +
                      (e.complete ? "" : ` ${t("export.record.partial")}`)}
                  </Info>
                ))}
                {(info.last.editable ?? []).map((e) => (
                  <Info key={e.file} help="help.export.record.editable" block>
                    {t("export.record.editable", {
                      file: e.file,
                      map: mapName(t, e.map),
                      width: e.width.toLocaleString(),
                      height: e.height.toLocaleString(),
                      size: gigabytes(e.bytes),
                      at: when(e.atUtc),
                    })}
                  </Info>
                ))}
              </div>
            ) : (
              <p className="muted">{t("export.record.none")}</p>
            )}
          </div>
        </section>
      </div>
    </div>
  );
}

function message(e: unknown): string {
  return e instanceof ApiError ? errorText(e.code, e.message) : e instanceof Error ? e.message : String(e);
}
