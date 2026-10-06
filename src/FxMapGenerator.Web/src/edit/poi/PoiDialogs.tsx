import { useEffect, useMemo, useState } from "react";
import { api, ApiError } from "../../shared/api";
import { Button, Info, Select, TextField } from "../../shared/controls";
import { errorText, useI18nStore, useT } from "../../shared/i18n";
import { itemName, makesJapanese, type PoiImportDto, type PoiImportReason } from "../../shared/types";
import type { MessageKey } from "../../shared/i18n";
import { useProjectStore } from "../../project/store";
import { addFolder, addGroup, chainOf, folderName, groupName, labelIn, parentOf, renameFolder, renameGroup } from "./model";
import { useLabelLang, usePoiEditor } from "./store";

/** Closes a window with Esc. */
function useEscape(onClose: () => void) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
}

/** What the name window names: a new folder or group (in a folder), or a folder or group to rename. */
export type NameTarget = { kind: "newFolder"; parent: string } | { kind: "newGroup"; parent: string } | { kind: "folder"; path: string } | { kind: "group"; path: string };

/**
 * The name of a folder or group (one text, in the user's own words): for a new one, or another name for one there is
 * (a bundled one's name starts as the screen shows it). Its name on the disk follows the name.
 */
export function NameDialog({ target, onClose }: { target: NameTarget; onClose(): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  useEscape(onClose);
  const set = st.set!;
  const existing = target.kind === "folder" ? set.folders.find((f) => f.path === target.path) : target.kind === "group" ? set.groups.find((g) => g.path === target.path) : null;
  const shown = existing ? (target.kind === "folder" ? folderName(existing as never, lang) : groupName(existing as never, lang)) : "";
  const [name, setName] = useState(shown);
  const title = t(target.kind === "newFolder" ? "poi.name.newFolder" : target.kind === "newGroup" ? "poi.name.newGroup" : "poi.name.rename");
  const ok = () => {
    const text = name.trim();
    const s = usePoiEditor.getState();
    if (target.kind === "newFolder") {
      const r = addFolder(set, target.parent, text);
      s.change(r.set, { folders: [r.path], groups: [], points: [] });
      s.toggle(target.parent, true);
    } else if (target.kind === "newGroup") {
      const r = addGroup(set, target.parent, text, null);
      s.change(r.set, { folders: [], groups: [r.path], points: [] });
      s.toggle(target.parent, true);
    } else if (target.kind === "folder") {
      const r = renameFolder(set, target.path, text);
      s.change(r.set);
      s.moved(target.path, r.path);
    } else {
      const r = renameGroup(set, target.path, text);
      s.change(r.set);
      s.moved(target.path, r.path);
    }
    onClose();
  };
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form style-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.poi.name.dialog" block className="muted">
            {t("poi.name.intro")}
          </Info>
          <TextField help="help.poi.name.field" label={t("poi.name")} value={name} onChange={setName} />
        </div>
        <div className="modal-foot">
          <Button help="help.poi.dialog.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.poi.name.ok" variant="primary" disabledReason={!name.trim() ? "reason.poi.noName" : null} onClick={ok}>
            {t("poi.name.ok")}
          </Button>
        </div>
      </div>
    </div>
  );
}

/**
 * Points read from a CSV or JSON file: what was read (and the lines left out, with why), then the folder they go to,
 * the group's name and the POI style, made a group of their own.
 */
