import { useEffect, useState } from "react";
import { Button, Info, type Reason, TextField } from "../../shared/controls";
import { useT } from "../../shared/i18n";
import type { AddedStreetName } from "../../shared/types";

interface Props {
  /** The name whose words change, or null for a new name. */
  name: AddedStreetName | null;
  /** The project makes Japanese maps: the Japanese name is asked for too. */
  japanese: boolean;
  /** English names already there, in lower case: the game's, and the others the edits add. */
  gameNames: ReadonlySet<string>;
  addedNames: ReadonlySet<string>;
  onClose(): void;
  onDone(name: AddedStreetName): void;
}

/**
 * A street name of one's own: the English name (the English maps, and what the roads make of a name) and, when the
 * project makes Japanese maps, the Japanese one (the Japanese maps; empty: the English one there too). A name the game
 * or the edits have already is chosen from the list instead.
 */
export function StreetNameDialog({ name, japanese, gameNames, addedNames, onClose, onDone }: Props) {
  const t = useT();
  const [en, setEn] = useState(name?.en ?? "");
  const [ja, setJa] = useState(name?.ja ?? "");
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  const key = en.trim().toLowerCase();
  const reason: Reason | null = !key ? (japanese ? "reason.roads.street.noEnglish" : "reason.roads.street.noName")
    : gameNames.has(key) ? "reason.roads.street.gameName"
    : addedNames.has(key) ? "reason.roads.street.addedName"
    : null;
  const title = t(name ? "roads.street.editTitle" : "roads.street.addTitle");
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form edit-street-dialog" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.roads.street.dialog" block className="muted">
            {japanese ? t(name ? "roads.street.editIntro.ja" : "roads.street.addIntro.ja") : t(name ? "roads.street.editIntro" : "roads.street.addIntro")}
          </Info>
          {japanese ? (
            <>
              <TextField help="help.roads.street.en" label={t("roads.street.en")} value={en} onChange={setEn} />
              <TextField help="help.roads.street.ja" label={t("roads.street.ja")} value={ja} onChange={setJa} />
            </>
          ) : (
            <TextField help="help.roads.street.name" label={t("roads.street.name")} value={en} onChange={setEn} />
          )}
        </div>
        <div className="modal-foot">
          <Button help="help.roads.street.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.roads.street.ok" variant="primary" disabledReason={reason} onClick={() => onDone({ en: en.trim(), ja: japanese ? ja.trim() : (name?.ja ?? "") })}>
            {t(name ? "roads.street.change" : "roads.street.addButton")}
          </Button>
        </div>
      </div>
    </div>
  );
}
