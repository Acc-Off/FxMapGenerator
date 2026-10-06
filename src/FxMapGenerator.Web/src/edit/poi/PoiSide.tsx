import { type DragEvent, useMemo, useState } from "react";
import { Button, FileButton, Info, TextField } from "../../shared/controls";
import { t as translate, useI18nStore, useT } from "../../shared/i18n";
import { type ItemName, itemName, type PoiEditFolder, type PoiEditGroup, type PoiEditSet, type PoiEditStyle } from "../../shared/types";
import {
  chainOf,
  type Entry,
  entriesOf,
  folderName,
  groupName,
  lastOf,
  movePointsTo,
  parentOf,
  pathOf,
  place,
  pointCount,
  pointKey,
  pointName,
  removeFolder,
  removeGroup,
  splitKey,
  styleById,
  updateFolder,
  updateGroup,
} from "./model";
import { ImportDialog, NameDialog, type NameTarget } from "./PoiDialogs";
import { StyleSwatch } from "./PoiStyles";
import { NOTHING, useLabelLang, usePoiEditor } from "./store";

/** What is being dragged in the tree. */
type Dragged = { kind: "folder" | "group"; path: string } | { kind: "points"; keys: string[] };
let dragged: Dragged | null = null;

type Dialog = { kind: "name"; target: NameTarget } | { kind: "import"; file: File } | null;

/**
 * The left side of the POI screen: the switch between the points and the POI styles; for the points a search, the
 * folders' tree (folders, groups, points; counts, shown or hidden, locked; dragged to another place) and what adds to
 * it; for the styles their list.
 */
export function PoiSide({ onFly }: { onFly(x: number, y: number): void }) {
  const t = useT();
  const st = usePoiEditor();
  return (
    <aside className="side edit-side poi-side">
      <div className="edit-switch poi-switch" data-guide="poi.side">
        <Button help="help.poi.side.points" className="tab" pressed={st.side === "points"} onClick={() => st.setSide("points")}>
          {t("poi.side.points")}
        </Button>
        <Button help="help.poi.side.styles" className="tab" pressed={st.side === "styles"} onClick={() => st.setSide("styles")}>
          {t("poi.side.styles")}
        </Button>
      </div>
      {st.side === "points" ? <PointsSide onFly={onFly} /> : <StylesSide />}
    </aside>
  );
}

