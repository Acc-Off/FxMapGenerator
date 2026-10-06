import { useEffect, useState, type ReactNode } from "react";
import { api, ApiError } from "../shared/api";
import { sourceName } from "../shared/AppSettingsDialog";
import { Button, Check, Info, Select, TextField, type HelpKey, type Reason } from "../shared/controls";
import { errorText, useT } from "../shared/i18n";
import { useAppStore } from "../shared/store";
import type { CheckDto, ServerPreset, Sides } from "../shared/types";
import { INPUTS, mapName } from "../project/labels";
import { useLock } from "../project/locks";
import { useProjectStore } from "../project/store";
import { PostalsDialog } from "./PostalsDialog";

/** ✓ / ✗ / — in front of a line, with the line's hover help. */
function Mark({ ok, help, children }: { ok: boolean | null; help: HelpKey; children: ReactNode }) {
  const mark = ok === true ? "ok" : ok === false ? "ng" : "unknown";
  return (
    <Info help={help} block className={`check-row check-${mark}`}>
      <span className="check-mark">{mark === "ok" ? "✓" : mark === "ng" ? "✗" : "—"}</span>
      {children}
    </Info>
  );
}

type Side = keyof Sides;

/** The sides in the order shown, with their words, their help and the side facing them (they share a limit). */
const SIDES = [
  { side: "top", label: "settings.frame.top", help: "help.settings.frame.top", facing: "bottom" },
  { side: "bottom", label: "settings.frame.bottom", help: "help.settings.frame.bottom", facing: "top" },
  { side: "left", label: "settings.frame.left", help: "help.settings.frame.left", facing: "right" },
  { side: "right", label: "settings.frame.right", help: "help.settings.frame.right", facing: "left" },
] as const;

/** The most cells added above and below together, and left and right together (WorldGrid.MaxCellsVertical / Horizontal). */
const SIDE_LIMIT: Record<Side, number> = { top: 2, bottom: 2, left: 4, right: 4 };
const NO_CELLS: Sides = { top: 0, bottom: 0, left: 0, right: 0 };

/**
 * The areas outside the standard map: the cells added on each side, saved as chosen. A number past the limits, or one
 * that would leave blocks of the range outside the map, cannot be chosen (the second says why, and the box cannot be
 * turned off while the range has blocks outside the standard map). Then the map's size in blocks, and whether the game
 * files read Cayo Perico's roads.
 */
function FrameSection() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const edit = useProjectStore((s) => s.edit);
  const frameLock = useLock(INPUTS.frame);
  const cayoLock = useLock(INPUTS.cayoPerico);
  const cells = project.file.range.extraCells;
  const needed = project.frameNeeded;
  const held = cells ? SIDES.filter((s) => needed[s.side] > 0) : [];
  const offReason: Reason | null =
    frameLock ?? (cells && project.rangeOutside > 0 ? { key: "reason.settings.frameOff", params: { n: project.rangeOutside } } : null);
  const setSide = (side: Side, n: number) => void edit({ extraCells: { on: true, ...(cells ?? NO_CELLS), [side]: n } });

  return (
    <section className="settings-section" data-guide="settings.frame">
      <Info help="help.settings.frame" block className="section-title">
        {t("settings.frame")}
      </Info>
      <Check help="help.settings.frame.on" checked={cells !== null} disabledReason={offReason} onChange={(on) => void edit({ extraCells: { on } })}>
        {t("settings.frame.on")}
      </Check>
      {cells && (
        <div className="settings-frame">
          <Info help="help.settings.frame.cells" block className="field-label settings-sub">
            {t("settings.frame.cells")}
          </Info>
          <div className="field-row">
            {SIDES.map((s) => (
              <Select
                key={s.side}
                help={s.help}
                label={t(s.label)}
                value={String(cells[s.side])}
                options={Array.from({ length: SIDE_LIMIT[s.side] + 1 }, (_, n) => ({
                  value: String(n),
                  label: String(n),
                  disabled: n + cells[s.facing] > SIDE_LIMIT[s.side] || n < needed[s.side],
                }))}
                disabledReason={frameLock}
                onChange={(v) => setSide(s.side, Number(v))}
              />
            ))}
          </div>
          <Info help="help.settings.frame.limits" block className="muted">
            {t("settings.frame.limits")}
          </Info>
          {held.length > 0 && (
            <Info help="help.settings.frame.needed" block className="note">
              {t("settings.frame.needed", {
                sides: held
                  .map((s, i) => t(i === 0 ? "settings.frame.neededFirst" : "settings.frame.neededNext", { side: t(s.label), n: needed[s.side] }))
                  .join(t("settings.frame.neededJoin")),
              })}
            </Info>
          )}
          <Info help="help.settings.frame.size" block>
            {t("settings.frame.size", { cols: project.frame.cols, rows: project.frame.rows })}
          </Info>
        </div>
      )}
      <Check help="help.settings.cayoPerico" checked={project.file.cayoPerico} disabledReason={cayoLock} onChange={(v) => void edit({ cayoPerico: v })}>
        {t("settings.cayoPerico")}
      </Check>
    </section>
  );
}

