import { useState } from "react";
import { Button, Check, Info, Radio, Select, Slider, type HelpKey, type Reason } from "../shared/controls";
import { useT, type MessageKey } from "../shared/i18n";
import { heightQualityOf, makesJapanese, type HeightQuality, type MinimapOutside } from "../shared/types";
import { INPUTS, mapName, PRESET_IDS, presetName, workerMarks } from "./labels";
import { useLock } from "./locks";
import { isActive, jobOfProject, useProjectStore } from "./store";

/** The minimap's two ways with the blocks outside the range. */
const MINIMAP_OUTSIDE: readonly { id: MinimapOutside; label: MessageKey; help: HelpKey }[] = [
  { id: "map", label: "minimap.outside.map", help: "help.minimap.outside.map" },
  { id: "transparent", label: "minimap.outside.transparent", help: "help.minimap.outside.transparent" },
];

/** The height qualities in the order of the screen (fastest first). */
const HEIGHT_QUALITIES: readonly { id: HeightQuality; label: MessageKey; help: HelpKey }[] = [
  { id: "speed", label: "heightQuality.speed", help: "help.heightQuality.speed" },
  { id: "balance", label: "heightQuality.balance", help: "help.heightQuality.balance" },
  { id: "quality", label: "heightQuality.quality", help: "help.heightQuality.quality" },
];

/**
 * Left side: what changes the job list at once: the maps to output (satellite, atlas with its list of styles, road map)
 * and the height quality, the minimap, the workers, the range in numbers. The server and the rest of the project's
 * settings are on the project settings screen.
 */
