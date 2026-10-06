import { useEffect, useState } from "react";
import { api } from "./api";
import { Button, Info, Select, TextField } from "./controls";
import { useT, type MessageKey } from "./i18n";
import { useAppStore } from "./store";
import type { GameFilesStatus, LanguageSetting, ThemeSetting } from "./types";

/** Where a folder came from, in words. */
const SOURCES: Record<string, MessageKey> = {
  app: "gameFiles.source.app",
  detected: "gameFiles.source.detected",
  environment: "gameFiles.source.environment",
  default: "gameFiles.source.default",
  emotePreviewer: "gameFiles.source.emotePreviewer",
};

export function sourceName(t: (key: MessageKey) => string, source: string | null | undefined): string {
  const key = source ? SOURCES[source] : undefined;
  return key ? t(key) : (source ?? "");
}

/**
 * The app's settings, the same for every project on this PC (opened with the gear of the header): the language and
 * theme of the screens, the GTA V folder and the RPF key folder. An empty folder means "found automatically": GTA V
 * from the registry, the keys along the key search order. Below each folder, what it gives now (checked as it is
 * typed) and, when something is missing, why and how to fix it. Saved together with the button.
 */
export function AppSettingsDialog({ onClose }: { onClose: () => void }) {
  const t = useT();
  const settings = useAppStore((s) => s.settings);
  const saveSettings = useAppStore((s) => s.saveSettings);
  const [language, setLanguage] = useState<LanguageSetting>(settings?.language ?? "auto");
  const [theme, setTheme] = useState<ThemeSetting>(settings?.theme ?? "system");
  const [gta, setGta] = useState(settings?.gtaFolder ?? "");
  const [keys, setKeys] = useState(settings?.keysFolder ?? "");
  const [status, setStatus] = useState<GameFilesStatus | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const id = setTimeout(() => void api.gameFilesStatus(gta, keys).then(setStatus, () => setStatus(null)), 250);
    return () => clearTimeout(id);
  }, [gta, keys]);

  const languages: { value: LanguageSetting; label: string }[] = [
    { value: "auto", label: t("settings.language.auto") },
    { value: "ja", label: t("settings.language.ja") },
    { value: "en", label: t("settings.language.en") },
  ];
  const themes: { value: ThemeSetting; label: string }[] = [
    { value: "system", label: t("settings.theme.system") },
    { value: "light", label: t("settings.theme.light") },
    { value: "dark", label: t("settings.theme.dark") },
  ];

  const browse = async (current: string, set: (v: string) => void, title: string) => {
    const r = await api.pickFolder(current || undefined, title);
    if (r.path) set(r.path);
  };
  const save = async () => {
    if (!settings) return;
    setBusy(true);
    try {
      // the settings as they are now (the recent list may have changed while the window was open)
      const current = useAppStore.getState().settings ?? settings;
      await saveSettings({ ...current, language, theme, gtaFolder: gta.trim() || null, keysFolder: keys.trim() || null });
      onClose();
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="modal-backdrop">
      <div className="modal modal-form" role="dialog" aria-label={t("appSettings.title")}>
        <div className="modal-head">
          <h2>{t("appSettings.title")}</h2>
        </div>
        <div className="form">
          <Info help="help.appSettings.intro" block className="muted">
            {t("appSettings.intro")}
          </Info>
          <Info help="help.appSettings.screens" block className="section-title">
            {t("appSettings.screens")}
          </Info>
          <div className="field-row">
            <Select help="help.settings.language" label={t("settings.language")} value={language} options={languages} onChange={setLanguage} />
            <Select help="help.settings.theme" label={t("settings.theme")} value={theme} options={themes} onChange={setTheme} />
          </div>
          <Info help="help.gameFiles.intro" block className="section-title">
            {t("gameFiles.title")}
          </Info>
          <Info help="help.gameFiles.intro" block className="muted">
            {t("gameFiles.intro")}
          </Info>
          <div className="field-row">
            <TextField help="help.gameFiles.gta" label={t("gameFiles.gta")} value={gta} onChange={setGta} placeholder={t("gameFiles.auto")} wide />
            <Button help="help.gameFiles.gta.browse" onClick={() => void browse(gta, setGta, t("gameFiles.gta"))}>
              {t("common.browse")}
            </Button>
          </div>
          {status && (
            <Info help="help.gameFiles.gta.status" block className={status.gtaFound ? "found-ok" : "found-ng"}>
              {status.gtaFound
                ? t("gameFiles.gta.found", { path: status.gta ?? "", source: sourceName(t, status.gtaSource) })
                : status.gtaProblem === "notGta"
                  ? t("gameFiles.gta.notGta", { path: status.gta ?? "" })
                  : t("gameFiles.gta.notFound")}
              {status.gtaFromCommandLine && <span className="muted"> {t("gameFiles.commandLine")}</span>}
            </Info>
          )}
          <div className="field-row">
            <TextField help="help.gameFiles.keys" label={t("gameFiles.keys")} value={keys} onChange={setKeys} placeholder={t("gameFiles.auto")} wide />
            <Button help="help.gameFiles.keys.browse" onClick={() => void browse(keys, setKeys, t("gameFiles.keys"))}>
              {t("common.browse")}
            </Button>
          </div>
          {status && (
            <Info help="help.gameFiles.keys.status" block className={status.keysFound ? "found-ok" : "found-ng"}>
              {status.keysFound
                ? t("gameFiles.keys.found", { path: status.keys ?? "", source: sourceName(t, status.keysSource) })
                : t("gameFiles.keys.notFound")}
              {status.keysFromCommandLine && <span className="muted"> {t("gameFiles.commandLine")}</span>}
              {!status.keysFound && status.keysTried.length > 0 && (
                <ul className="tried">
                  {status.keysTried.map((k) => (
                    <li key={k.folder}>
                      {t("gameFiles.keys.tried", { folder: k.folder, source: sourceName(t, k.source), missing: k.missing.join(", ") })}
                    </li>
                  ))}
                </ul>
              )}
            </Info>
          )}
        </div>
        <div className="modal-foot">
          <Button help="help.gameFiles.cancel" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button help="help.gameFiles.save" variant="primary" disabledReason={busy ? "reason.saving" : null} onClick={() => void save()}>
            {t("common.save")}
          </Button>
        </div>
      </div>
    </div>
  );
}
