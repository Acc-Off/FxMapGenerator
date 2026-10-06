import { type ReactNode, useMemo, useState } from "react";
import { Button, Check, type HelpKey, Info, NumberField, type Reason, Select, TextField } from "../../shared/controls";
import { type MessageKey, useI18nStore, useT } from "../../shared/i18n";
import { itemName, makesJapanese, type PoiEditFolder, type PoiEditGroup, type PoiEditPoint, type PoiEditSet, type PoiLabel, type PoiShow, type StyleValues } from "../../shared/types";
import { INPUTS } from "../../project/labels";
import { useLock } from "../../project/locks";
import { useProjectStore } from "../../project/store";
import { ColorBox } from "../styles/StyleControls";
import {
  chainOf,
  DEFAULT_SHOW,
  folderName,
  groupName,
  inherited,
  labelIn,
  movePointsTo,
  orderedGroups,
  parentOf,
  resolveAll,
  round,
  type Source,
  splitKey,
  type Target,
  updateFolder,
  updateGroup,
  updatePoints,
} from "./model";
import { NameDialog, type NameTarget } from "./PoiDialogs";
import { PoiStyleEditor } from "./PoiStyles";
import { POI_TOOLS, type PoiTool, useLabelLang, usePoiEditor } from "./store";

interface Option {
  value: string;
  label: string;
}

interface Props {
  backgrounds: Option[];
  background: string;
  onBackground(v: string): void;
  onRemovePoints(): void;
}

/** The list's entry that takes the style from above again (not an id of a style). */
const INHERIT = ":inherit:";

/** Each tool's name and help (the key that chooses it: its place in POI_TOOLS). */
const TOOL_TEXT: Record<PoiTool, readonly [MessageKey, HelpKey]> = {
  place: ["poi.tool.place", "help.poi.tool.place"],
  move: ["poi.tool.move", "help.poi.tool.move"],
  select: ["poi.tool.select", "help.poi.tool.select"],
  box: ["poi.tool.box", "help.poi.tool.box"],
};

/** The POI screen's right side: saving, the tools, what the map shows, and the values of what is chosen (or of the POI style chosen). */
export function PoiPanel(p: Props) {
  const t = useT();
  const st = usePoiEditor();
  const japanese = useProjectStore((s) => makesJapanese(s.project!.file));
  const lock = useLock(INPUTS.poi);
  const dirty = st.dirty();
  const dto = st.dto!;
  const save = async () => {
    if (await st.save()) void useProjectStore.getState().refresh();
  };
  const where = dto.folder ? t("poi.file", { folder: dto.folder, styles: dto.stylesFile ?? t("poi.file.noStyles") }) : t("poi.file.bundled");
  return (
    <aside className="side edit-side poi-panel">
      <section data-guide="poi.save">
        <Info help="help.poi.file" block className="muted">
          {where}
        </Info>
        <div className="edit-buttons">
          <Button help="help.poi.undo" disabledReason={st.past.length === 0 ? "reason.poi.noUndo" : null} onClick={st.undo}>
            {t("poi.undo")}
          </Button>
          <Button help="help.poi.redo" disabledReason={st.future.length === 0 ? "reason.poi.noRedo" : null} onClick={st.redo}>
            {t("poi.redo")}
          </Button>
          <Button help="help.poi.save" variant="primary" disabledReason={lock ?? (st.saving ? "reason.poi.saving" : !dirty ? "reason.poi.nothingToSave" : null)} onClick={() => void save()}>
            {t("poi.save")}
          </Button>
        </div>
        {dirty && (
          <Info help="help.poi.unsaved" block className="edit-unsaved">
            {t("poi.unsaved")}
          </Info>
        )}
        {st.notice && (
          <div className="banner">
            {st.notice}
            <Button help="help.poi.notice.close" variant="link" onClick={() => st.setNotice(null)}>
              {t("common.close")}
            </Button>
          </div>
        )}
        {st.error && (
          <div className="banner banner-error">
            {st.error}
            <Button help="help.poi.error.close" variant="link" onClick={st.clearError}>
              {t("common.close")}
            </Button>
          </div>
        )}
      </section>

      <section data-guide="poi.tools">
        <Info help="help.poi.tools" block className="section-title">
          {t("poi.tools")}
        </Info>
        <div className="edit-buttons">
          {POI_TOOLS.map((tool, i) => (
            <Button key={tool} help={TOOL_TEXT[tool][1]} pressed={st.tool === tool} onClick={() => st.setTool(tool)}>
              <span className="edit-tool-key">{i + 1}</span>
              {t(TOOL_TEXT[tool][0])}
            </Button>
          ))}
        </div>
      </section>

      <section>
        <Info help="help.poi.display" block className="section-title">
          {t("poi.display")}
        </Info>
        <Select help="help.poi.background" label={t("poi.background")} value={p.background} options={p.backgrounds} onChange={p.onBackground} />
        {japanese && (
          <Select help="help.poi.labelLang" label={t("poi.labelLang")} value={st.labelLang} options={[{ value: "ja", label: t("lang.ja") }, { value: "en", label: t("lang.en") }]}
            onChange={(v) => st.setLabelLang(v === "ja" ? "ja" : "en")} />
        )}
      </section>

      <section className="poi-values" data-guide="poi.values">{st.side === "styles" ? <PoiStyleEditor /> : <Chosen onRemovePoints={p.onRemovePoints} />}</section>
    </aside>
  );
}