function PointsSide({ onFly }: { onFly(x: number, y: number): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  const [search, setSearch] = useState("");
  const [dialog, setDialog] = useState<Dialog>(null);
  const set = st.set!;
  const bundled = st.dto!.bundledStyles;
  const sel = st.selection;
  // where a new folder or group goes: the folder chosen, or the folder of the group or point chosen
  const here = sel.folders.length === 1 ? sel.folders[0] : sel.groups.length === 1 ? parentOf(sel.groups[0]) : sel.points.length > 0 ? parentOf(splitKey(sel.points[0])[0]) : "";
  const one = sel.folders.length + sel.groups.length === 1 && sel.points.length === 0;
  const oneLocked = one && (sel.folders.length === 1 ? chainOf(set, sel.folders[0]).some((f) => f.locked) : lockedGroup(set, sel.groups[0]));
  const q = search.trim().toLowerCase();
  const matches = useMemo(() => (q ? matching(set, bundled, q, lang) : null), [set, bundled, q, lang]);


  const remove = () => {
    const s = usePoiEditor.getState();
    if (sel.folders.length === 1) {
      const f = set.folders.find((x) => x.path === sel.folders[0])!;
      if (!window.confirm(t("poi.delete.folder.confirm", { name: folderName(f, lang), n: pointCount(set, f.path) }))) return;
      s.change(removeFolder(set, f.path), NOTHING);
    } else if (sel.groups.length === 1) {
      const g = set.groups.find((x) => x.path === sel.groups[0])!;
      if (!window.confirm(t("poi.delete.group.confirm", { name: groupName(g, lang), n: g.points.length }))) return;
      s.change(removeGroup(set, g.path), NOTHING);
    }
  };
  const oneReason = !one ? "reason.poi.oneFolderOrGroup" : sel.folders[0] === "" ? "reason.poi.topFolder" : null;

  return (
    <>
      <TextField help="help.poi.search" label={t("poi.search")} value={search} onChange={setSearch} />
      <div className="edit-buttons poi-tree-buttons" data-guide="poi.buttons">
        <Button help="help.poi.addFolder" onClick={() => setDialog({ kind: "name", target: { kind: "newFolder", parent: here } })}>
          {t("poi.addFolder")}
        </Button>
        <Button help="help.poi.addGroup" onClick={() => setDialog({ kind: "name", target: { kind: "newGroup", parent: here } })}>
          {t("poi.addGroup")}
        </Button>
        <Button help="help.poi.rename" disabledReason={oneReason}
          onClick={() => setDialog({ kind: "name", target: sel.folders.length === 1 ? { kind: "folder", path: sel.folders[0] } : { kind: "group", path: sel.groups[0] } })}>
          {t("poi.rename")}
        </Button>
        <Button help="help.poi.deleteItem" variant="danger" disabledReason={oneReason ?? (oneLocked ? "reason.poi.locked" : null)} onClick={remove}>
          {t("poi.deleteItem")}
        </Button>
        <FileButton help="help.poi.import" accept=".csv,.json,text/csv,application/json" onFile={(file) => setDialog({ kind: "import", file })}>
          {t("poi.import")}
        </FileButton>
      </div>
      <div className="poi-tree" role="tree" data-guide="poi.tree">
        <FolderRows folder="" depth={0} matches={matches} onFly={onFly} />
      </div>
      {dialog?.kind === "import" && <ImportDialog file={dialog.file} folder={here} onClose={() => setDialog(null)} />}
      {dialog?.kind === "name" && <NameDialog target={dialog.target} onClose={() => setDialog(null)} />}
    </>
  );
}

function lockedGroup(set: PoiEditSet, path: string): boolean {
  const g = set.groups.find((x) => x.path === path);
  return !!g && (g.locked || chainOf(set, parentOf(g.path)).some((f) => f.locked));
}

/** What a search finds: the folders and groups to show (with their folders above) and the points that match. */
interface Matches {
  folders: Set<string>;
  groups: Set<string>;
  points: Set<string>;
}

/** What a search finds: names (a bundled one's in either language), points' names and labels, style names. */
function matching(set: PoiEditSet, bundled: readonly PoiEditStyle[], q: string, lang: "en" | "ja"): Matches {
  const m: Matches = { folders: new Set(), groups: new Set(), points: new Set() };
  const hit = (s: string | null | undefined) => !!s && s.toLowerCase().includes(q);
  const names = (n: ItemName | null) => (n == null ? [] : typeof n === "string" ? [n] : [n.en, n.ja ?? ""]);
  const showUp = (folder: string) => {
    for (const f of chainOf(set, folder)) m.folders.add(f.path);
  };
  for (const f of set.folders)
    if (f.path && (names(f.name).some(hit) || hit(lastOf(f.path)) || hit(styleName(set, bundled, f.style, lang)))) {
      showUp(f.path);
      // everything in a folder found is shown
      for (const g of set.groups) if (g.path.startsWith(f.path + "/")) m.groups.add(g.path);
      for (const x of set.folders) if (x.path.startsWith(f.path + "/")) m.folders.add(x.path);
    }
  for (const g of set.groups) {
    const groupHit = names(g.name).some(hit) || hit(lastOf(g.path)) || hit(styleName(set, bundled, g.style, lang));
    for (const p of g.points)
      if (hit(p.name) || Object.values(p.label).some(hit) || hit(styleName(set, bundled, p.style, lang))) {
        m.points.add(pointKey(g.path, p.id));
        m.groups.add(g.path);
      }
    if (groupHit) m.groups.add(g.path);
    if (m.groups.has(g.path)) showUp(parentOf(g.path));
  }
  return m;
}

function styleName(set: PoiEditSet, bundled: readonly PoiEditStyle[], id: string | null, lang: "en" | "ja"): string {
  const s = styleById(set, bundled, id);
  return s ? itemName(s.name, lang) || s.id : "";
}

function FolderRows({ folder, depth, matches, onFly }: { folder: string; depth: number; matches: Matches | null; onFly(x: number, y: number): void }) {
  const set = usePoiEditor((s) => s.set)!;
  const root = set.folders.find((f) => f.path === "")!;
  const entries = entriesOf(set, folder).filter((e) => !matches || (e.kind === "group" ? matches.groups.has(e.group.path) : matches.folders.has(e.folder.path)));
  return (
    <>
      {folder === "" && <FolderRow folder={root} depth={0} open />}
      {entries.map((e) =>
        e.kind === "folder" ? (
          <FolderBranch key={`f:${e.folder.path}`} folder={e.folder} depth={depth + 1} matches={matches} onFly={onFly} />
        ) : (
          <GroupBranch key={`g:${e.group.path}`} group={e.group} depth={depth + 1} matches={matches} onFly={onFly} />
        ),
      )}
    </>
  );
}

function FolderBranch({ folder, depth, matches, onFly }: { folder: PoiEditFolder; depth: number; matches: Matches | null; onFly(x: number, y: number): void }) {
  const open = usePoiEditor((s) => s.open.has(folder.path)) || !!matches;
  return (
    <>
      <FolderRow folder={folder} depth={depth} open={open} />
      {open && <FolderRows folder={folder.path} depth={depth} matches={matches} onFly={onFly} />}
    </>
  );
}

function GroupBranch({ group, depth, matches, onFly }: { group: PoiEditGroup; depth: number; matches: Matches | null; onFly(x: number, y: number): void }) {
  const t = useT();
  const labelLang = useLabelLang();
  const st = usePoiEditor();
  const found = matches ? group.points.filter((p) => matches.points.has(pointKey(group.path, p.id))) : [];
  const open = st.open.has(`g:${group.path}`) || found.length > 0;
  const points = found.length > 0 ? found : group.points;
  return (
    <>
      <GroupRow group={group} depth={depth} open={open} />
      {open &&
        points.map((p) => {
          const k = pointKey(group.path, p.id);
          const chosen = st.selection.points.includes(k);
          return (
            <div
              key={k}
              className={`poi-row poi-point${chosen ? " chosen" : ""}`}
              style={{ paddingLeft: 8 + (depth + 1) * 14 }}
              draggable
              onDragStart={(e) => startDrag(e, { kind: "points", keys: chosen ? st.selection.points : [k] })}
              onDragOver={(e) => overRow(e, "into")}
              onDragLeave={leaveRow}
              onDrop={(e) => dropOn(e, { kind: "group", path: group.path }, "into")}
            >
              <Button
                help="help.poi.row.point"
                variant="row"
                className="poi-row-name"
                onClick={(e) => {
                  // Shift adds to the points chosen (or takes it out)
                  const cur = usePoiEditor.getState().selection.points;
                  st.select({ folders: [], groups: [], points: e.shiftKey ? (chosen ? cur.filter((x) => x !== k) : [...cur, k]) : [k] });
                  onFly(p.x, p.y);
                }}
              >
                {pointName(p, labelLang) || t("poi.noName")}
              </Button>
              {p.visible === false && <span className="poi-mark muted">{t("poi.hidden.mark")}</span>}
            </div>
          );
        })}
    </>
  );
}

function startDrag(e: DragEvent, what: Dragged) {
  dragged = what;
  e.dataTransfer.effectAllowed = "move";
  e.dataTransfer.setData("text/plain", "poi");
}

/** Where a drop over a row puts what is dragged: before it, after it, or into it (a folder, or a group for points). */
type Where = "before" | "after" | "into";

function whereOn(e: DragEvent, kind: "folder" | "group"): Where {
  const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
  const f = (e.clientY - r.top) / Math.max(1, r.height);
  if (dragged?.kind === "points") return "into";
  if (kind === "folder") return f < 0.25 ? "before" : f > 0.75 ? "after" : "into";
  return f < 0.5 ? "before" : "after";
}

function overRow(e: DragEvent, where: Where) {
  if (!dragged) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = "move";
  (e.currentTarget as HTMLElement).dataset.drop = where;
}

function leaveRow(e: DragEvent) {
  delete (e.currentTarget as HTMLElement).dataset.drop;
}

/** Puts what is dragged at the row: into a folder, before or after a folder or group, points into a group. */
function dropOn(e: DragEvent, target: { kind: "folder" | "group"; path: string }, where: Where) {
  e.preventDefault();
  delete (e.currentTarget as HTMLElement).dataset.drop;
  const what = dragged;
  dragged = null;
  const s = usePoiEditor.getState();
  if (!what || !s.set) return;
  const set = s.set;
  if (what.kind === "points") {
    if (target.kind !== "group") return;
    const pointLocked = (k: string) => {
      const [g, id] = splitKey(k);
      return lockedGroup(set, g) || !!set.groups.find((x) => x.path === g)?.points.find((p) => p.id === id)?.locked;
    };
    if (what.keys.some(pointLocked) || lockedGroup(set, target.path)) {
      s.setNotice(translate("poi.drag.locked"));
      return;
    }
    const moved = movePointsTo(set, what.keys, target.path);
    s.change(moved.set, { folders: [], groups: [], points: moved.keys });
    return;
  }
  // a folder or group goes into a folder (at its end), or next to the target in the target's folder
  if (what.path === target.path && what.kind === target.kind) return;
  const into = where === "into" && target.kind === "folder" ? target.path : parentOf(target.path);
  let before: Entry | null = null;
  if (where !== "into") {
    const entries = entriesOf(set, into);
    const i = entries.findIndex((x) => x.kind === target.kind && pathOf(x) === target.path);
    before = where === "before" ? (entries[i] ?? null) : (entries[i + 1] ?? null);
    if (before && before.kind === what.kind && pathOf(before) === what.path) return;
  }
  const locked = what.kind === "group" ? lockedGroup(set, what.path) : chainOf(set, what.path).some((f) => f.locked);
  if (locked || (into !== "" && chainOf(set, into).some((f) => f.locked))) {
    s.setNotice(translate("poi.drag.locked"));
    return;
  }
  const r = place(set, what.kind, what.path, into, before);
  if (!r) return;
  s.change(r.set);
  s.moved(what.path, r.path);
  if (into !== "") s.toggle(into, true);
}

function FolderRow({ folder, depth, open }: { folder: PoiEditFolder; depth: number; open: boolean }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  const set = st.set!;
  const chosen = st.selection.folders.includes(folder.path);
  const isRoot = folder.path === "";
  const hiddenAbove = !isRoot && chainOf(set, parentOf(folder.path)).some((f) => !f.visible);
  const lockedAbove = !isRoot && chainOf(set, parentOf(folder.path)).some((f) => f.locked);
  return (
    <div
      className={`poi-row poi-folder${chosen ? " chosen" : ""}`}
      style={{ paddingLeft: 8 + depth * 14 }}
      draggable={!isRoot}
      onDragStart={(e) => startDrag(e, { kind: "folder", path: folder.path })}
      onDragOver={(e) => overRow(e, whereOn(e, "folder"))}
      onDragLeave={leaveRow}
      onDrop={(e) => dropOn(e, { kind: "folder", path: folder.path }, isRoot ? "into" : whereOn(e, "folder"))}
    >
      {!isRoot && (
        <Button help="help.poi.row.open" variant="row" className="poi-toggle" onClick={() => st.toggle(folder.path)}>
          {open ? "▾" : "▸"}
        </Button>
      )}
      <Button help={isRoot ? "help.poi.row.top" : "help.poi.row.folder"} variant="row" className="poi-row-name" onClick={() => st.select({ folders: [folder.path], groups: [], points: [] })}>
        <span className="poi-kind">{isRoot ? "⌂" : "📁"}</span>
        {isRoot ? t("poi.top") : folderName(folder, lang)}
      </Button>
      <span className="poi-count">{pointCount(set, folder.path)}</span>
      <Button help="help.poi.row.visible" variant="row" className={`poi-flag${folder.visible && !hiddenAbove ? "" : " off"}`} pressed={!folder.visible}
        onClick={() => st.change(updateFolder(set, folder.path, (f) => ({ ...f, visible: !f.visible })))}>
        {folder.visible ? "👁" : "⊘"}
      </Button>
      <Button help="help.poi.row.lock" variant="row" className={`poi-flag${folder.locked || lockedAbove ? " on" : ""}`} pressed={folder.locked}
        onClick={() => st.change(updateFolder(set, folder.path, (f) => ({ ...f, locked: !f.locked })))}>
        {folder.locked || lockedAbove ? "🔒" : "🔓"}
      </Button>
    </div>
  );
}

function GroupRow({ group, depth, open }: { group: PoiEditGroup; depth: number; open: boolean }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  const set = st.set!;
  const chosen = st.selection.groups.includes(group.path);
  const bundledGroup = st.dto?.folder === null && st.dto.bundledGroups.includes(group.path);
  const lockedAbove = chainOf(set, parentOf(group.path)).some((f) => f.locked);
  const hiddenAbove = chainOf(set, parentOf(group.path)).some((f) => !f.visible);
  const style = styleById(set, st.dto!.bundledStyles, group.style ?? chainOf(set, parentOf(group.path)).find((f) => f.style)?.style);
  return (
    <div
      className={`poi-row poi-group${chosen ? " chosen" : ""}`}
      style={{ paddingLeft: 8 + depth * 14 }}
      draggable
      onDragStart={(e) => startDrag(e, { kind: "group", path: group.path })}
      onDragOver={(e) => overRow(e, whereOn(e, "group"))}
      onDragLeave={leaveRow}
      onDrop={(e) => dropOn(e, { kind: "group", path: group.path }, whereOn(e, "group"))}
    >
      <Button help="help.poi.row.open" variant="row" className="poi-toggle" onClick={() => st.toggle(`g:${group.path}`)}>
        {open ? "▾" : "▸"}
      </Button>
      <Button help="help.poi.row.group" variant="row" className="poi-row-name" onClick={() => st.select({ folders: [], groups: [group.path], points: [] })}>
        {style ? <StyleSwatch style={style} /> : <span className="poi-kind">📄</span>}
        {groupName(group, lang)}
        {bundledGroup && <span className="poi-mark muted">{t("poi.bundled.mark")}</span>}
      </Button>
      <span className="poi-count">{group.points.length}</span>
      <Button help="help.poi.row.visible" variant="row" className={`poi-flag${group.visible && !hiddenAbove ? "" : " off"}`} pressed={!group.visible}
        onClick={() => st.change(updateGroup(set, group.path, (g) => ({ ...g, visible: !g.visible })))}>
        {group.visible ? "👁" : "⊘"}
      </Button>
      <Button help="help.poi.row.lock" variant="row" className={`poi-flag${group.locked || lockedAbove ? " on" : ""}`} pressed={group.locked}
        onClick={() => st.change(updateGroup(set, group.path, (g) => ({ ...g, locked: !g.locked })))}>
        {group.locked || lockedAbove ? "🔒" : "🔓"}
      </Button>
    </div>
  );
}

// ---------------------------------------------------------------- the styles side

function StylesSide() {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  const bundled = st.dto!.bundledStyles;
  const own = st.set!.styles;
  const row = (s: PoiEditStyle, isBundled: boolean) => (
    <Button key={s.id} help={isBundled ? "help.poi.style.row.bundled" : "help.poi.style.row"} variant="row" className={`poi-row poi-style-row${st.styleId === s.id ? " chosen" : ""}`} onClick={() => st.setStyleId(s.id)}>
      <StyleSwatch style={s} />
      <span className="poi-row-name">{itemName(s.name, lang) || s.id}</span>
    </Button>
  );
  return (
    <div className="poi-styles-list">
      <Info help="help.poi.styles.bundled" block className="poi-heading">
        {t("poi.styles.bundled")}
      </Info>
      {bundled.map((s) => row(s, true))}
      <Info help="help.poi.styles.project" block className="poi-heading">
        {t("poi.styles.project")}
      </Info>
      {own.length === 0 ? (
        <Info help="help.poi.styles.none" block className="muted">
          {t("poi.styles.none")}
        </Info>
      ) : (
        own.map((s) => row(s, false))
      )}
    </div>
  );
}
