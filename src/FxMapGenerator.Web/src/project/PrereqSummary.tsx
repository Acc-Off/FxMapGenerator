import { Button, Info, type HelpKey } from "../shared/controls";
import { useT, type MessageKey } from "../shared/i18n";
import { useAppStore } from "../shared/store";
import type { CheckDto } from "../shared/types";
import { useProjectStore, type Screen } from "./store";

type CheckId = CheckDto["id"];

/** Where each prerequisite is set: a tab of the project, or the app's settings (the GTA V and key folders). */
export const CHECK_PLACE: Record<CheckId, Screen | "app"> = {
  capture: "precheck",
  gameFiles: "app",
  serverResources: "settings",
  postals: "settings",
  fonts: "settings",
  disk: "settings",
  render: "precheck",
  game: "precheck",
  preset: "precheck",
};

const CHECK_TEXT: Record<CheckId, readonly [MessageKey, HelpKey]> = {
  capture: ["check.capture", "help.check.capture"],
  game: ["check.game", "help.check.game"],
  render: ["check.render", "help.check.render"],
  preset: ["check.preset", "help.check.preset"],
  gameFiles: ["check.gameFiles", "help.check.gameFiles"],
  serverResources: ["check.serverResources", "help.check.serverResources"],
  postals: ["check.postals", "help.check.postals"],
  fonts: ["check.fonts", "help.check.fonts"],
  disk: ["check.disk", "help.check.disk"],
};

/** The prerequisites not met (✗) that are set on a screen: the count its tab shows. */
export function failing(checks: CheckDto[] | null, screen: Screen): number {
  return (checks ?? []).filter((c) => c.ok === false && CHECK_PLACE[c.id] === screen).length;
}

/**
 * The prerequisites of the chosen maps that are not met, one line each with a link to where they are set (the project
 * settings, the FiveM setup and check, or the app's settings); the values themselves and their checks live there.
 */
export function PrereqSummary() {
  const t = useT();
  const checks = useProjectStore((s) => s.checks);
  const setScreen = useProjectStore((s) => s.setScreen);
  const setSettingsOpen = useAppStore((s) => s.setSettingsOpen);
  if (!checks) return <p className="muted">{t("common.loading")}</p>;
  const failed = checks.filter((c) => c.ok === false);
  if (failed.length === 0)
    return (
      <Info help="help.prereq.allOk" block className="check-row check-ok">
        <span className="check-mark">✓</span>
        {t("prereq.allOk")}
      </Info>
    );
  return (
    <ul className="checks">
      {failed.map((c) => {
        const [label, help] = CHECK_TEXT[c.id];
        const place = CHECK_PLACE[c.id];
        return (
          <li key={c.id}>
            <Info help={help} className="check-row check-ng">
              <span className="check-mark">✗</span>
              {t(label, c.values)}
            </Info>
            <Button
              help={place === "app" ? "help.prereq.openApp" : place === "settings" ? "help.prereq.openSettings" : "help.prereq.openFiveM"}
              variant="link"
              className="row-link"
              onClick={() => (place === "app" ? setSettingsOpen(true) : setScreen(place))}
            >
              {t(place === "app" ? "prereq.openApp" : place === "settings" ? "prereq.openSettings" : "prereq.openFiveM")}
            </Button>
          </li>
        );
      })}
    </ul>
  );
}
