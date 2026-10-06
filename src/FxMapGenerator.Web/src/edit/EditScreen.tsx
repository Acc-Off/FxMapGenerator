import { useEffect } from "react";
import { create } from "zustand";
import { Button, Info, type HelpKey } from "../shared/controls";
import { type MessageKey, t as translate, useT } from "../shared/i18n";
import { useProjectStore } from "../project/store";
import "./edit.css";
import { PoiEditor } from "./poi/PoiEditor";
import { usePoiEditor } from "./poi/store";
import { GameFilesRead } from "./roads/GameFilesRead";
import { RoadEditor } from "./roads/RoadEditor";
import { useRoadEditor } from "./roads/store";
import { StyleEditor } from "./styles/StyleEditor";
import { useStyleEditor } from "./styles/store";

/** The kinds of data the editing tab edits. */
export type EditKind = "roads" | "poi" | "styles";

/** The kind shown (the last one chosen, kept while the app is open; the guides read it too). */
export const useEditKind = create<{ kind: EditKind; setKind(kind: EditKind): void }>((set) => ({
  kind: "roads",
  setKind: (kind) => set({ kind }),
}));

const KINDS: readonly (readonly [EditKind, MessageKey, HelpKey])[] = [
  ["roads", "edit.roads", "help.edit.roads"],
  ["poi", "edit.poi", "help.edit.poi"],
  ["styles", "edit.styles", "help.edit.styles"],
];

/**
 * Whether the editing tab may be left: unsaved road edits, style changes and points of interest are dropped only when
 * the user agrees. The project screen asks before it changes tabs or closes the project.
 */
export function mayLeaveEditing(): boolean {
  const r = useRoadEditor.getState();
  const s = useStyleEditor.getState();
  const p = usePoiEditor.getState();
  const roads = !!r.base && r.dirty(), styles = s.dirty(), poi = p.dirty();
  const kinds = [roads, poi, styles].filter(Boolean).length;
  if (kinds === 0) return true;
  const question = kinds > 1
    ? translate("edit.leave.several", { list: [roads ? translate("edit.roads") : null, poi ? translate("edit.poi") : null, styles ? translate("edit.styles") : null].filter(Boolean).join(translate("edit.leave.join")) })
    : translate(roads ? "roads.leave" : poi ? "poi.leave" : "styles.leave");
  if (!window.confirm(question)) return false;
  if (roads) {
    // what was saved comes back; the undo list goes
    r.change(r.saved);
    useRoadEditor.setState({ past: [], future: [] });
  }
  if (styles) s.discard();
  if (poi) p.discard();
  return true;
}

/** The editing tab: a switch between the kinds of data, and the screen of the kind chosen. */
export function EditScreen() {
  const t = useT();
  const kind = useEditKind((s) => s.kind);
  const setKind = useEditKind((s) => s.setKind);
  const status = useRoadEditor((s) => s.status);
  const loading = useRoadEditor((s) => s.loading);
  const error = useRoadEditor((s) => s.error);
  const project = useProjectStore((s) => s.project)!;
  const setScreen = useProjectStore((s) => s.setScreen);

  // unsaved edits also hold the browser tab
  useEffect(() => {
    const before = (e: BeforeUnloadEvent) => {
      const s = useRoadEditor.getState();
      if ((s.base && s.dirty()) || useStyleEditor.getState().dirty() || usePoiEditor.getState().dirty()) e.preventDefault();
    };
    window.addEventListener("beforeunload", before);
    return () => window.removeEventListener("beforeunload", before);
  }, []);
  // read again when the project's maps or edits file change (the editor may become usable, or not)
  const mapsKey = JSON.stringify(project.file.maps) + "|" + (project.file.roadEdits ?? "");
  useEffect(() => {
    void useRoadEditor.getState().load(project.path);
  }, [project.path, mapsKey]);

  const unavailable = status?.unavailable ?? null;
  return (
    <div className="edit-screen">
      <div className="edit-switch">
        {KINDS.map(([id, label, help]) => (
          <Button key={id} help={help} className="tab" pressed={kind === id} onClick={() => setKind(id)}>
            {t(label)}
          </Button>
        ))}
      </div>
      {kind === "styles" ? (
        <StyleEditor />
      ) : kind === "poi" ? (
        <PoiEditor />
      ) : unavailable ? (
        <div className="edit-unavailable">
          <Info help="help.roads.unavailable" block>
            {t(unavailable === "noRoadMaps" ? "roads.unavailable.noRoadMaps" : "roads.unavailable.noGameFiles")}
          </Info>
          {unavailable === "noRoadMaps" ? (
            <Button help="help.roads.toSettings" variant="primary" onClick={() => setScreen("settings")}>
              {t("roads.toSettings")}
            </Button>
          ) : (
            <GameFilesRead primary />
          )}
        </div>
      ) : !status ? (
        <div className="edit-unavailable">
          <Info help="help.roads.loading" block className={error ? "text-error" : "muted"}>
            {error ?? (loading ? t("roads.loading") : "")}
          </Info>
        </div>
      ) : (
        <RoadEditor />
      )}
    </div>
  );
}
