import { Button, Info } from "../../shared/controls";
import { useT } from "../../shared/i18n";
import { useGameFilesBusy } from "../../project/locks";
import { useRoadEditor } from "./store";

/**
 * The button that reads the game files now, with what the last reading found: in the road editor's panel, and in its
 * place while the files are not read yet (`primary`).
 */
export function GameFilesRead({ primary }: { primary?: boolean }) {
  const t = useT();
  const reading = useRoadEditor((s) => s.reading);
  const found = useRoadEditor((s) => s.gameFiles);
  const read = useRoadEditor((s) => s.readGameFiles);
  const busy = useGameFilesBusy();
  return (
    <>
      <Button
        help="help.roads.gameFiles.read"
        variant={primary ? "primary" : undefined}
        disabledReason={busy ?? (reading ? "reason.roads.gameFiles.reading" : null)}
        onClick={() => void read()}
      >
        {t("roads.gameFiles.read")}
      </Button>
      {(reading || found) && (
        <Info help="help.roads.gameFiles.note" block className={found && "failed" in found ? "text-error" : "muted"}>
          {reading || !found
            ? t("roads.gameFiles.reading")
            : "failed" in found
              ? t("roads.gameFiles.failed", { message: found.failed })
              : t(found.changed ? "roads.gameFiles.changed" : "roads.gameFiles.same", { nodes: found.nodes.toLocaleString(), s: found.seconds.toFixed(1) })}
        </Info>
      )}
    </>
  );
}
