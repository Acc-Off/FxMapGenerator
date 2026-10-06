import type { Reason } from "../shared/controls";
import { useT } from "../shared/i18n";
import { stageText } from "./labels";
import { isActive, jobOfProject, useProjectStore } from "./store";

/** Same rule as the server (InputKeys.Overlap): the same key, or one contains the other ("style" and "style.regions"). */
function overlap(held: string, edited: string): boolean {
  return held === edited || edited.startsWith(held + ".") || held.startsWith(edited + ".");
}

/**
 * Why an input of the open project cannot be edited now: a stage of this project's running job still reads it.
 * Null when it can be edited (the change then goes to the next run).
 */
export function useLock(key: string): Reason | null {
  const t = useT();
  const job = useProjectStore((s) => s.job);
  const project = useProjectStore((s) => s.project);
  if (!isActive(job) || !jobOfProject(job, project) || !job) return null;
  const rows = job.locks.filter((l) => overlap(l.key, key)).flatMap((l) => l.stages);
  if (rows.length === 0) return null;
  return { key: "reason.locked", params: { stages: [...new Set(rows)].map((r) => stageText(t, r)).join(t("list.join")) } };
}

/**
 * Why the game files cannot be read now (the road editor's button): a step of this project's running job that reads
 * them (the game files step or a map data step) is running. Null when they can (also while the capture runs).
 */
export function useGameFilesBusy(): Reason | null {
  const t = useT();
  const job = useProjectStore((s) => s.job);
  const project = useProjectStore((s) => s.project);
  if (!isActive(job) || !jobOfProject(job, project) || !job) return null;
  const rows = job.stages.filter((s) => s.state === "running" && (s.row === "gameFiles" || s.row.startsWith("mapData"))).map((s) => s.row);
  if (rows.length === 0) return null;
  return { key: "reason.roads.gameFiles.busy", params: { stages: [...new Set(rows)].map((r) => stageText(t, r)).join(t("list.join")) } };
}
