import { useState } from "react";
import { api } from "../shared/api";
import { Button, Info, Radio, TextField } from "../shared/controls";
import { useT } from "../shared/i18n";
import { INPUTS } from "../project/labels";
import { useLock } from "../project/locks";
import { useProjectStore } from "../project/store";

type Kind = "default" | "url" | "file";

const isWeb = (s: string) => /^https?:\/\//i.test(s.trim());

/**
 * Where the atlas maps' postal codes come from: nearest-postal's table (the default), another address, or a file on
 * this PC. Both are lists in nearest-postal's form. The labels step copies the list into the work folder.
 */
export function PostalsDialog({ onClose }: { onClose: () => void }) {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const edit = useProjectStore((s) => s.edit);
  const lock = useLock(INPUTS.postals);
  const current = project.file.postals;
  const [kind, setKind] = useState<Kind>(!current ? "default" : isWeb(current) ? "url" : "file");
  const [url, setUrl] = useState(current && isWeb(current) ? current : "");
  const [file, setFile] = useState(current && !isWeb(current) ? current : "");

  const value = kind === "default" ? "" : kind === "url" ? url.trim() : file.trim();
  const bad = kind === "url" ? !isWeb(url) : kind === "file" ? !file.trim() : false;
  const browse = async () => {
    const r = await api.pickFile(false, file || project.path, t("postals.file"));
    if (r.path) setFile(r.path);
  };
  const save = async () => {
    await edit({ postals: value });
    onClose();
  };

  return (
    <div className="modal-backdrop">
      <div className="modal modal-form" role="dialog" aria-label={t("postals.title")}>
        <div className="modal-head">
          <h2>{t("postals.title")}</h2>
        </div>
        <div className="form">
          <Info help="help.postals.intro" block className="muted">
            {t("postals.intro")}
          </Info>
          <div className="radio-group">
            <Radio group="postals" help="help.postals.default" checked={kind === "default"} onChange={() => setKind("default")}>
              {t("postals.default")}
            </Radio>
            <Radio group="postals" help="help.postals.url" checked={kind === "url"} onChange={() => setKind("url")}>
              {t("postals.url")}
            </Radio>
            {kind === "url" && <TextField help="help.postals.url.field" label={t("postals.url.field")} value={url} onChange={setUrl} placeholder="https://" wide />}
            <Radio group="postals" help="help.postals.file" checked={kind === "file"} onChange={() => setKind("file")}>
              {t("postals.file")}
            </Radio>
            {kind === "file" && (
              <div className="field-row">
                <TextField help="help.postals.file.field" label={t("postals.file.field")} value={file} onChange={setFile} wide />
                <Button help="help.postals.file.browse" onClick={() => void browse()}>
                  {t("common.browse")}
                </Button>
              </div>
            )}
          </div>
        </div>
        <div className="modal-foot">
          <Button help="help.postals.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            help="help.postals.save"
            variant="primary"
            disabledReason={lock ?? (bad ? (kind === "url" ? "reason.badUrl" : "reason.emptyPath") : null)}
            onClick={() => void save()}
          >
            {t("common.save")}
          </Button>
        </div>
      </div>
    </div>
  );
}