/** "{name} から" for a value taken from above, "既定" when nothing above says. */
function useFromText(): (from: Source) => string {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const set = usePoiEditor((s) => s.set)!;
  return (from) => {
    if (from.kind === "default") return t("poi.from.default");
    if (from.kind === "group") {
      const g = set.groups.find((x) => x.path === from.path);
      return t("poi.from", { name: g ? groupName(g, lang) : from.path });
    }
    const f = set.folders.find((x) => x.path === from.path);
    return t("poi.from", { name: from.path === "" ? t("poi.top") : f ? folderName(f, lang) : from.path });
  };
}

/** A row of a value that may come from above: the control, then where it comes from, or the button that takes it from above again. */
function Row({ label, help, own, from, onInherit, children, disabled }: { label: string; help: HelpKey; own: boolean; from: Source; onInherit(): void; children: ReactNode; disabled: Reason | null }) {
  const t = useT();
  const fromText = useFromText();
  return (
    <div className="poi-field">
      <Info help={help} className="field-label">
        {label}
      </Info>
      <span className="poi-field-control">{children}</span>
      {own ? (
        <Button help="help.poi.inherit" variant="link" disabledReason={disabled} onClick={onInherit}>
          {t("poi.inherit")}
        </Button>
      ) : (
        <Info help="help.poi.inherited" className="muted poi-from">
          {fromText(from)}
        </Info>
      )}
    </div>
  );
}

function styleOptions(set: PoiEditSet, bundled: ReturnType<typeof usePoiEditor.getState>["dto"], lang: "en" | "ja", t: ReturnType<typeof useT>) {
  const b = bundled?.bundledStyles ?? [];
  return [
    ...b.map((s) => ({ value: s.id, label: itemName(s.name, lang) || s.id, group: t("poi.styles.bundled") })),
    ...set.styles.map((s) => ({ value: s.id, label: itemName(s.name, lang) || s.id, group: t("poi.styles.project") })),
  ];
}

/** A label with its text in a language set (an empty text takes the language away). */
export function withLabel(label: PoiLabel, lang: string, text: string): PoiLabel {
  const next = { ...label };
  if (text.length > 0) next[lang] = text;
  else delete next[lang];
  return next;
}

/** The values of what is chosen: a folder, a group, a point, or several points. */
function Chosen({ onRemovePoints }: { onRemovePoints(): void }) {
  const t = useT();
  const st = usePoiEditor();
  const sel = st.selection;
  if (sel.points.length > 1 || (sel.points.length > 0 && sel.folders.length + sel.groups.length > 0)) return <ManyPoints onRemove={onRemovePoints} />;
  if (sel.points.length === 1) return <OnePoint pointKey={sel.points[0]} onRemove={onRemovePoints} />;
  if (sel.groups.length === 1 && sel.folders.length === 0) return <FolderOrGroup target={{ kind: "group", path: sel.groups[0] }} />;
  if (sel.folders.length === 1 && sel.groups.length === 0) return <FolderOrGroup target={{ kind: "folder", path: sel.folders[0] }} />;
  return (
    <>
      <Info help="help.poi.chosen" block className="section-title">{t("poi.chosen")}</Info>
      <Info help="help.poi.chosen.none" block className="muted">
        {t("poi.chosen.none")}
      </Info>
    </>
  );
}

function MapsControl({ show, disabled, onChange }: { show: PoiShow; disabled: Reason | null; onChange(s: PoiShow): void }) {
  const t = useT();
  return (
    <span className="poi-maps">
      <Check help="help.poi.maps.atlas" checked={show.atlas} disabledReason={disabled} onChange={(v) => onChange({ ...show, atlas: v })}>
        {t("poi.maps.atlas")}
      </Check>
      <Check help="help.poi.maps.roadmap" checked={show.roadmap} disabledReason={disabled} onChange={(v) => onChange({ ...show, roadmap: v })}>
        {t("poi.maps.roadmap")}
      </Check>
    </span>
  );
}

