import { useEffect, useMemo, useState } from "react";
import { api } from "../../shared/api";
import { Button, FileButton, Info, type Reason, Select, Splitter } from "../../shared/controls";
import { type MessageKey, t as translate, useI18nStore, useT } from "../../shared/i18n";
import type { StyleRebuild } from "../../shared/types";
import { mapName } from "../../project/labels";
import { useProjectStore } from "../../project/store";
import { nameOf, rebuildsOf } from "./schema";
import { DuplicateDialog, ImportIdDialog, RenameDialog } from "./StyleDialogs";
import { StyleList } from "./StyleList";
import { StylePreviewPane } from "./StylePreviewPane";
import { useStyleEditor } from "./store";

/** The width of the list of values (kept while the app is open). */
const LIST_WIDTH = 600;
let listWidth = LIST_WIDTH;
const clampWidth = (w: number) => Math.max(420, Math.min(1000, w));

/** The steps saving can rebuild, in the project screen's order, with their names there. */
const REBUILD_NAMES: readonly (readonly [StyleRebuild, MessageKey])[] = [
  ["cells.prep", "row.cells.prep"],
  ["mapData.regions", "row.mapData.regions"],
  ["mapData.labels", "row.mapData.labels"],
];

/**
 * Whether the style editor may be left: unsaved changes of a style are dropped only when the user agrees (the editing
 * tab asks before it changes tabs or closes the project, and before another style is shown).
 */
export function mayLeaveStyle(): boolean {
  const s = useStyleEditor.getState();
  if (!s.dirty()) return true;
  if (!window.confirm(translate("styles.leave"))) return false;
  s.discard();
  return true;
}

/** A field that takes typed keys: the editor's keys are not for it (a check box or a slider is not). */
function typing(target: EventTarget | null): boolean {
  if (target instanceof HTMLInputElement) return !["checkbox", "radio", "range", "button"].includes(target.type);
  return target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || (target instanceof HTMLElement && target.isContentEditable);
}

/**
 * The style editor (the editing tab's "styles"): on top the style shown and what can be done with it
 * (make a style from it, rename, delete, export, import, save as a shared style; undo, redo, save); on the left its
 * values by group, each with its control; the right is the place of the preview. The bundled styles are shown, never
 * changed: a style of one's own is made from one first.
 */
