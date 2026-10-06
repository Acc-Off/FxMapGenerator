import { useEffect, useRef, useState } from "react";
import { api, ApiError } from "../shared/api";
import { Button, Check, Info, Select, TextField, type Reason } from "../shared/controls";
import { errorText, useT, type MessageKey } from "../shared/i18n";
import type { ConvertChoices, ConvertInfo, ExportInfo, ExportProblem, PictureInfo } from "../shared/types";
import { mapName } from "../project/labels";
import { useProjectStore } from "../project/store";

const CHECK_DELAY_MS = 300;

/** Why a file is not a picture that can be converted (Core/Export/PictureConvert.cs, PictureInfo.Problem). */
const PICTURE_PROBLEMS: Record<NonNullable<PictureInfo["problem"]>, MessageKey> = {
  NOT_FOUND: "export.convert.problem.NOT_FOUND",
  NOT_PNG: "export.convert.problem.NOT_PNG",
  INTERLACED: "export.convert.problem.INTERLACED",
  BAD_SIZE: "export.convert.problem.BAD_SIZE",
};

interface Props {
  /** The export screen's information: the frame's blocks (the pictures' sizes) and the picture last converted. */
  info: ExportInfo;
  /** The output folder and the address of the web tiles as the screen has them now. */
  folder: string | null;
  baseUrl: string | null;
  /** Why no run can start now (another one is going on), or null. */
  busy: Reason | null;
  /** A problem of the export's kind in the screen's words. */
  problemText: (p: ExportProblem) => string;
  onError: (message: string | null) => void;
}

/**
 * The export screen's group "Convert an edited picture": a PNG picture written from an edited layered file becomes web
 * tiles and a minimap resource of its own (Core/Export/ConvertStages.cs). The picture, the map it was made from, the
 * outputs to write and the button that starts it; the project's maps and the export above are not touched.
 */
export function ConvertPicture({ info, folder, baseUrl, busy, problemText, onError }: Props) {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const [file, setFile] = useState(info.picture.file ?? "");
  const [map, setMap] = useState(info.picture.map ?? "");
  const [tiles, setTiles] = useState(true);
  const [minimap, setMinimap] = useState(true);
  const [check, setCheck] = useState<ConvertInfo | null>(null);
  const timer = useRef(0);
  const guess = useRef(false);

  const choices = (patch: Partial<ConvertChoices> = {}): ConvertChoices => ({ folder, file: file || null, map: map || null, tiles, minimap, baseUrl, ...patch });

  // the picture as it is now, whenever a choice changes (a new picture's name may tell its map)
  useEffect(() => {
    window.clearTimeout(timer.current);
    if (!file.trim()) {
      setCheck(null);
      return;
    }
    const sent = choices(guess.current ? { map: null } : {});
    timer.current = window.setTimeout(
      () =>
        void api.checkConvert(sent).then(
          (c) => {
            setCheck(c);
            if (guess.current && c.guessedMap) setMap(c.guessedMap);
            guess.current = false;
          },
          (e) => onError(message(e)),
        ),
      CHECK_DELAY_MS,
    );
    return () => window.clearTimeout(timer.current);
  }, [file, map, tiles, minimap, folder, baseUrl, project.path]);

  const changeFile = (path: string) => {
    guess.current = true;
    setFile(path);
  };

  const pick = async () => {
    const r = await api.pickFile(false, file || folder || project.path, t("export.convert.file"), "picture");
    if (r.path) changeFile(r.path);
  };

  const start = async () => {
    onError(null);
    try {
      useProjectStore.getState().onJob(await api.startConvert(choices()));
    } catch (e) {
      onError(message(e));
    }
  };

  const size = (zoom: number) => ({
    width: (info.editable.blocksX * 256 * 2 ** (zoom - 6)).toLocaleString(),
    height: (info.editable.blocksY * 256 * 2 ** (zoom - 6)).toLocaleString(),
  });
  const picture = check?.picture ?? null;
  const others = (check?.problems ?? []).filter((p) => p.code !== "BAD_PICTURE");
  const reason: Reason | null = busy ?? (!file.trim() ? "reason.convert.noPicture" : !check || check.problems.length > 0 ? "reason.convert.problems" : null);
  const maps = project.maps.map((m) => ({ value: m, label: mapName(t, m) }));

  return (
    <div className="export-group export-convert" data-guide="export.convert">
      <Info help="help.export.convert" block className="section-title">
        {t("export.convert")}
      </Info>
      <div className="field-row">
        <TextField help="help.export.convert.file" label={t("export.convert.file")} value={file} wide onChange={changeFile} />
        <Button help="help.export.convert.browse" onClick={() => void pick()}>
          {t("common.browse")}
        </Button>
      </div>
      {picture &&
        (picture.problem ? (
          <Info help="help.export.convert.problem" block className="text-error">
            {t(PICTURE_PROBLEMS[picture.problem], {
              width: picture.width.toLocaleString(),
              height: picture.height.toLocaleString(),
              w6: size(6).width,
              h6: size(6).height,
              w7: size(7).width,
              h7: size(7).height,
            })}
          </Info>
        ) : (
          <Info help="help.export.convert.size" block className="muted">
            {t("export.convert.size", { width: picture.width.toLocaleString(), height: picture.height.toLocaleString(), zoom: picture.zoom ?? 0 })}
          </Info>
        ))}
      <div className="field-row">
        <Select help="help.export.convert.map" label={t("export.convert.map")} value={map} options={maps} onChange={setMap} />
      </div>
      <Info help="help.export.convert.outputs" block className="field-label">
        {t("export.convert.outputs")}
      </Info>
      <div className="export-outputs">
        <Check help="help.export.convert.tiles" checked={tiles} onChange={setTiles}>
          {t("export.web")}
        </Check>
        <Check help="help.export.convert.minimap" checked={minimap} onChange={setMinimap}>
          {t("export.convert.minimap")}
        </Check>
      </div>
      {file.trim() && others.length > 0 && (
        <ul className="export-problems">
          {others.map((p) => (
            <li key={p.code + p.message}>
              <Info help="help.export.problem" className="text-error">
                {p.code === "NOTHING" ? t("export.convert.problem.NOTHING") : problemText(p)}
              </Info>
            </li>
          ))}
        </ul>
      )}
      <div className="export-go">
        <span className="spacer" />
        <Button help="help.export.convert.start" variant="primary" disabledReason={reason} onClick={() => void start()}>
          {t("export.convert.start")}
        </Button>
      </div>
    </div>
  );
}

function message(e: unknown): string {
  return e instanceof ApiError ? errorText(e.code, e.message) : e instanceof Error ? e.message : String(e);
}