export function SidePanel() {
  const t = useT();
  const project = useProjectStore((s) => s.project)!;
  const plan = useProjectStore((s) => s.plan);
  const workers = useProjectStore((s) => s.workers);
  const job = useProjectStore((s) => s.job);
  const { edit, setWorkers } = useProjectStore.getState();
  const mapsLock = useLock(INPUTS.maps);
  const minimapLock = useLock(INPUTS.minimap);
  const running = isActive(job) && jobOfProject(job, project);
  const f = project.file;
  const processors = plan?.table.processors ?? navigator.hardwareConcurrency ?? 8;
  const [newPreset, setNewPreset] = useState(PRESET_IDS[0]);

  // the atlas and the road map are chosen like the satellite map (their drawing comes in a later version)
  const atlasReason = mapsLock;
  const minimapOptions = [{ value: "none", label: t("minimap.none") }, ...project.maps.map((m) => ({ value: m, label: mapName(t, m) }))];
  const outsideReason: Reason | null = minimapLock ?? (f.minimap.map ? null : "reason.noMinimap");
  const styleOptions = [
    ...PRESET_IDS.map((p) => ({ value: p, label: presetName(t, p), group: t("styles.group.bundled") })),
    ...project.ownStyles.map((s) => ({ value: s.id, label: presetName(t, s.id), group: t("styles.group.project") })),
  ];
  // the height quality: all three shown, those that do not go with the maps greyed out with the reason
  const cellMaps = f.maps.atlas.enabled || f.maps.roadmap;
  const quality = heightQualityOf(f);
  const qualityReason = (q: HeightQuality): Reason | null =>
    mapsLock ??
    (q === "speed" && cellMaps ? "reason.heightQuality.cellMaps" : null);

  return (
    <aside className="side">
      <section data-guide="project.maps">
        <Info help="help.panel.maps" block className="section-title">
          {t("panel.maps")}
        </Info>
        <Check help="help.map.satellite" checked={f.maps.satellite} disabledReason={mapsLock} onChange={(v) => void edit({ satellite: v })}>
          {t("map.satellite")}
        </Check>
        <Check help="help.map.atlas" checked={f.maps.atlas.enabled} disabledReason={atlasReason} onChange={(v) => void edit({ atlas: { enabled: v } })}>
          {t("map.atlas")}
        </Check>
        <div className={`atlas-styles${f.maps.atlas.enabled ? "" : " is-off"}`}>
          <Info help={makesJapanese(f) ? "help.atlas.styles.ja" : "help.atlas.styles"} block className="field-label">
            {t("atlas.styles")}
          </Info>
          {f.maps.atlas.styles.map((s, i) => (
            <div key={s} className="atlas-style">
              <Info help="help.atlas.style">{presetName(t, s)}</Info>
              <Button
                help="help.atlas.remove"
                variant="link"
                disabledReason={atlasReason}
                onClick={() => void edit({ atlas: { styles: f.maps.atlas.styles.filter((_, j) => j !== i) } })}
              >
                {t("atlas.remove")}
              </Button>
            </div>
          ))}
          <div className="atlas-add">
            <Select help="help.atlas.addStyle" value={newPreset} options={styleOptions} disabledReason={atlasReason} onChange={setNewPreset} />
            <Button
              help="help.atlas.add"
              disabledReason={atlasReason ?? (f.maps.atlas.styles.includes(newPreset) ? "reason.atlas.listed" : null)}
              onClick={() => void edit({ atlas: { styles: [...f.maps.atlas.styles, newPreset] } })}
            >
              {t("atlas.add")}
            </Button>
          </div>
        </div>
        <Check help="help.map.roadmap" checked={f.maps.roadmap} disabledReason={mapsLock} onChange={(v) => void edit({ roadmap: v })}>
          {t("map.roadmap")}
        </Check>
        <Info help="help.heightQuality" block className="field-label height-quality-label">
          {t("heightQuality")}
        </Info>
        <div className="radio-group">
          {HEIGHT_QUALITIES.map((q) => (
            <Radio key={q.id} group="heightQuality" help={q.help} checked={quality === q.id} disabledReason={qualityReason(q.id)} onChange={() => void edit({ heightQuality: q.id })}>
              {t(q.label)}
            </Radio>
          ))}
        </div>
      </section>

      <section data-guide="project.minimap">
        <Info help="help.panel.minimap" block className="section-title">
          {t("panel.minimap")}
        </Info>
        <Select
          help="help.minimap.map"
          label={t("minimap.map")}
          value={f.minimap.map ?? "none"}
          options={minimapOptions}
          disabledReason={minimapLock}
          onChange={(v) => void edit({ minimapMap: v })}
        />
        <Info help="help.minimap.outside" block className="field-label">
          {t("minimap.outside")}
        </Info>
        <div className="radio-group">
          {MINIMAP_OUTSIDE.map((o) => (
            <Radio key={o.id} group="minimapOutside" help={o.help} checked={f.minimap.outside === o.id} disabledReason={outsideReason} onChange={() => void edit({ minimapOutside: o.id })}>
              {t(o.label)}
            </Radio>
          ))}
        </div>
      </section>

      <section data-guide="project.workers">
        <Info help="help.panel.workers" block className="section-title">
          {t("panel.workers")}
        </Info>
        <Slider
          help="help.panel.workers"
          value={workers}
          min={1}
          max={processors}
          marks={workerMarks(processors)}
          display={t("workers.of", { n: workers, total: processors })}
          onChange={setWorkers}
        />
      </section>

      <section>
        <Info help="help.panel.range" block className="section-title">
          {t("panel.range")}
        </Info>
        <Info help="help.panel.range.count" block>
          {t("panel.range.count", { blocks: project.rangeBlocks, land: project.rangeLand, water: project.rangeWater, cells: project.cells })}
        </Info>
        {(f.range.add.length > 0 || f.range.remove.length > 0) && (
          <Info help="help.panel.range.changed" block className="muted">
            {t("panel.range.changed", { added: f.range.add.length, removed: f.range.remove.length })}
          </Info>
        )}
      </section>

      {running && (
        <Info help="help.panel.nextRun" block className="note">
          {t("panel.nextRun")}
        </Info>
      )}
    </aside>
  );
}
