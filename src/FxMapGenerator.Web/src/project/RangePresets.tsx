import { useEffect, useState } from "react";
import { Button, Check, Info, Select } from "../shared/controls";
import { useT, type MessageKey } from "../shared/i18n";
import type { ProjectDto, RangePresetDto, RangePresetId, Sides } from "../shared/types";
import { INPUTS } from "./labels";
import { useLock } from "./locks";
import { useProjectStore } from "./store";

const SIDES: { side: keyof Sides; label: MessageKey }[] = [
  { side: "top", label: "settings.frame.top" },
  { side: "bottom", label: "settings.frame.bottom" },
  { side: "left", label: "settings.frame.left" },
  { side: "right", label: "settings.frame.right" },
];
const MAX_VERTICAL = 2, MAX_HORIZONTAL = 4;

/** The west, south, east and north edges (game metres) of a preset's blocks, for the map to move to. */
export interface PresetArea {
  west: number;
  south: number;
  east: number;
  north: number;
}

/**
 * The frame a preset needs from the project's: on each side the more of the cells there and the cells the preset needs.
 * `limit` names the axes that would then pass the most cells a project may add.
 */
function widened(have: Sides, preset: RangePresetDto): { cells: Sides; widen: boolean; limit: ("vertical" | "horizontal")[] } {
  const cells = {
    top: Math.max(have.top, preset.needs.top),
    bottom: Math.max(have.bottom, preset.needs.bottom),
    left: Math.max(have.left, preset.needs.left),
    right: Math.max(have.right, preset.needs.right),
  };
  const widen = SIDES.some((s) => cells[s.side] > have[s.side]);
  const limit: ("vertical" | "horizontal")[] = [];
  if (cells.top + cells.bottom > MAX_VERTICAL) limit.push("vertical");
  if (cells.left + cells.right > MAX_HORIZONTAL) limit.push("horizontal");
  return { cells, widen, limit };
}

/**
 * The range tools' drop-down of range presets (shown while the project adds areas outside the standard map): choosing
 * one opens a window that says what it adds and asks before widening the frame.
 */
export function RangePresetSelect({ onAdded }: { onAdded: (area: PresetArea) => void }) {
  const t = useT();
  const project = useProjectStore((s) => s.project);
  const rangeLock = useLock(INPUTS.range);
  const [open, setOpen] = useState<RangePresetId | null>(null);
  if (!project || project.file.range.extraCells === null) return null;
  const name = (id: RangePresetId) => t(id === "cayoPerico" ? "rangePreset.cayoPerico" : "rangePreset.roxwood");
  const options = [
    { value: "" as const, label: t("tool.preset.choose") },
    ...project.rangePresets.map((p) => {
      const added = p.missingLand + p.missingWater === 0;
      return { value: p.id, label: added ? t("rangePreset.added", { name: name(p.id) }) : name(p.id), disabled: added };
    }),
  ];
  const preset = open ? project.rangePresets.find((p) => p.id === open) : undefined;
  return (
    <>
      <Select<"" | RangePresetId>
        help="help.tool.preset"
        label={t("tool.preset")}
        value=""
        options={options}
        disabledReason={rangeLock}
        onChange={(v) => v && setOpen(v)}
      />
      {preset && <RangePresetDialog project={project} preset={preset} onClose={() => setOpen(null)} onAdded={onAdded} />}
    </>
  );
}

function RangePresetDialog({ project, preset, onClose, onAdded }: {
  project: ProjectDto;
  preset: RangePresetDto;
  onClose(): void;
  onAdded(area: PresetArea): void;
}) {
  const t = useT();
  const frameLock = useLock(INPUTS.frame);
  const cayoLock = useLock(INPUTS.cayoPerico);
  const rangeLock = useLock(INPUTS.range);
  const edit = useProjectStore((s) => s.edit);
  // the island's roads with the island: offered ticked when the project does not read them yet
  const offerRoads = preset.id === "cayoPerico" && !project.file.cayoPerico;
  const [roads, setRoads] = useState(offerRoads);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const have = project.file.range.extraCells ?? { top: 0, bottom: 0, left: 0, right: 0 };
  const { cells, widen, limit } = widened(have, preset);
  const blocked = widen && limit.length > 0;
  const n = preset.missingLand + preset.missingWater;
  const sides = SIDES.filter((s) => cells[s.side] > have[s.side])
    .map((s) => t("rangePreset.widenSide", { side: t(s.label), from: have[s.side], to: cells[s.side] }))
    .join(t("rangePreset.widenJoin"));
  // the sides to lower when the frame cannot grow: those of the full axis the preset does not need
  const lower = SIDES.filter((s) => {
    const axis = s.side === "top" || s.side === "bottom" ? "vertical" : "horizontal";
    return limit.includes(axis) && have[s.side] > 0 && preset.needs[s.side] === 0;
  }).map((s) => t(s.label)).join(t("rangePreset.limitJoin"));
  const limits = limit.map((a) => t(a === "vertical" ? "rangePreset.limitVertical" : "rangePreset.limitHorizontal")).join(t("rangePreset.limitJoin"));

  const add = async () => {
    await edit({
      ...(widen ? { extraCells: { on: true, ...cells } } : {}),
      range: { preset: preset.id },
      ...(offerRoads && roads ? { cayoPerico: true } : {}),
    });
    const now = useProjectStore.getState().project?.rangePresets.find((p) => p.id === preset.id);
    onClose();
    if (now && now.missingLand + now.missingWater === 0) onAdded({ west: preset.west, south: preset.south, east: preset.east, north: preset.north });
  };
  const title = t(preset.id === "cayoPerico" ? "rangePreset.title.cayoPerico" : "rangePreset.title.roxwood");
  const reason = rangeLock ?? (widen ? frameLock : null) ?? (offerRoads && roads ? cayoLock : null);
  return (
    <div className="modal-backdrop">
      <div className="modal modal-form" role="dialog" aria-label={title}>
        <div className="modal-head">
          <h2>{title}</h2>
        </div>
        <div className="form">
          <Info help="help.rangePreset.body" block>
            {t(preset.id === "cayoPerico" ? "rangePreset.body.cayoPerico" : "rangePreset.body.roxwood", { n, land: preset.missingLand, water: preset.missingWater })}
          </Info>
          {preset.id === "roxwood" && (
            <Info help="help.rangePreset.body" block className="muted">
              {t("rangePreset.estimate")}
            </Info>
          )}
          {widen && !blocked && (
            <Info help="help.rangePreset.widen" block className="note">
              {t("rangePreset.widen", { n: preset.missingOutside, sides })}
            </Info>
          )}
          {blocked && (
            <Info help="help.rangePreset.limit" block className="note">
              {t("rangePreset.limit", { n: preset.missingOutside, limit: limits, sides: lower })}
            </Info>
          )}
          {offerRoads && !blocked && (
            <Check help="help.settings.cayoPerico" checked={roads} onChange={setRoads}>
              {t("settings.cayoPerico")}
            </Check>
          )}
        </div>
        <div className="modal-foot">
          {blocked ? (
            <Button help="help.rangePreset.close" onClick={onClose}>
              {t("common.close")}
            </Button>
          ) : (
            <>
              <Button help="help.rangePreset.cancel" onClick={onClose}>
                {t("common.cancel")}
              </Button>
              <Button help={widen ? "help.rangePreset.widenAdd" : "help.rangePreset.add"} variant="primary" disabledReason={reason} onClick={() => void add()}>
                {t(widen ? "rangePreset.widenAdd" : "rangePreset.add")}
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
