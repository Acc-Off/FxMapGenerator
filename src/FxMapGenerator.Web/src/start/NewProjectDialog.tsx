import { useState } from "react";
import { api, ApiError } from "../shared/api";
import { Button, Check, Select, TextField } from "../shared/controls";
import { errorText, useI18nStore, useT } from "../shared/i18n";
import type { ServerPreset } from "../shared/types";

/** New project: name, project file, work folder (empty = next to the file), server preset, maps. All can change later. */
export function NewProjectDialog({ onClose }: { onClose: () => void }) {
  const t = useT();
  const [name, setName] = useState("");
  const [path, setPath] = useState("");
  const [work, setWork] = useState("");
  const [preset, setPreset] = useState<ServerPreset>("qbox");
  const [satellite, setSatellite] = useState(true);
  const [atlas, setAtlas] = useState(false);
  const [roadmap, setRoadmap] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const browseFile = async () => {
    const r = await api.pickFile(true, path || (name ? `${name}.fxmapgen.json` : undefined), t("new.path"));
    if (r.path) {
      setPath(r.path);
      if (!name) setName(r.path.replace(/^.*[\\/]/, "").replace(/\.fxmapgen\.json$/i, "").replace(/\.json$/i, ""));
    }
  };
  const browseWork = async () => {
    const r = await api.pickFolder(work || undefined, t("new.work"));
    if (r.path) setWork(r.path);
  };
  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.newProject({ path, name, workFolder: work, preset, satellite, atlas, roadmap, language: useI18nStore.getState().lang });
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
    } finally {
      setBusy(false);
    }
  };

  const presets: { value: ServerPreset; label: string }[] = [
    { value: "qbox", label: t("preset.qbox") },
    { value: "qbcore", label: t("preset.qbcore") },
  ];

  return (
    <div className="modal-backdrop">
      <div className="modal modal-form" role="dialog" aria-label={t("new.title")}>
        <div className="modal-head">
          <h2>{t("new.title")}</h2>
        </div>
        <div className="form">
          <TextField help="help.new.name" label={t("new.name")} value={name} onChange={setName} />
          <div className="field-row">
            <TextField help="help.new.path" label={t("new.path")} value={path} onChange={setPath} wide />
            <Button help="help.new.path.browse" onClick={() => void browseFile()}>
              {t("common.browse")}
            </Button>
          </div>
          <div className="field-row">
            <TextField help="help.new.work" label={t("new.work")} value={work} onChange={setWork} placeholder={t("new.work.placeholder")} wide />
            <Button help="help.new.work.browse" onClick={() => void browseWork()}>
              {t("common.browse")}
            </Button>
          </div>
          <Select help="help.new.preset" label={t("new.preset")} value={preset} options={presets} onChange={setPreset} />
          <div className="form-maps">
            <span className="field-label">{t("panel.maps")}</span>
            <Check help="help.map.satellite" checked={satellite} onChange={setSatellite}>
              {t("map.satellite")}
            </Check>
            <Check help="help.map.atlas" checked={atlas} onChange={setAtlas}>
              {t("map.atlas")}
            </Check>
            <Check help="help.map.roadmap" checked={roadmap} onChange={setRoadmap}>
              {t("map.roadmap")}
            </Check>
          </div>
          {error && <div className="text-error">{error}</div>}
        </div>
        <div className="modal-foot">
          <Button help="help.new.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.new.create" variant="primary" disabledReason={!path.trim() ? "reason.pathRequired" : busy ? "reason.busy" : null} onClick={() => void create()}>
            {t("new.create")}
          </Button>
        </div>
      </div>
    </div>
  );
}