export function StyleEditor() {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const project = useProjectStore((s) => s.project)!;
  const st = useStyleEditor();
  const [dialog, setDialog] = useState<null | "duplicate" | "rename" | { file: string; id: string; name: string }>(null);
  const [width, setWidth] = useState(listWidth);

  useEffect(() => {
    void useStyleEditor.getState().load(project.path);
  }, [project.path]);

  // Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) undo and redo, but not while typing into a field, with a window open or while a
  // guide is shown
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || typing(e.target) || document.querySelector(".modal-backdrop, .color-picker, .driver-active")) return;
      const s = useStyleEditor.getState();
      if (e.key === "z" || e.key === "Z") {
        e.preventDefault();
        if (e.shiftKey) s.redo();
        else s.undo();
      } else if (e.key === "y" || e.key === "Y") {
        e.preventDefault();
        s.redo();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const list = st.list?.styles ?? [];
  const cur = st.current;
  const values = st.edited ?? cur?.values ?? null;
  const dirty = st.dirty();
  const rebuilds = useMemo(() => (st.schema && cur && st.edited ? rebuildsOf(st.schema, cur.values, st.edited) : new Set<StyleRebuild>()), [st.schema, cur, st.edited]);
  const options = list.map((s) => ({
    value: s.id,
    label: s.bundled ? nameOf(s.name, lang) : `${nameOf(s.name, lang)}（${s.id}）`,
    group: t(s.bundled ? "styles.group.bundled" : "styles.group.project"),
  }));
  const bundled: Reason | null = cur?.bundled ? "reason.styles.bundled" : null;
  const inUse: Reason | null = cur && cur.maps.length > 0 ? { key: "reason.styles.inUse", params: { maps: cur.maps.map((m) => mapName(t, m)).join("、") } } : null;
  const unreadable: Reason | null = cur?.problem ? "reason.styles.unreadable" : null;
  const unsaved: Reason | null = dirty ? "reason.styles.unsaved" : null;

  const importFile = async (file: File) => {
    const text = await file.text();
    const r = await st.importFile(text);
    if (r === "ok") st.setNotice(t("styles.imported", { id: useStyleEditor.getState().current?.id ?? "" }));
    else if (r === "ID_TAKEN") {
      // the file's own id and name, to ask for another id
      let id = "", name = file.name;
      try {
        const o = JSON.parse(text) as { id?: unknown; name?: { en?: unknown; ja?: unknown } };
        if (typeof o.id === "string") id = o.id;
        const n = lang === "ja" && typeof o.name?.ja === "string" && o.name.ja ? o.name.ja : o.name?.en;
        if (typeof n === "string" && n) name = n;
      } catch {
        // the server read it; the page only wants its names
      }
      setDialog({ file: text, id, name });
    }
  };
  const remove = async () => {
    if (!cur || !window.confirm(t("styles.delete.confirm", { name: nameOf(cur.name, lang), id: cur.id }))) return;
    if (await st.remove()) st.setNotice(t("styles.deleted", { id: cur.id }));
  };
  const share = async () => {
    if (!cur) return;
    let r = await st.share(false);
    if (r === "EXISTS" && window.confirm(t("styles.shared.confirm", { id: cur.id }))) r = await st.share(true);
    if (r === "ok") st.setNotice(t("styles.shared.done", { id: cur.id }));
  };
  const exportFile = () => {
    if (!cur) return;
    const a = document.createElement("a");
    a.href = api.styleFileUrl(cur.id);
    a.download = `${cur.id}.json`;
    document.body.appendChild(a);
    a.click();
    a.remove();
  };
  const choose = (id: string) => {
    if (id === cur?.id || !mayLeaveStyle()) return;
    void st.choose(id);
  };
  const save = async () => {
    if (await st.save()) st.setNotice(t("styles.saved", { id: cur?.id ?? "" }));
  };

  if (!st.list || !st.schema) {
    return (
      <div className="edit-unavailable">
        <Info help="help.styles.loading" block className={st.error ? "text-error" : "muted"}>
          {st.error ?? t("styles.loading")}
        </Info>
      </div>
    );
  }

  // what saving rebuilds (the maps that use the style; the cells are drawn again whatever changed but the credit)
  const saveLine = !dirty || !cur ? null
    : cur.maps.length === 0 ? t("styles.save.noMaps")
    : rebuilds.size === 0 ? t("styles.save.none")
    : t("styles.save.line", {
        list: [
          ...(rebuilds.has("cells") ? [t("styles.save.cells", { n: cur.maps.length })] : []),
          ...REBUILD_NAMES.filter(([id]) => rebuilds.has(id)).map(([, key]) => t(key)),
        ].join("・"),
      });
  const baseName = cur?.base ? nameOf(list.find((s) => s.id === cur.base)?.name, lang) || cur.base : null;
  return (
    <div className="style-editor">
      <div className="style-bar">
        <Select help="help.styles.choose" label={t("styles.choose")} value={cur?.id ?? list[0].id} options={options} onChange={choose} className="style-choose" />
        <div className="edit-buttons">
          <Button help="help.styles.duplicate" disabledReason={unsaved} onClick={() => setDialog("duplicate")}>
            {t("styles.duplicate")}
          </Button>
          <Button help="help.styles.rename" disabledReason={bundled ?? unreadable ?? unsaved} onClick={() => setDialog("rename")}>
            {t("styles.rename")}
          </Button>
          <Button help="help.styles.delete" disabledReason={bundled ?? inUse ?? unsaved} onClick={() => void remove()}>
            {t("styles.delete")}
          </Button>
        </div>
        <div className="edit-buttons">
          <Button help="help.styles.export" disabledReason={unreadable ?? unsaved} onClick={exportFile}>
            {t("styles.export")}
          </Button>
          <FileButton help="help.styles.import" accept=".json,application/json" disabledReason={unsaved} onFile={(f) => void importFile(f)}>
            {t("styles.import")}
          </FileButton>
          <Button help="help.styles.share" disabledReason={bundled ?? unreadable ?? unsaved} onClick={() => void share()}>
            {t("styles.share")}
          </Button>
        </div>
        <div className="edit-buttons style-save-buttons">
          <Button help="help.styles.undo" disabledReason={bundled ?? (st.past.length === 0 ? "reason.styles.noUndo" : null)} onClick={st.undo}>
            {t("styles.undo")}
          </Button>
          <Button help="help.styles.redo" disabledReason={bundled ?? (st.future.length === 0 ? "reason.styles.noRedo" : null)} onClick={st.redo}>
            {t("styles.redo")}
          </Button>
          <Button help="help.styles.save" variant="primary" disabledReason={bundled ?? unreadable ?? (st.saving ? "reason.styles.saving" : !dirty ? "reason.styles.nothingToSave" : null)} onClick={() => void save()}>
            {t("styles.save")}
          </Button>
        </div>
      </div>
      <div className="style-about">
        {cur?.bundled ? (
          <Info help="help.styles.bundledNote" className="style-bundled-note">
            {t("styles.bundledNote")}
          </Info>
        ) : cur ? (
          <>
            {baseName && (
              <Info help="help.styles.base" className="muted">
                {t("styles.base", { name: baseName })}
              </Info>
            )}
            <Info help="help.styles.file" className="muted">
              {t("styles.file", { file: `${st.list.folder ?? "styles"}/${cur.id}.json` })}
            </Info>
          </>
        ) : null}
        {cur && (
          <Info help="help.styles.maps" className="muted">
            {cur.maps.length > 0 ? t("styles.maps", { maps: cur.maps.map((m) => mapName(t, m)).join("、") }) : t("styles.maps.none")}
          </Info>
        )}
        {dirty && (
          <Info help="help.styles.unsaved" className="edit-unsaved style-unsaved">
            {t("styles.unsaved")}
          </Info>
        )}
        {saveLine && (
          <Info help="help.styles.saveLine" className="style-save-line">
            {saveLine}
          </Info>
        )}
      </div>
      {st.notice && (
        <div className="banner">
          {st.notice}
          <Button help="help.styles.notice.close" variant="link" onClick={() => st.setNotice(null)}>
            {t("common.close")}
          </Button>
        </div>
      )}
      {st.error && (
        <div className="banner banner-error">
          {st.error}
          <Button help="help.styles.error.close" variant="link" onClick={st.clearError}>
            {t("common.close")}
          </Button>
        </div>
      )}
      <div className="style-body" style={{ gridTemplateColumns: `${width}px 6px minmax(0, 1fr)` }}>
        {cur?.problem ? (
          <Info help="help.styles.problem" block className="text-error style-problem">
            {t("styles.problem", { message: cur.problem })}
          </Info>
        ) : cur && values ? (
          <StyleList
            schema={st.schema}
            values={values}
            base={cur.baseValues}
            disabled={bundled}
            onChange={(next, key) => st.change(next, key)}
            onRevert={(item) => st.revert(item)}
            zoneNames={st.list.zoneNames ?? {}}
          />
        ) : (
          <div />
        )}
        <Splitter
          help="help.styles.splitter"
          panel="left"
          width={width}
          defaultWidth={LIST_WIDTH}
          onResize={(w) => setWidth(clampWidth(w))}
          onCommit={(w) => {
            listWidth = clampWidth(w);
            setWidth(listWidth);
          }}
        />
        {cur && values && !cur.problem && st.schema ? (
          <StylePreviewPane schema={st.schema} saved={cur.values} base={cur.baseValues} edited={values} />
        ) : (
          <div className="style-preview" />
        )}
      </div>
      {dialog === "duplicate" && <DuplicateDialog onClose={() => setDialog(null)} />}
      {dialog === "rename" && <RenameDialog onClose={() => setDialog(null)} />}
      {dialog && typeof dialog === "object" && <ImportIdDialog file={dialog.file} fileId={dialog.id} fileName={dialog.name} onClose={() => setDialog(null)} />}
    </div>
  );
}
