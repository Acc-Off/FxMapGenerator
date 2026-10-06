import { useEffect, useMemo, useState } from "react";
import { Button, Check, Info, type Reason } from "../../shared/controls";
import { useI18nStore, useT } from "../../shared/i18n";
import { itemName } from "../../shared/types";
import { bundledGroups } from "./model";
import { useRoadEditor } from "./store";

interface Props {
  onClose(): void;
}

/**
 * Taking in the bundled edits: a check box a group with what it would add (groups the edits hold already cannot be
 * chosen), the items left out, and the total of the groups chosen. Taking in is one change of the edits (one undo).
 */
export function BundledDialog({ onClose }: Props) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = useRoadEditor();
  const groups = useMemo(() => (st.base && st.bundled ? bundledGroups(st.base, st.edits, st.bundled) : []), [st.base, st.edits, st.bundled]);
  const open = groups.filter((g) => !g.taken && g.nodes + g.links > 0);
  // every group that can be taken in starts chosen
  const [chosen, setChosen] = useState<Set<string>>(() => new Set(open.map((g) => g.id)));
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  const picked = open.filter((g) => chosen.has(g.id));
  const nodes = picked.reduce((s, g) => s + g.nodes, 0), links = picked.reduce((s, g) => s + g.links, 0);
  const title = t("roads.bundled.title");
  const none: Reason | null = picked.length === 0 ? "reason.roads.bundled.noneChosen" : null;
  const toggle = (id: string, on: boolean) => {
    const next = new Set(chosen);
    if (on) next.add(id);
    else next.delete(id);
    setChosen(next);
  };
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form edit-bundled-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.roads.bundled.dialog" block className="muted">
            {open.length === 0 ? t("roads.bundled.allTaken") : t("roads.bundled.intro", { nodes, links })}
          </Info>
          <div className="edit-bundled-list">
            {groups.map((g) => (
              <div key={g.id} className="edit-bundled-row">
                <Check
                  help="help.roads.bundled.group"
                  checked={!g.taken && chosen.has(g.id) && g.nodes + g.links > 0}
                  disabledReason={g.taken ? "reason.roads.bundled.taken" : g.nodes + g.links === 0 ? "reason.roads.bundled.nothing" : null}
                  onChange={(on) => toggle(g.id, on)}
                >
                  {itemName(g.name, lang)}
                  <span className="muted">
                    {" "}
                    {g.taken ? t("roads.bundled.taken") : g.nodes + g.links === 0 ? t("roads.bundled.nothing") : t("roads.bundled.counts", { nodes: g.nodes, links: g.links })}
                  </span>
                </Check>
                {!g.taken && g.edited + g.mismatch > 0 && (
                  <Info help="help.roads.bundled.left" block className="muted edit-bundled-left">
                    {[g.edited > 0 ? t("roads.bundled.edited", { n: g.edited }) : "", g.mismatch > 0 ? t("roads.bundled.mismatch", { n: g.mismatch }) : ""].filter(Boolean).join(" ")}
                  </Info>
                )}
              </div>
            ))}
          </div>
        </div>
        <div className="modal-foot">
          {open.length === 0 ? (
            <Button help="help.roads.bundled.close" variant="primary" onClick={onClose}>
              {t("common.close")}
            </Button>
          ) : (
            <>
              <Button help="help.roads.bundled.cancel" onClick={onClose}>
                {t("common.cancel")}
              </Button>
              <Button
                help="help.roads.bundled.ok"
                variant="primary"
                disabledReason={none}
                onClick={() => {
                  st.takeIn(picked.map((g) => g.id));
                  onClose();
                }}
              >
                {t("roads.bundled.ok")}
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
