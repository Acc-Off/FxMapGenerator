import type { HelpKey } from "../shared/controls";
import type { MessageKey } from "../shared/i18n";

/** The screens with a guide: the start screen, the tabs of an open project and the kinds of the editing tab. */
export type GuideId = "start" | "project" | "settings" | "precheck" | "view" | "export" | "roads" | "poi" | "styles";

/** A step's name "<guide>.<part>": its title and text are guide.<name>.title and guide.<name>.text in the dictionaries. */
type Named<Suffix extends string, K = MessageKey> = K extends `guide.${infer Name}.${Suffix}` ? Name : never;
export type StepName = Named<"title"> & Named<"text">;

/** A step pointing at the element marked data-guide="<step name>" (a panel, a map, a group of items). */
export const AREA = "area";

export interface GuideStep {
  name: StepName;
  /** The item: its hover help key, or AREA. */
  target: HelpKey | typeof AREA;
  /** Where the box goes; the side with room when not given. */
  side?: "top" | "right" | "bottom" | "left";
}

export interface Guide {
  steps: readonly GuideStep[];
  /** Besides the first step's item being there, what must hold before the screen asks about its guide. */
  ready?: () => boolean;
}

function step(name: StepName, target: HelpKey | typeof AREA, side?: GuideStep["side"]): GuideStep {
  return { name, target, side };
}

/**
 * The guides, a short tour per screen, step by step in the order they are shown. A step whose item is not on
 * the screen is left out. scripts/check-ui.mjs checks that every step's item is on a screen and that every guide text
 * belongs to a step.
 */
export const GUIDES: Partial<Record<GuideId, Guide>> = {
  start: {
    steps: [
      step("start.new", "help.start.new"),
      step("start.open", "help.start.open"),
      step("start.recent", AREA),
      step("start.job", AREA),
      step("start.appSettings", "help.appSettings.open"),
      step("start.quit", "help.footer.quit", "top"),
      step("start.guide", "help.guide.open"),
    ],
  },
  project: {
    steps: [
      step("project.tabs", AREA, "bottom"),
      step("project.maps", AREA),
      step("project.minimap", AREA),
      step("project.workers", AREA),
      step("project.map", AREA),
      step("project.presets", "help.tool.preset"),
      step("project.jobs", AREA),
      step("project.prereq", AREA),
      step("project.run", AREA, "top"),
    ],
    // once the job list is there
    ready: () => document.querySelector(".todo-table") !== null,
  },
  settings: {
    steps: [
      step("settings.project", AREA),
      step("settings.frame", AREA),
      step("settings.server", AREA),
      step("settings.serverResources", AREA),
      step("settings.gameFiles", AREA),
      step("settings.labels", AREA),
      step("settings.disk", AREA),
    ],
  },
  precheck: {
    steps: [
      step("precheck.intro", "help.fivem.intro", "bottom"),
      step("precheck.resource", AREA),
      step("precheck.render", AREA),
      step("precheck.start", AREA),
      step("precheck.check", AREA),
      step("precheck.shot", AREA),
    ],
    // once the resource's panel has read what is placed
    ready: () => document.querySelector(".resource-panel") !== null,
  },
  view: {
    steps: [
      step("view.map", AREA, "left"),
      step("view.layers", AREA),
      step("view.compare", AREA),
      step("view.grids", AREA),
      step("view.places", AREA),
      step("view.point", AREA),
    ],
  },
  export: {
    steps: [
      step("export.where", AREA),
      step("export.web", AREA),
      step("export.minimap", AREA),
      step("export.editable", AREA),
      step("export.go", AREA),
      step("export.convert", AREA),
      step("export.run", AREA),
      step("export.record", AREA),
    ],
  },
  roads: {
    steps: [
      step("roads.left", AREA, "right"),
      step("roads.right", AREA, "left"),
      step("roads.overview", "help.roads.overview", "left"),
      step("roads.tools", AREA, "right"),
      step("roads.bundled", "help.roads.bundled", "right"),
      step("roads.legend", AREA, "top"),
      step("roads.selection", AREA, "right"),
      step("roads.list", AREA, "right"),
      step("roads.save", AREA, "right"),
    ],
  },
  poi: {
    steps: [
      step("poi.side", AREA, "right"),
      step("poi.tree", AREA, "right"),
      step("poi.buttons", AREA, "right"),
      step("poi.map", AREA, "left"),
      step("poi.tools", AREA, "left"),
      step("poi.values", AREA, "left"),
      step("poi.styles", "help.poi.side.styles", "right"),
      step("poi.save", AREA, "left"),
    ],
  },
  styles: {
    steps: [
      step("styles.choose", "help.styles.choose", "bottom"),
      step("styles.list", AREA, "right"),
      step("styles.preview", AREA, "left"),
      step("styles.source", AREA, "bottom"),
      step("styles.size", "help.styles.preview.size", "bottom"),
      step("styles.maps", "help.styles.maps", "bottom"),
      step("styles.save", "help.styles.save", "bottom"),
    ],
    // once both preview maps show their drawing (the first time, after the sample land is made)
    ready: () => {
      const maps = document.querySelectorAll('[data-guide="styles.preview"] [data-help="help.styles.preview.map"]');
      return maps.length >= 2 && [...maps].every((m) => m.querySelector("img.leaflet-tile-loaded") !== null);
    },
  },
};

/** The element a step points at: a CSS selector. */
export function selectorOf(s: GuideStep): string {
  return s.target === AREA ? `[data-guide="${s.name}"]` : `[data-help="${s.target}"]`;
}

/** Whether a step's item is on the screen. */
export function present(s: GuideStep): boolean {
  return document.querySelector(selectorOf(s)) !== null;
}
