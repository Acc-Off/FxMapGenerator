import type { ApiErrorBody, AppSettings, BeforeDto, GameFilesStatus, GameUse, PointDto, BlocksDto, CaptureResourceDto, CheckDto, ConvertChoices, ConvertInfo, DiagnosticsDto, ExportChoices, ExportInfo, JobLogLine, JobSnapshot, PlanDto, PrecheckReport, ProjectDto, ProjectEdit, RecentProjectDto, RenderChange, RenderStatus, ResourceStartDto, RetakeRequest, RoadEditorDto, RoadEditsFile, RoadGroundDto, RoadLinkDto, RoadNodeDto, RoadPathsDto, ServerPreset, StageUnits, StatusDto, StyleDto, StyleListDto, StyleListItem, StyleSchema, StyleValues, SampleStatus, StylePreviewDrawing, StylePick, PreviewSource, ProjectPreviewStatus, PoiEditorDto, PoiEditSet, PoiEditStyle, PoiIcons, PoiImportDto, PoiLabel } from "./types";

/** An HTTP error carrying the server's `{ error: { code, message } }` envelope when there was one. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { cache: "no-store", ...init });
  if (!response.ok) throw await toApiError(response);
  return (await response.json()) as T;
}

/** Like request, but 204 No Content gives null. */
async function maybe<T>(url: string): Promise<T | null> {
  const response = await fetch(url, { cache: "no-store" });
  if (response.status === 204) return null;
  if (!response.ok) throw await toApiError(response);
  return (await response.json()) as T;
}

async function toApiError(response: Response): Promise<ApiError> {
  let body: { error?: ApiErrorBody } | null = null;
  try {
    body = (await response.json()) as { error?: ApiErrorBody };
  } catch {
    // not JSON
  }
  return new ApiError(response.status, body?.error?.code ?? `HTTP_${response.status}`, body?.error?.message ?? `HTTP ${response.status}`);
}

function json(method: string, body: unknown): RequestInit {
  return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
}

export interface NewProjectRequest {
  path: string;
  name: string;
  workFolder: string;
  preset: ServerPreset;
  satellite: boolean;
  atlas: boolean;
  roadmap: boolean;
  /** The screen's language: the names the bundled road edits' groups take in the project. */
  language: string;
}