export function ImportDialog({ file, folder, onClose }: { file: File; folder: string; onClose(): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const labelLang = useLabelLang();
  const japanese = useProjectStore((s) => makesJapanese(s.project!.file));
  const st = usePoiEditor();
  useEscape(onClose);
  const set = st.set!;
  const [read, setRead] = useState<PoiImportDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [into, setInto] = useState(folder);
  const [name, setName] = useState(file.name.replace(/\.[^.]*$/, ""));
  const inheritedStyle = chainOf(set, into).find((f) => f.style)?.style ?? null;
  const [style, setStyle] = useState<string>("");
  useEffect(() => {
    let live = true;
    void file.text().then(
      (text) =>
        api.poiImport(file.name, text).then(
          (r) => live && setRead(r),
          (e) => live && setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e)),
        ),
      (e) => live && setError(String(e)),
    );
    return () => {
      live = false;
    };
  }, [file]);
  const folders = set.folders.map((f) => ({ value: f.path, label: f.path === "" ? t("poi.top") : `${"　".repeat(f.path.split("/").length - 1)}${folderName(f, lang)}` }));
  const styles = [
    ...(inheritedStyle ? [{ value: "", label: t("poi.import.styleFromFolder") }] : []),
    ...st.dto!.bundledStyles.map((s) => ({ value: s.id, label: itemName(s.name, lang) || s.id, group: t("poi.styles.bundled") })),
    ...set.styles.map((s) => ({ value: s.id, label: itemName(s.name, lang) || s.id, group: t("poi.styles.project") })),
  ];
  const chosenStyle = style || (inheritedStyle ? "" : "facility");
  const title = t("poi.import.title");
  const ok = () => {
    if (!read) return;
    const r = addGroup(set, into, name.trim() || file.name, chosenStyle || null, read.points);
    const s = usePoiEditor.getState();
    s.change(r.set, { folders: [], groups: [r.path], points: [] });
    for (const f of chainOf(r.set, parentOf(r.path))) s.toggle(f.path, true);
    s.setNotice(t("poi.import.done", { n: read.points.length, name: name.trim() || file.name }));
    onClose();
  };
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form style-dialog poi-import" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.poi.import.dialog" block className="muted">
            {t(japanese ? "poi.import.intro.ja" : "poi.import.intro", { file: file.name })}
          </Info>
          {error && (
            <Info help="help.poi.import.dialog" block className="text-error">
              {error}
            </Info>
          )}
          {read && (
            <>
              <Info help="help.poi.import.count" block>
                {t("poi.import.count", { n: read.points.length })}
              </Info>
              {read.points.length > 0 && (
                <table className="poi-import-table">
                  <tbody>
                    {read.points.slice(0, 5).map((p) => (
                      <tr key={p.id}>
                        <td>{labelIn(p.label, labelLang) || t("poi.noLabel")}</td>
                        <td>{p.name}</td>
                        <td>{p.x}</td>
                        <td>{p.y}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {read.skipped.length > 0 && (
                <Info help="help.poi.import.skipped" block className="text-error">
                  {t("poi.import.skipped", { n: read.skipped.length })}
                  <br />
                  {read.skipped.slice(0, 8).map((s) => t("poi.import.skippedLine", { line: s.line, reason: s.reasons.map((r) => reasonText(t, r)).join(t("edit.leave.join")) })).join(" / ")}
                </Info>
              )}
              <Select help="help.poi.import.folder" label={t("poi.import.folder")} value={into} options={folders} onChange={setInto} />
              <TextField help="help.poi.import.name" label={t("poi.import.name")} value={name} onChange={setName} />
              <Select help="help.poi.import.style" label={t("poi.style")} value={chosenStyle} options={styles} onChange={setStyle} />
            </>
          )}
        </div>
        <div className="modal-foot">
          <Button help="help.poi.dialog.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.poi.import.ok" variant="primary" disabledReason={!read ? "reason.poi.import.reading" : read.points.length === 0 ? "reason.poi.import.nothing" : null} onClick={ok}>
            {t("poi.import.ok")}
          </Button>
        </div>
      </div>
    </div>
  );
}

const REASONS: Record<PoiImportReason["code"], MessageKey> = {
  x: "poi.import.reason.x",
  y: "poi.import.reason.y",
  offMap: "poi.import.reason.offMap",
  color: "poi.import.reason.color",
  size: "poi.import.reason.size",
  empty: "poi.import.reason.empty",
  columns: "poi.import.reason.columns",
  json: "poi.import.reason.json",
  list: "poi.import.reason.list",
  point: "poi.import.reason.point",
};

/** A reason a line is left out, in the screen's words. */
function reasonText(t: ReturnType<typeof useT>, r: PoiImportReason): string {
  return t(REASONS[r.code] ?? "poi.import.reason.point", { value: r.value ?? "" });
}

/** At most this many icons are shown at once (the search narrows them). */
const ICON_LIMIT = 400;

/** The MDI icons, found by their English names, other names and groups; a press chooses one. */
export function IconPicker({ value, onPick, onClose }: { value: string | null; onPick(name: string): void; onClose(): void }) {
  const t = useT();
  const icons = usePoiEditor((s) => s.icons);
  useEscape(onClose);
  const [q, setQ] = useState("");
  const found = useMemo(() => {
    if (!icons) return [];
    const words = q.trim().toLowerCase().split(/\s+/).filter(Boolean);
    // the name itself first, then names starting with the words, names holding them as a whole word, then the rest
    const rank = (name: string) => {
      if (words.length === 0) return 0;
      const whole = words.join("-");
      if (name === whole) return 0;
      if (name.startsWith(whole)) return 1;
      if (words.every((w) => name.split("-").includes(w))) return 2;
      return words.every((w) => name.includes(w)) ? 3 : 4;
    };
    return Object.entries(icons.icons)
      .filter(([name, i]) => {
        const text = [name, ...i.aliases, ...i.tags].join(" ").toLowerCase();
        return words.every((w) => text.includes(w));
      })
      .map(([name, i]) => ({ name, i, r: rank(name) }))
      .sort((a, b) => a.r - b.r || (a.name < b.name ? -1 : 1))
      .map((e) => [e.name, e.i] as const);
  }, [icons, q]);
  const title = t("poi.icon.title");
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form poi-icon-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <TextField help="help.poi.icon.search" label={t("poi.icon.search")} value={q} onChange={setQ} />
          <Info help="help.poi.icon.count" block className="muted">
            {icons ? t(found.length > ICON_LIMIT ? "poi.icon.countMore" : "poi.icon.count", { n: found.length, shown: ICON_LIMIT, version: icons.version }) : t("common.loading")}
          </Info>
          <div className="poi-icon-grid">
            {found.slice(0, ICON_LIMIT).map(([name, i]) => (
              <Button key={name} help={{ text: name }} variant="row" className={`poi-icon-cell${name === value ? " chosen" : ""}`} onClick={() => onPick(name)}>
                <IconSvg path={i.path} />
              </Button>
            ))}
          </div>
        </div>
        <div className="modal-foot">
          <Button help="help.poi.dialog.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
        </div>
      </div>
    </div>
  );
}

/** An MDI icon as an inline picture. */
export function IconSvg({ path }: { path: string }) {
  return (
    <svg viewBox="0 0 24 24" className="poi-icon-svg">
      <path d={path} />
    </svg>
  );
}