function FolderOrGroup({ target }: { target: Target & { kind: "folder" | "group" } }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  const [renaming, setRenaming] = useState<NameTarget | null>(null);
  const set = st.set!;
  const isGroup = target.kind === "group";
  const item = isGroup ? set.groups.find((g) => g.path === target.path) : set.folders.find((f) => f.path === target.path);
  if (!item) return null;
  const isRoot = !isGroup && target.path === "";
  const lockedAbove = chainOf(set, isGroup ? parentOf(target.path) : isRoot ? "" : parentOf(target.path)).some((f) => f.locked) && !isRoot;
  const locked: Reason | null = lockedAbove ? "reason.poi.lockedAbove" : null;
  const style = inherited(set, target, "style");
  const show = inherited(set, target, "show");
  // a change of the settings the folders and groups share (and a group's credit)
  const change = (patch: Partial<Pick<PoiEditFolder, "style" | "show" | "visible" | "locked">> & { credit?: string | null }) =>
    st.change(isGroup ? updateGroup(set, target.path, (x) => ({ ...x, ...patch })) : updateFolder(set, target.path, (x) => ({ ...x, ...patch, credit: undefined }) as PoiEditFolder));
  const name = isRoot ? t("poi.top") : isGroup ? groupName(item as never, lang) : folderName(item as never, lang);
  return (
    <>
      <Info help="help.poi.chosen" block className="section-title">{t(isGroup ? "poi.chosen.group" : isRoot ? "poi.chosen.top" : "poi.chosen.folder")}</Info>
      {!isRoot && (
        <div className="poi-field">
          <Info help={isGroup ? "help.poi.group.name" : "help.poi.folder.name"} className="field-label">
            {t("poi.name")}
          </Info>
          <span className="poi-field-control">{name}</span>
          <Button help="help.poi.rename" variant="link" onClick={() => setRenaming(isGroup ? { kind: "group", path: target.path } : { kind: "folder", path: target.path })}>
            {t("poi.rename")}
          </Button>
        </div>
      )}
      <Info help={isGroup ? "help.poi.group.path" : "help.poi.folder.path"} block className="muted">
        {t("poi.path", { path: isGroup ? `${target.path}.json` : isRoot ? "/" : `${target.path}/` })}
      </Info>
      <Row label={t("poi.style")} help="help.poi.style" own={item.style !== null} from={style.from} disabled={locked} onInherit={() => change({ style: null })}>
        <Select help="help.poi.style" value={item.style ?? style.value ?? ""} options={[...(item.style ?? style.value ? [] : [{ value: "", label: t("poi.style.none") }]), ...styleOptions(set, st.dto, lang, t)]}
          disabledReason={locked} onChange={(v) => change({ style: v || null })} />
      </Row>
      <Row label={t("poi.maps")} help="help.poi.maps" own={item.show !== null} from={show.from} disabled={locked} onInherit={() => change({ show: null })}>
        <MapsControl show={item.show ?? show.value ?? DEFAULT_SHOW} disabled={locked} onChange={(s) => change({ show: s })} />
      </Row>
      <div className="poi-field">
        <Info help="help.poi.visible" className="field-label">
          {t("poi.visible")}
        </Info>
        <Check help="help.poi.visible" checked={item.visible} disabledReason={locked} onChange={(v) => change({ visible: v })}>
          {t("poi.visible.on")}
        </Check>
      </div>
      <div className="poi-field">
        <Info help="help.poi.locked" className="field-label">
          {t("poi.locked")}
        </Info>
        <Check help="help.poi.locked" checked={item.locked || lockedAbove} disabledReason={locked} onChange={(v) => change({ locked: v })}>
          {t("poi.locked.on")}
        </Check>
      </div>
      {isGroup && (
        <TextField help="help.poi.credit" label={t("poi.credit")} value={isGroup ? ((item as PoiEditGroup).credit ?? "") : ""}
          onChange={(v) => change({ credit: v.trim() ? v : null })} wide />
      )}
      {renaming && <NameDialog target={renaming} onClose={() => setRenaming(null)} />}
    </>
  );
}

