import { useEffect, useMemo, useState } from "react";
import { Button, Info, type Reason, Select, TextField } from "../../shared/controls";
import { useI18nStore, useT } from "../../shared/i18n";
import { nameOf } from "./schema";
import { useStyleEditor } from "./store";

/** The form of a style's id (its file name): small letters and digits, a letter first, at most 32. */
const ID = /^[a-z][a-z0-9]{0,31}$/;

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

/** An id free in the project, made from a name: its letters and digits in small letters, a number added while taken. */
export function freeId(name: string, taken: ReadonlySet<string>): string {
  let stem = name.toLowerCase().replace(/[^a-z0-9]/g, "");
  stem = stem.replace(/^[0-9]+/, "");
  if (!stem) stem = "style";
  stem = stem.slice(0, 28);
  if (!taken.has(stem)) return stem;
  for (let n = 2; ; n++) if (!taken.has(`${stem}${n}`)) return `${stem}${n}`;
}

/** Why an id cannot be used, or null. */
function idReason(id: string, taken: ReadonlySet<string>): Reason | null {
  if (!id) return "reason.styles.noId";
  if (!ID.test(id)) return "reason.styles.badId";
  if (taken.has(id)) return "reason.styles.idTaken";
  return null;
}

/** A style's name: one text (the exported maps drawn with it are named with it as written). */
function NameField({ name, onName }: { name: string; onName(v: string): void }) {
  const t = useT();
  return <TextField help="help.styles.name" label={t("styles.name")} value={name} onChange={onName} />;
}

/**
 * Makes a project style from another: a bundled style, one of the project's, or a shared one. Its name can change any
 * time (it starts as the copy's name in the screen's language); its file name (the id) is chosen here once.
 */
export function DuplicateDialog({ onClose }: { onClose(): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = useStyleEditor();
  useEscape(onClose);
  useEffect(() => {
    void useStyleEditor.getState().loadShared();
  }, []);
  const styles = st.list?.styles ?? [];
  const taken = useMemo(() => new Set(styles.map((s) => s.id)), [styles]);
  const options = [
    ...styles.filter((s) => s.bundled).map((s) => ({ value: `b:${s.id}`, label: nameOf(s.name, lang), group: t("styles.group.bundled") })),
    ...styles.filter((s) => !s.bundled && !s.problem).map((s) => ({ value: `p:${s.id}`, label: `${nameOf(s.name, lang)}（${s.id}）`, group: t("styles.group.project") })),
    ...(st.shared ?? []).filter((s) => !s.problem).map((s) => ({ value: `s:${s.id}`, label: `${nameOf(s.name, lang)}（${s.id}）`, group: t("styles.group.shared") })),
  ];
  const cur = st.current;
  const [from, setFrom] = useState(cur ? `${cur.bundled ? "b" : "p"}:${cur.id}` : options[0]?.value ?? "");
  const source = [...styles, ...(st.shared ?? [])].find((s) => s.id === from.slice(2));
  const [name, setName] = useState(() => (cur ? t("styles.copyOf", { name: nameOf(cur.name, lang) }) : ""));
  const [id, setId] = useState(() => freeId(cur ? `${cur.id}` : "style", taken));
  const [takenNow, setTakenNow] = useState<Set<string>>(new Set());
  const allTaken = useMemo(() => new Set([...taken, ...takenNow]), [taken, takenNow]);
  const reason: Reason | null = !name.trim() ? "reason.styles.noName" : idReason(id, allTaken);
  const title = t("styles.duplicate.title");
  const ok = async () => {
    const r = await st.create(from.slice(2), from.startsWith("s:"), id, name.trim());
    if (r === "ID_TAKEN") setTakenNow(new Set([...takenNow, id]));
    else if (r === "ok") {
      st.setNotice(t("styles.made", { name: name.trim(), id }));
      onClose();
    }
  };
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form style-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.styles.duplicate.dialog" block className="muted">
            {t("styles.duplicate.intro")}
          </Info>
          <Select help="help.styles.duplicate.from" label={t("styles.duplicate.from")} value={from} options={options} onChange={setFrom} />
          {source && !source.bundled && source.base && (
            <Info help="help.styles.base" block className="muted">
              {t("styles.base", { name: nameOf(styles.find((s) => s.id === source.base)?.name, lang) || source.base })}
            </Info>
          )}
          <NameField name={name} onName={setName} />
          <TextField help="help.styles.id" label={t("styles.id")} value={id} onChange={(v) => setId(v.trim())} />
          {takenNow.has(id) && (
            <Info help="help.styles.idTaken" block className="text-error">
              {t("styles.idTaken")}
            </Info>
          )}
        </div>
        <div className="modal-foot">
          <Button help="help.styles.dialog.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.styles.duplicate.ok" variant="primary" disabledReason={reason} onClick={() => void ok()}>
            {t("styles.duplicate.ok")}
          </Button>
        </div>
      </div>
    </div>
  );
}

/** Another name for a project style (its file name stays). */
export function RenameDialog({ onClose }: { onClose(): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = useStyleEditor();
  useEscape(onClose);
  const cur = st.current!;
  const [name, setName] = useState(nameOf(cur.name, lang));
  const title = t("styles.rename.title");
  const ok = async () => {
    if (await st.rename(name.trim())) onClose();
  };
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form style-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.styles.rename.dialog" block className="muted">
            {t("styles.rename.intro", { id: cur.id })}
          </Info>
          <NameField name={name} onName={setName} />
        </div>
        <div className="modal-foot">
          <Button help="help.styles.dialog.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.styles.rename.ok" variant="primary" disabledReason={!name.trim() ? "reason.styles.noName" : null} onClick={() => void ok()}>
            {t("styles.rename.ok")}
          </Button>
        </div>
      </div>
    </div>
  );
}

/** A style file whose file name the project has already: read in under another. */
export function ImportIdDialog({ file, fileId, fileName, onClose }: { file: string; fileId: string; fileName: string; onClose(): void }) {
  const t = useT();
  const st = useStyleEditor();
  useEscape(onClose);
  const taken = useMemo(() => new Set((st.list?.styles ?? []).map((s) => s.id)), [st.list]);
  const [id, setId] = useState(() => freeId(fileId || fileName, taken));
  const [takenNow, setTakenNow] = useState<Set<string>>(new Set());
  const allTaken = useMemo(() => new Set([...taken, ...takenNow]), [taken, takenNow]);
  const title = t("styles.importId.title");
  const ok = async () => {
    const r = await st.importFile(file, id);
    if (r === "ID_TAKEN") setTakenNow(new Set([...takenNow, id]));
    else if (r === "ok") {
      st.setNotice(t("styles.imported", { id }));
      onClose();
    } else onClose();
  };
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form style-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.styles.importId.dialog" block className="muted">
            {t("styles.importId.intro", { name: fileName, id: fileId })}
          </Info>
          <TextField help="help.styles.id" label={t("styles.id")} value={id} onChange={(v) => setId(v.trim())} />
        </div>
        <div className="modal-foot">
          <Button help="help.styles.dialog.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.styles.importId.ok" variant="primary" disabledReason={idReason(id, allTaken)} onClick={() => void ok()}>
            {t("styles.importId.ok")}
          </Button>
        </div>
      </div>
    </div>
  );
}