/**
 * The project's settings (a tab of its own): the name and folders, the areas outside the standard map (the cells added
 * around it, Cayo Perico's roads), the server (preset, the resources stopped during the visit, the game's console, the
 * server's own resources for the road data), where the GTA V files come from (the app's settings), the postal codes and
 * fonts of the atlas maps, and the free disk space. Each value that can be
 * wrong carries its ✓ / ✗ and why, next to where it is changed. Values a running step reads cannot be changed until
 * that step is over.
 */
export function ProjectSettingsScreen() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const checks = useProjectStore((s) => s.checks);
  const edit = useProjectStore((s) => s.edit);
  const refresh = useProjectStore((s) => s.refresh);
  const setSettingsOpen = useAppStore((s) => s.setSettingsOpen);
  const serverLock = useLock(INPUTS.server);
  const consoleLock = useLock(INPUTS.console);
  const gameFilesLock = useLock(INPUTS.gameFiles);
  const postalsLock = useLock(INPUTS.postals);
  const mapsLock = useLock(INPUTS.maps);
  const f = project.file;
  const check = (id: CheckDto["id"]) => checks?.find((c) => c.id === id) ?? null;

  const [name, setName] = useState(project.name);
  const [host, setHost] = useState(f.console.host);
  const [port, setPort] = useState(String(f.console.port));
  const [newStop, setNewStop] = useState("");
  const [newFolder, setNewFolder] = useState("");
  const [postals, setPostals] = useState(false);
  const [fetching, setFetching] = useState(false);
  const [fetchError, setFetchError] = useState<string | null>(null);
  useEffect(() => setName(project.name), [project.name]);
  useEffect(() => {
    setHost(f.console.host);
    setPort(String(f.console.port));
  }, [f.console.host, f.console.port]);

  const presetName = (id: ServerPreset, fallback: string) => (id === "qbox" ? t("preset.qbox") : id === "qbcore" ? t("preset.qbcore") : fallback);
  const presets = project.presets.map((p) => ({ value: p.id, label: presetName(p.id, p.name) }));
  const presetLabel = presetName(f.server.preset, f.server.preset);
  const customStop = f.server.stopResources !== null;
  // the game's files are read for an atlas or road map and for the minimap resource
  const gameFilesRead = f.maps.roadmap || f.maps.atlas.enabled || !!f.minimap.map;
  const atlas = f.maps.atlas.enabled;
  const portNumber = Number(port);
  const consoleChanged = host.trim() !== f.console.host || portNumber !== f.console.port;
  const consoleBad = !host.trim() || !Number.isInteger(portNumber) || portNumber < 1 || portNumber > 65535;

  const addStop = () => {
    const n = newStop.trim();
    if (!n) return;
    void edit({ stopResources: { list: [...project.stopResources.filter((x) => x !== n), n], preset: false } }).then(() => setNewStop(""));
  };
  const addFolder = () => {
    const p = newFolder.trim();
    if (!p) return;
    void edit({ serverResources: [...f.gameFiles.serverResources, p] }).then(() => setNewFolder(""));
  };
  const browseFolder = async () => {
    const r = await api.pickFolder(newFolder || project.workFolder, t("settings.serverResources.add"));
    if (r.path) setNewFolder(r.path);
  };
  const browseFile = async () => {
    const r = await api.pickFile(false, newFolder || project.workFolder, t("settings.serverResources.add"), "roadData");
    if (r.path) setNewFolder(r.path);
  };
  const fetchPostals = async () => {
    setFetching(true);
    setFetchError(null);
    try {
      await api.fetchPostals();
      await refresh();
    } catch (e) {
      setFetchError(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
    } finally {
      setFetching(false);
    }
  };

  // ---- what the checks found
  const gameFiles = check("gameFiles");
  const serverResources = check("serverResources");
  const missingFolders = new Set((serverResources?.values.missing ?? "").split("\n").filter((x) => x));
  const resolvedFolders = (serverResources?.values.entries ?? "").split("\n").filter((x) => x);
  const postal = check("postals");
  const fonts = check("fonts");
  const disk = check("disk");

  const postalSource = () => {
    const v = postal?.values ?? {};
    if (v.kind === "default" || !f.postals) return t("settings.postals.default");
    return t(v.kind === "url" ? "settings.postals.url" : "settings.postals.file", { source: v.source ?? f.postals });
  };
  const postalState = (): ReactNode => {
    const v = postal?.values ?? {};
    switch (v.state) {
      case "copied":
        return t("settings.postals.copied", { n: v.count ?? "?", at: v.fetched ? new Date(v.fetched).toLocaleString() : "" });
      case "file":
        return t("settings.postals.read", { n: v.count ?? "?" });
      case "notFetched":
        return t("settings.postals.notFetched");
      case "notFound":
        return t("settings.postals.notFound");
      case "bad":
        return t("settings.postals.bad", { error: v.error ?? "" });
      default:
        return "";
    }
  };
  const fontLines = (fonts?.values.fonts ?? "").split("\n").filter((x) => x).map((l) => l.split("\t"));
  const missingFonts = new Set((fonts?.values.missing ?? "").split("\n").filter((x) => x).map((x) => x.toLowerCase()));
  const byFont = new Map<string, string[]>();
  for (const [font, map] of fontLines) byFont.set(font, [...(byFont.get(font) ?? []), mapName(t, map)]);

  return (
    <div className="screen settings-screen">
      <Info help="help.settings.intro" block className="screen-intro muted">
        {t("settings.intro")}
      </Info>

      <section className="settings-section" data-guide="settings.project">
        <Info help="help.settings.project" block className="section-title">
          {t("settings.project")}
        </Info>
        <div className="field-row">
          <TextField help="help.settings.name" label={t("settings.name")} value={name} onChange={setName} wide />
          <Button
            help="help.settings.name.save"
            disabledReason={!name.trim() ? "reason.emptyName" : name.trim() === project.name ? "reason.noChange" : null}
            onClick={() => void edit({ name: name.trim() })}
          >
            {t("common.save")}
          </Button>
        </div>
        <Info help="help.settings.file" block className="settings-value">
          <span className="field-label">{t("settings.file")}</span> <span className="path">{project.path}</span>
        </Info>
        <div className="settings-value">
          <Info help="help.settings.workFolder">
            <span className="field-label">{t("settings.workFolder")}</span> <span className="path">{project.workFolder}</span>
          </Info>
          <Button help="help.settings.workFolder.open" variant="link" onClick={() => void api.showFolder(project.workFolder)}>
            {t("settings.workFolder.open")}
          </Button>
        </div>
      </section>

      <FrameSection />

      <section className="settings-section">
        <Info help="help.settings.server" block className="section-title">
          {t("settings.server")}
        </Info>
        <div className="settings-group" data-guide="settings.server">
          <Select<ServerPreset>
            help="help.new.preset"
            label={t("new.preset")}
            value={f.server.preset}
            options={presets}
            disabledReason={serverLock}
            onChange={(v) => void edit({ serverPreset: v })}
          />

          <Info help="help.settings.stop" block className="field-label settings-sub">
            {t("settings.stop")}
          </Info>
          <Info help="help.settings.stop.source" block className="muted">
            {customStop ? t("settings.stop.custom", { n: project.stopResources.length }) : t("settings.stop.preset", { preset: presetLabel, n: project.stopResources.length })}
          </Info>
          <ul className="list-edit">
            {project.stopResources.map((n) => (
              <li key={n}>
                <Info help="help.settings.stop.item" className="list-edit-name">
                  {n}
                </Info>
                <Button
                  help="help.settings.stop.remove"
                  variant="link"
                  disabledReason={serverLock}
                  onClick={() => void edit({ stopResources: { list: project.stopResources.filter((x) => x !== n), preset: false } })}
                >
                  {t("settings.remove")}
                </Button>
              </li>
            ))}
          </ul>
          <div className="field-row">
            <TextField help="help.settings.stop.name" label={t("settings.stop.name")} value={newStop} onChange={setNewStop} />
            <Button help="help.settings.stop.add" disabledReason={serverLock ?? (!newStop.trim() ? "reason.emptyName" : null)} onClick={addStop}>
              {t("settings.add")}
            </Button>
            <Button
              help="help.settings.stop.backToPreset"
              disabledReason={serverLock ?? (!customStop ? "reason.presetList" : null)}
              onClick={() => void edit({ stopResources: { preset: true } })}
            >
              {t("settings.stop.backToPreset")}
            </Button>
          </div>

          <Info help="help.settings.console" block className="field-label settings-sub">
            {t("settings.console")}
          </Info>
          <div className="field-row">
            <TextField help="help.settings.console.host" label={t("settings.console.host")} value={host} onChange={setHost} />
            <TextField help="help.settings.console.port" label={t("settings.console.port")} value={port} onChange={setPort} />
            <Button
              help="help.settings.console.save"
              disabledReason={consoleLock ?? (consoleBad ? "reason.badConsole" : !consoleChanged ? "reason.noChange" : null)}
              onClick={() => void edit({ console: { host: host.trim(), port: portNumber } })}
            >
              {t("common.save")}
            </Button>
          </div>
        </div>

        <div className="settings-group" data-guide="settings.serverResources">
          <Info help="help.check.serverResources" block className="field-label settings-sub">
            {t("check.serverResources")}
          </Info>
          {f.gameFiles.serverResources.length === 0 ? (
            <Info help="help.check.details" block className="muted">
              {t("check.serverResources.none")}
            </Info>
          ) : (
            <ul className="list-edit">
              {f.gameFiles.serverResources.map((p, i) => {
                const found = serverResources ? !missingFolders.has(resolvedFolders[i] ?? p) : null;
                return (
                  <li key={`${i}/${p}`}>
                    <Mark ok={found} help="help.settings.serverResources.item">
                      <span className="path">{p}</span>
                      {found === false && <span className="text-error"> {t("settings.serverResources.missing")}</span>}
                    </Mark>
                    <Button
                      help="help.settings.serverResources.remove"
                      variant="link"
                      disabledReason={gameFilesLock}
                      onClick={() => void edit({ serverResources: f.gameFiles.serverResources.filter((_, j) => j !== i) })}
                    >
                      {t("settings.remove")}
                    </Button>
                  </li>
                );
              })}
            </ul>
          )}
          <div className="field-row">
            <TextField help="help.settings.serverResources.path" label={t("settings.serverResources.add")} value={newFolder} onChange={setNewFolder} wide />
            <Button help="help.settings.serverResources.browse" onClick={() => void browseFolder()}>
              {t("settings.serverResources.pickFolder")}
            </Button>
            <Button help="help.settings.serverResources.browseFile" onClick={() => void browseFile()}>
              {t("settings.serverResources.pickFile")}
            </Button>
            <Button help="help.settings.serverResources.addButton" disabledReason={gameFilesLock ?? (!newFolder.trim() ? "reason.emptyPath" : null)} onClick={addFolder}>
              {t("settings.add")}
            </Button>
          </div>
        </div>
      </section>

      {gameFilesRead && (
        <section className="settings-section" data-guide="settings.gameFiles">
          <Info help="help.settings.gameFiles" block className="section-title">
            {t("settings.gameFiles")}
          </Info>
          <Mark ok={gameFiles?.ok ?? null} help="help.check.gameFiles">
            {t("check.gameFiles")}
          </Mark>
          {gameFiles && (
            <Info help="help.check.details" block className="check-details muted">
              <div>
                {gameFiles.values.gtaOk === "1"
                  ? t("check.gameFiles.gta", { path: gameFiles.values.gta, source: sourceName(t, gameFiles.values.gtaSource) })
                  : gameFiles.values.gtaProblem === "notGta"
                    ? t("gameFiles.gta.notGta", { path: gameFiles.values.gta })
                    : t("gameFiles.gta.notFound")}
              </div>
              <div>
                {gameFiles.values.keys
                  ? t("check.gameFiles.keys", { path: gameFiles.values.keys, source: sourceName(t, gameFiles.values.keysSource) })
                  : t("gameFiles.keys.notFound")}
              </div>
            </Info>
          )}
          <div>
            <Button help="help.settings.openApp" variant="link" onClick={() => setSettingsOpen(true)}>
              {t("settings.openApp")}
            </Button>
          </div>
        </section>
      )}

      {atlas && (
        <section className="settings-section" data-guide="settings.labels">
          <Info help="help.settings.labels" block className="section-title">
            {t("settings.labels")}
          </Info>
          <Info help="help.settings.languages" block className="field-label settings-sub">
            {t("settings.languages")}
          </Info>
          <div className="settings-languages">
            <Check help="help.settings.languages.en" checked disabledReason="reason.settings.englishAlways" onChange={() => undefined}>
              {t("settings.languages.en")}
            </Check>
            <Check help="help.settings.languages.ja" checked={f.maps.atlas.languages.includes("ja")} disabledReason={mapsLock}
              onChange={(v) => void edit({ atlas: { languages: v ? ["en", "ja"] : ["en"] } })}>
              {t("settings.languages.ja")}
            </Check>
          </div>
          <Info help="help.check.postals" block className="field-label settings-sub">
            {t("check.postals")}
          </Info>
          <Info help="help.settings.postals.source" block>
            {postalSource()}
          </Info>
          <Mark ok={postal ? (postal.values.state === "notFetched" ? null : postal.ok) : null} help="help.settings.postals.state">
            {postalState()}
          </Mark>
          <div className="field-row">
            <Button help="help.settings.postals.change" disabledReason={postalsLock} onClick={() => setPostals(true)}>
              {t("settings.postals.change")}
            </Button>
            {postal?.values.state === "notFetched" && (
              <Button help="help.settings.postals.fetch" disabledReason={fetching ? "reason.fetching" : null} onClick={() => void fetchPostals()}>
                {t("settings.postals.fetch")}
              </Button>
            )}
          </div>
          {fetchError && <div className="text-error">{fetchError}</div>}

          <Info help="help.check.fonts" block className="field-label settings-sub">
            {t("check.fonts")}
          </Info>
          {[...byFont].map(([font, maps]) => (
            <Mark key={font} ok={!missingFonts.has(font.toLowerCase())} help="help.settings.fonts.item">
              {t("settings.fonts.item", { font, maps: maps.join(t("list.join")) })}
              {missingFonts.has(font.toLowerCase()) && <span className="text-error"> {t("settings.fonts.missing")}</span>}
            </Mark>
          ))}
          {missingFonts.size > 0 && (
            <Info help="help.settings.fonts.install" block className="note">
              {t("settings.fonts.install")}
            </Info>
          )}
        </section>
      )}

      <section className="settings-section" data-guide="settings.disk">
        <Info help="help.settings.disk" block className="section-title">
          {t("settings.disk")}
        </Info>
        {disk && (
          <Mark ok={disk.ok} help="help.check.disk">
            {t("check.disk", disk.values)}
            <span className="muted"> — {disk.values.folder}</span>
          </Mark>
        )}
      </section>

      {postals && <PostalsDialog onClose={() => setPostals(false)} />}
    </div>
  );
}