function OnePoint({ pointKey, onRemove }: { pointKey: string; onRemove(): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const labelLang = useLabelLang();
  const japanese = useProjectStore((s) => makesJapanese(s.project!.file));
  const st = usePoiEditor();
  const set = st.set!;
  const [gp, id] = splitKey(pointKey);
  const g = set.groups.find((x) => x.path === gp);
  const p = g?.points.find((x) => x.id === id);
  const r = useMemo(() => (g && p && st.dto ? resolveAll({ ...set, groups: [g] }, st.dto.bundledStyles).find((x) => x.point.id === id) : undefined), [set, g, p, id, st.dto]);
  if (!g || !p || !r) return null;
  const target: Target = { kind: "point", key: pointKey };
  const style = inherited(set, target, "style");
  const show = inherited(set, target, "show");
  const lockedAbove = g.locked || chainOf(set, parentOf(g.path)).some((f) => f.locked);
  const locked: Reason | null = lockedAbove || p.locked ? "reason.poi.locked" : null;
  const change = (f: (x: PoiEditPoint) => PoiEditPoint) => st.change(updatePoints(set, [pointKey], f));
  const styleDefault = r.style;
  const usedColors = { colors: resolveAll(set, st.dto!.bundledStyles).map((x) => x.color) } as unknown as StyleValues;
  return (
    <>
      <Info help="help.poi.chosen" block className="section-title">{t("poi.chosen.point")}</Info>
      <Info help="help.poi.point.group" block className="muted">
        {t("poi.point.group", { name: groupName(g, lang), id: p.id })}
      </Info>
      {locked ? (
        <>
          <div className="poi-field">
            <Info help="help.poi.point.label" className="field-label">
              {t("poi.label")}
            </Info>
            <span className="poi-field-control">{labelIn(p.label, labelLang) || t("poi.noLabel")}</span>
          </div>
          {p.name && (
            <div className="poi-field">
              <Info help="help.poi.point.name" className="field-label">
                {t("poi.name")}
              </Info>
              <span className="poi-field-control">{p.name}</span>
            </div>
          )}
        </>
      ) : (
        <>
          <TextField help={japanese ? "help.poi.point.label.ja" : "help.poi.point.label"} label={t("poi.label")} value={p.label.en ?? ""} onChange={(v) => change((x) => ({ ...x, label: withLabel(x.label, "en", v) }))} wide />
          {japanese && (
            <TextField help="help.poi.point.labelJa" label={t("poi.labelJa")} value={p.label.ja ?? ""} onChange={(v) => change((x) => ({ ...x, label: withLabel(x.label, "ja", v) }))} wide />
          )}
          <TextField help="help.poi.point.name" label={t("poi.name")} value={p.name} onChange={(v) => change((x) => ({ ...x, name: v }))} wide />
        </>
      )}
      <div className="poi-xy">
        <NumberField help="help.poi.point.x" label="X" value={p.x} step={0.01} unit="m" disabledReason={locked} onChange={(v) => v !== null && change((x) => ({ ...x, x: round(v) }))} />
        <NumberField help="help.poi.point.y" label="Y" value={p.y} step={0.01} unit="m" disabledReason={locked} onChange={(v) => v !== null && change((x) => ({ ...x, y: round(v) }))} />
      </div>
      <Row label={t("poi.style")} help="help.poi.style" own={p.style !== null} from={style.from} disabled={locked} onInherit={() => change((x) => ({ ...x, style: null }))}>
        <Select help="help.poi.style" value={p.style ?? style.value ?? ""} options={[...(p.style ?? style.value ? [] : [{ value: "", label: t("poi.style.none") }]), ...styleOptions(set, st.dto, lang, t)]}
          disabledReason={locked} onChange={(v) => change((x) => ({ ...x, style: v || null }))} />
      </Row>
      <Row label={t("poi.maps")} help="help.poi.maps" own={p.show !== null} from={show.from} disabled={locked} onInherit={() => change((x) => ({ ...x, show: null }))}>
        <MapsControl show={p.show ?? show.value ?? DEFAULT_SHOW} disabled={locked} onChange={(s) => change((x) => ({ ...x, show: s }))} />
      </Row>
      <Row label={t("poi.color")} help="help.poi.color" own={p.color !== null} from={{ kind: "default" }} disabled={locked} onInherit={() => change((x) => ({ ...x, color: null }))}>
        <ColorBox value={r.color} values={usedColors} disabled={locked} help="help.poi.color" onSet={(hex) => change((x) => ({ ...x, color: hex }))} />
      </Row>
      <Row label={t("poi.size")} help="help.poi.size" own={p.size !== null} from={{ kind: "default" }} disabled={locked} onInherit={() => change((x) => ({ ...x, size: null }))}>
        <NumberField help="help.poi.size" label="" value={r.size} min={0.1} step={0.1} unit="m" disabledReason={locked} onChange={(v) => v !== null && v > 0 && change((x) => ({ ...x, size: v }))} />
      </Row>
      {styleDefault && (p.color === null || p.size === null) && (
        <Info help="help.poi.fromStyle" block className="muted">
          {t("poi.fromStyle", { name: itemName(styleDefault.name, lang) || styleDefault.id })}
        </Info>
      )}
      <div className="poi-field">
        <Info help="help.poi.visible" className="field-label">
          {t("poi.visible")}
        </Info>
        <Check help="help.poi.visible" checked={p.visible ?? true} disabledReason={locked} onChange={(v) => change((x) => ({ ...x, visible: v ? null : false }))}>
          {t("poi.visible.on")}
        </Check>
        {!r.visible && (p.visible ?? true) && <span className="muted">{t("poi.hiddenAbove")}</span>}
      </div>
      <div className="poi-field">
        <Info help="help.poi.locked" className="field-label">
          {t("poi.locked")}
        </Info>
        <Check help="help.poi.locked" checked={p.locked === true || lockedAbove} disabledReason={lockedAbove ? "reason.poi.lockedAbove" : null}
          onChange={(v) => change((x) => ({ ...x, locked: v ? true : null }))}>
          {t("poi.locked.on")}
        </Check>
      </div>
      <div className="edit-buttons">
        <Button help="help.poi.deletePoints" variant="danger" disabledReason={locked} onClick={onRemove}>
          {t("poi.deletePoints")}
        </Button>
      </div>
    </>
  );
}

function ManyPoints({ onRemove }: { onRemove(): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  const set = st.set!;
  const keys = st.selection.points;
  const [to, setTo] = useState("");
  const all = useMemo(() => resolveAll(set, st.dto!.bundledStyles), [set, st.dto]);
  const chosen = all.filter((r) => keys.includes(r.key));
  const free = chosen.filter((r) => !r.locked).map((r) => r.key);
  const none: Reason | null = free.length === 0 ? "reason.poi.allLocked" : null;
  const groups = orderedGroups(set).filter((g) => !g.locked && !chainOf(set, parentOf(g.path)).some((f) => f.locked));
  const usedColors = { colors: all.map((x) => x.color) } as unknown as StyleValues;
  const change = (f: (x: PoiEditPoint) => PoiEditPoint) => st.change(updatePoints(set, free, f));
  return (
    <>
      <Info help="help.poi.chosen" block className="section-title">{t("poi.chosen.many", { n: keys.length })}</Info>
      {free.length < keys.length && (
        <Info help="help.poi.many.locked" block className="muted">
          {t("poi.many.locked", { n: keys.length - free.length })}
        </Info>
      )}
      <div className="poi-field">
        <Select help="help.poi.moveTo" label={t("poi.moveTo")} value={to} options={[{ value: "", label: t("poi.moveTo.choose") }, ...groups.map((g) => ({ value: g.path, label: groupName(g, lang) }))]} disabledReason={none} onChange={setTo} />
        <Button help="help.poi.moveTo.ok" disabledReason={none ?? (!to ? "reason.poi.noGroupChosen" : null)} onClick={() => {
          const moved = movePointsTo(set, free, to);
          st.change(moved.set, { folders: [], groups: [], points: moved.keys });
        }}>
          {t("poi.moveTo.ok")}
        </Button>
      </div>
      <Select help="help.poi.many.style" label={t("poi.style")} value="" options={[{ value: "", label: t("poi.many.keep") }, { value: INHERIT, label: t("poi.many.inherit") }, ...styleOptions(set, st.dto, lang, t)]}
        disabledReason={none} onChange={(v) => v && change((x) => ({ ...x, style: v === INHERIT ? null : v }))} />
      <div className="poi-field">
        <Info help="help.poi.many.maps" className="field-label">
          {t("poi.maps")}
        </Info>
        <MapsControl show={chosen[0]?.show ?? DEFAULT_SHOW} disabled={none} onChange={(s) => change((x) => ({ ...x, show: s }))} />
      </div>
      <div className="poi-field">
        <Info help="help.poi.many.color" className="field-label">
          {t("poi.color")}
        </Info>
        <ColorBox value={chosen[0]?.color ?? null} values={usedColors} disabled={none} help="help.poi.many.color" onSet={(hex) => change((x) => ({ ...x, color: hex }))} />
        <Button help="help.poi.many.colorInherit" variant="link" disabledReason={none} onClick={() => change((x) => ({ ...x, color: null }))}>
          {t("poi.inherit")}
        </Button>
      </div>
      <div className="edit-buttons">
        <Button help="help.poi.deletePoints" variant="danger" disabledReason={none} onClick={onRemove}>
          {t("poi.deletePoints")}
        </Button>
      </div>
    </>
  );
}