export const api = {
  status: () => request<StatusDto>("/api/status"),
  settings: () => request<AppSettings>("/api/settings"),
  saveSettings: (settings: AppSettings) => request<AppSettings>("/api/settings", json("PUT", settings)),
  gameFilesStatus: (gta: string, keys: string) =>
    request<GameFilesStatus>(`/api/settings/gamefiles?gta=${encodeURIComponent(gta)}&keys=${encodeURIComponent(keys)}`),
  diagnostics: () => request<DiagnosticsDto>("/api/diagnostics"),
  notices: async () => {
    const response = await fetch("/api/notices", { cache: "no-store" });
    if (!response.ok) throw await toApiError(response);
    return response.text();
  },
  quit: () => request<{ ok: boolean }>("/api/quit", { method: "POST" }),
  pickFolder: (initial?: string, title?: string) => request<{ path: string | null }>("/api/dialog/folder", json("POST", { initial, title })),
  /** `kind`: the files the dialog lists: a project file (the default), "roadData" = a server's road data (zip, ynd), "picture" = a PNG picture. */
  pickFile: (save: boolean, initial?: string, title?: string, kind?: "roadData" | "picture") =>
    request<{ path: string | null }>("/api/dialog/file", json("POST", { initial, title, save, kind })),

  recentProjects: () => request<RecentProjectDto[]>("/api/projects/recent"),
  forgetProject: (path: string) => request<RecentProjectDto[]>("/api/projects/recent/remove", json("POST", { path })),
  project: () => maybe<ProjectDto>("/api/project"),
  openProject: (path: string) => request<ProjectDto>("/api/project/open", json("POST", { path })),
  newProject: (r: NewProjectRequest) => request<ProjectDto>("/api/project/new", json("POST", r)),
  closeProject: () => request<{ ok: boolean }>("/api/project/close", { method: "POST" }),
  editProject: (edit: ProjectEdit) => request<ProjectDto>("/api/project", json("PATCH", edit)),
  blocks: () => request<BlocksDto>("/api/project/blocks"),
  /** What changed of each map since its tiles were kept (the last export). */
  before: () => request<BeforeDto>("/api/project/before"),
  /** Marks blocks of the range for retake (or takes the mark off); the project as it is then. */
  retake: (r: RetakeRequest) => request<ProjectDto>("/api/project/retake", json("POST", r)),
  plan: (workers: number) => request<PlanDto>(`/api/project/plan?workers=${workers}`),
  checks: () => request<CheckDto[]>("/api/project/checks"),
  /** Fetches (or reads) the postal codes into the work folder now; the postal code check as it is then. */
  fetchPostals: () => request<CheckDto>("/api/project/postals/fetch", { method: "POST" }),
  point: (x: number, y: number) => request<PointDto>(`/api/project/point?x=${x}&y=${y}`),

  job: () => maybe<JobSnapshot>("/api/jobs/current"),
  jobUnits: () => maybe<StageUnits[]>("/api/jobs/current/units"),
  startJob: (project: string, workers: number) => request<JobSnapshot>("/api/jobs", json("POST", { project, workers })),
  stopJob: (mode: "boundary" | "now") => request<JobSnapshot>("/api/jobs/current/stop", json("POST", { mode })),
  setJobWorkers: (workers: number) => request<JobSnapshot>("/api/jobs/current/workers", json("PUT", { workers })),

  precheck: () => maybe<PrecheckReport>("/api/game/precheck"),
  runPrecheck: () => request<PrecheckReport>("/api/game/precheck", { method: "POST" }),
  /** A file of the latest pre-check (shot.png, shot-marked.png); `at` makes a new report load a new picture. */
  precheckFile: (name: string, at: string) => `/api/game/precheck/files/${name}?at=${encodeURIComponent(at)}`,
  render: () => request<RenderStatus>("/api/game/render"),
  applyRender: () => request<RenderChange>("/api/game/render/apply", { method: "POST" }),
  restoreRender: (backup?: string) => request<RenderChange>("/api/game/render/restore", json("POST", { backup })),
  /** Whether FiveM runs on this PC. */
  fiveM: () => request<{ running: boolean }>("/api/game/fivem"),
  /** Starts the capture resource from the game console when it does not answer (refresh, ensure). */
  startResource: () => request<ResourceStartDto>("/api/game/resource/start", { method: "POST" }),

  captureResource: () => request<CaptureResourceDto>("/api/capture-resource"),
  /** What a folder holds under the resource's name: null, or the version found there. */
  captureFound: (folder: string) => request<{ found: string | null }>(`/api/capture-resource/found?folder=${encodeURIComponent(folder)}`),
  saveCapture: (folder: string, replace: boolean) => request<CaptureResourceDto>("/api/capture-resource/save", json("POST", { folder, replace })),
  /** The zip of the resource folder, as a download. */
  captureZipUrl: "/api/capture-resource/zip",
  capturePlaced: (placed: boolean) => request<CaptureResourceDto>("/api/project/capture-placed", json("POST", { placed })),

  /** The road editor: whether it can be used, the saved edits and those not applied. */
  roadEditor: () => request<RoadEditorDto>("/api/project/road-editor"),
  roadPaths: () => request<RoadPathsDto>("/api/project/road-editor/paths"),
  roadNode: (key: string) => request<RoadNodeDto>(`/api/project/road-editor/node?key=${encodeURIComponent(key)}`),
  roadLink: (from: string, to: string) => request<RoadLinkDto>(`/api/project/road-editor/link?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`),
  roadGround: (x: number, y: number) => request<RoadGroundDto>(`/api/project/road-editor/ground?x=${x}&y=${y}`),
  /** The bundled road edits (the file's form): the groups the road editor can take in. */
  roadBundled: () => request<RoadEditsFile>("/api/project/road-editor/bundled"),
  saveRoadEdits: (edits: RoadEditsFile) => request<RoadEditorDto>("/api/project/road-editor/edits", json("PUT", edits)),
  /** The road shapes the next run would make with these edits (the right map's tiles take the id as their version). */
  /** Reads the game files now; `changed`: the files differ from those there were (`pathsChanged`: the road data does). */
  roadGameFiles: () =>
    request<{ changed: boolean; pathsChanged: boolean; areas: number; nodes: number; links: number; seconds: number }>("/api/project/road-editor/game-files", json("POST", {})),
  roadPreview: (edits: RoadEditsFile) => request<{ id: string; seconds: number; provisional: boolean }>("/api/project/road-editor/preview", json("POST", edits)),

  /** The style editor's table of a style's values. */
  styleSchema: () => request<StyleSchema>("/api/styles/schema"),
  /** The shared styles (the settings folder's), which new styles of any project can be made from. */
  sharedStyles: () => request<StyleListItem[]>("/api/styles/shared"),
  /** The bundled atlas styles and the project's own. */
  styles: () => request<StyleListDto>("/api/project/styles"),
  style: (id: string) => request<StyleDto>(`/api/project/styles/${encodeURIComponent(id)}`),
  /** Saves a project style's values (the whole style as edited; the file keeps what differs from its base). */
  saveStyle: (id: string, values: StyleValues) => request<StyleDto>(`/api/project/styles/${encodeURIComponent(id)}`, json("PUT", values)),
  /** The font families of this PC. */
  fonts: () => request<string[]>("/api/fonts"),
  /** The style editor's sample land: whether it is made. */
  sampleStatus: () => request<SampleStatus>("/api/styles/sample"),
  /** Starts making the sample land (unless it is made or being made). */
  makeSample: () => request<SampleStatus>("/api/styles/sample", { method: "POST" }),
  /** The preview on the open project's data: its state, the blocks it can draw, its places. */
  projectPreview: () => request<ProjectPreviewStatus>("/api/project/styles/preview"),
  /** The places the project adds to the recommended ones (the whole list). */
  savePreviewPlaces: (places: { name: string; x: number; y: number }[]) =>
    request<ProjectPreviewStatus>("/api/project/styles/preview/places", json("PUT", places)),
  /** A window of the sample land or the project drawn with the values: the id its z8 tiles are read by, its edges and the seconds it took. */
  stylePreview: (source: PreviewSource, values: StyleValues, x: number, y: number, size: number, language: string, side: string, signal?: AbortSignal) =>
    request<StylePreviewDrawing>("/api/project/styles/preview", { ...json("POST", { values, x, y, size, language, side, source }), signal }),
  /** Where the colour of a point of a window drawn with the values comes from. */
  stylePick: (source: PreviewSource, values: StyleValues, x: number, y: number, cx: number, cy: number, size: number, language: string, signal?: AbortSignal) =>
    request<StylePick>("/api/project/styles/preview/pick", { ...json("POST", { values, x, y, cx, cy, size, language, source }), signal }),
  /** A new project style made from another (bundled, the project's, or shared), with its id and name. */
  createStyle: (from: string, shared: boolean, id: string, name: string) => request<StyleDto>("/api/project/styles", json("POST", { from, shared, id, name })),
  renameStyle: (id: string, name: string) => request<StyleDto>(`/api/project/styles/${encodeURIComponent(id)}/name`, json("PUT", { name })),
  deleteStyle: (id: string) => request<StyleListDto>(`/api/project/styles/${encodeURIComponent(id)}`, { method: "DELETE" }),
  /** A style file taken into the project (under another id when given; a whole style's name in English and Japanese taken in the screen's language). */
  importStyle: (file: string, language: string, id?: string) =>
    request<StyleDto>(`/api/project/styles/import?language=${language}${id ? `&id=${encodeURIComponent(id)}` : ""}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: file }),
  /** Copies a project style into the shared styles (EXISTS unless `overwrite`). */
  shareStyle: (id: string, overwrite: boolean) => request<StyleListItem[]>(`/api/project/styles/${encodeURIComponent(id)}/shared${overwrite ? "?overwrite=true" : ""}`, { method: "POST" }),
  /** The style's file, as a download. */
  styleFileUrl: (id: string) => `/api/project/styles/${encodeURIComponent(id)}/file`,

  /** The POI screen: the project's points and POI styles (or why they cannot be read). */
  poi: () => request<PoiEditorDto>("/api/project/poi"),
  /** Saves the points and the project's POI styles (the whole set as edited); a first save names the bundled groups in `language`, the screen's. */
  savePoi: (set: PoiEditSet, language: string) => request<PoiEditorDto>(`/api/project/poi?language=${language}`, json("PUT", set)),
  /** The MDI icons the POI styles can draw. */
  poiIcons: () => request<PoiIcons>("/api/poi/icons"),
  /** A POI style drawn as the maps draw it (zoom 8, 7 or 6), as a PNG. */
  poiSample: async (style: PoiEditStyle, label: PoiLabel, language: string, zoom: number, signal?: AbortSignal): Promise<Blob> => {
    const response = await fetch("/api/project/poi/sample", { ...json("POST", { style, label, language, zoom }), cache: "no-store", signal });
    if (!response.ok) throw await toApiError(response);
    return await response.blob();
  },
  /** A PNG the POI styles name, for the screen. */
  poiImageUrl: (path: string) => `/api/project/poi/images?path=${encodeURIComponent(path)}`,
  /** Keeps a chosen PNG beside the POI styles: the path a style names it by. */
  poiImage: async (file: File) =>
    request<{ image: string }>(`/api/project/poi/images?name=${encodeURIComponent(file.name)}`, { method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: await file.arrayBuffer() }),
  /** Points read from a chosen CSV or JSON file. */
  poiImport: (name: string, text: string) => request<PoiImportDto>("/api/project/poi/import", json("POST", { name, text })),

  exportInfo: () => request<ExportInfo>("/api/project/export"),
  checkExport: (choices: ExportChoices) => request<ExportInfo>("/api/project/export/check", json("POST", choices)),
  startExport: (choices: ExportChoices) => request<JobSnapshot>("/api/project/export", json("POST", choices)),
  showFolder: (path: string) => request<{ ok: boolean }>("/api/project/export/show", json("POST", { path })),
  checkConvert: (choices: ConvertChoices) => request<ConvertInfo>("/api/project/convert/check", json("POST", choices)),
  startConvert: (choices: ConvertChoices) => request<JobSnapshot>("/api/project/convert", json("POST", choices)),
};

export interface EventHandlers {
  status?: (status: StatusDto) => void;
  settings?: (settings: AppSettings) => void;
  project?: (project: ProjectDto | null) => void;
  job?: (job: JobSnapshot) => void;
  jobLog?: (line: JobLogLine) => void;
  precheck?: (report: PrecheckReport) => void;
  /** What uses the game by hand changed (also sent when the stream connects). */
  gameUse?: (use: { kind: GameUse }) => void;
  /** true when the stream (re)connects, false once it has failed twice in a row. */
  connection?: (connected: boolean) => void;
}

/** Subscribes to the server's event stream; returns the unsubscribe function. */
export function subscribeEvents(handlers: EventHandlers): () => void {
  const source = new EventSource("/api/events");
  let connected = false;
  let failures = 0;
  source.addEventListener("open", () => {
    failures = 0;
    if (!connected) {
      connected = true;
      handlers.connection?.(true);
    }
  });
  source.addEventListener("error", () => {
    failures++;
    if (connected && failures >= 2) {
      connected = false;
      handlers.connection?.(false);
    }
  });
  const on = <T,>(name: string, handler?: (value: T) => void) => {
    if (handler) source.addEventListener(name, (e) => handler(JSON.parse((e as MessageEvent).data) as T));
  };
  on("status", handlers.status);
  on("settings", handlers.settings);
  on("project", handlers.project);
  on("job", handlers.job);
  on("jobLog", handlers.jobLog);
  on("precheck", handlers.precheck);
  on("gameUse", handlers.gameUse);
  return () => source.close();
}
