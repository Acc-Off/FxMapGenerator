import { useEffect, useMemo, useState } from "react";
import { api } from "../../shared/api";
import { Button, Check, Info, NumberField, Select, Slider, type HelpKey, type Reason } from "../../shared/controls";
import { type MessageKey, useI18nStore, useT } from "../../shared/i18n";
import { INPUTS } from "../../project/labels";
import { useLock } from "../../project/locks";
import { useProjectStore } from "../../project/store";
import { type AddedStreetName, groupName, itemName, makesJapanese, type NotAppliedEdit } from "../../shared/types";
import {
  type Base,
  dropNotApplied,
  editRows,
  type EditRow,
  isAdded,
  LINK,
  MAX_LANES,
  MAX_WIDTH,
  newStreetHash,
  NODE,
  nodePos,
  pairKey,
  pairOf,
  putStreet,
  revertRow,
  setLinkValues,
  setNodeValues,
  streetHashes,
  unhide,
  type View,
} from "./model";
import { BundledDialog } from "./BundledDialog";
import { GameFilesRead } from "./GameFilesRead";
import { type RoadTool, TOOLS, useRoadEditor } from "./store";
import { StreetNameDialog } from "./StreetNameDialog";

interface Option {
  value: string;
  label: string;
}

interface Props {
  backgrounds: Option[];
  background: string;
  onBackground(v: string): void;
  shapesMaps: Option[];
  shapesMap: string | null;
  onShapesMap(v: string): void;
  shapesOpacity: number;
  onShapesOpacity(v: number): void;
  onFly(bounds: [number, number, number, number]): void;
  onRemoveOrHide(): void;
}

/** Each tool's name and help (the key that chooses it: its place in TOOLS). */
const TOOL_TEXT: Record<RoadTool, readonly [MessageKey, HelpKey]> = {
  draw: ["roads.tool.draw", "help.roads.tool.draw"],
  move: ["roads.tool.move", "help.roads.tool.move"],
  select: ["roads.tool.select", "help.roads.tool.select"],
  box: ["roads.tool.box", "help.roads.tool.box"],
  road: ["roads.tool.road", "help.roads.tool.road"],
};

/** The street list's entry that opens the window for a name of one's own. */
const NEW_STREET = "new";


/** The road editor's side panel: saving, the tools, what the maps show, the values of the selection, the list of edits. */
export function RoadPanel(p: Props) {
  const t = useT();
  const st = useRoadEditor();
  const lock = useLock(INPUTS.roadEdits);
  const dirty = st.dirty();
  const rows = useMemo(() => (st.base && st.view ? editRows(st.base, st.view) : []), [st.base, st.view]);
  const [takingIn, setTakingIn] = useState(false);
  const sel = st.selection;
  const selAdded = sel.nodes.some(isAdded) || sel.links.some((k) => st.edits.links.some((l) => !l.original && pairKey(l.from, l.to) === k));
  const selGame = sel.nodes.some((k) => !isAdded(k)) || sel.links.some((k) => !!st.base?.pairs.has(k));
  const selHidden = sel.nodes.some((k) => st.edits.nodes[k]?.hidden) || sel.links.some((k) => st.edits.links.some((l) => l.hidden && pairKey(l.from, l.to) === k));
  const nothing: Reason | null = sel.nodes.length + sel.links.length === 0 ? "reason.roads.noSelection" : null;
  const save = async () => {
    if (await st.save()) void useProjectStore.getState().refresh();
  };

  return (
    <aside className="side edit-side">
      <section data-guide="roads.save">
        <Info help="help.roads.file" block className="muted">
          {st.status?.file ? t("roads.file", { file: st.status.file }) : t("roads.file.none")}
        </Info>
        <div className="edit-buttons">
          <Button help="help.roads.undo" disabledReason={st.past.length === 0 ? "reason.roads.noUndo" : null} onClick={st.undo}>
            {t("roads.undo")}
          </Button>
          <Button help="help.roads.redo" disabledReason={st.future.length === 0 ? "reason.roads.noRedo" : null} onClick={st.redo}>
            {t("roads.redo")}
          </Button>
          <Button help="help.roads.save" variant="primary" disabledReason={lock ?? (st.saving ? "reason.roads.saving" : !dirty ? "reason.roads.nothingToSave" : null)} onClick={() => void save()}>
            {t("roads.save")}
          </Button>
        </div>
        {dirty && (
          <Info help="help.roads.unsaved" block className="edit-unsaved">
            {t("roads.unsaved")}
          </Info>
        )}
        {st.status?.problem && (
          <Info help="help.roads.problem" block className="text-error">
            {t("roads.problem", { message: st.status.problem })}
          </Info>
        )}
        {st.error && (
          <div className="banner banner-error">
            {st.error}
            <Button help="help.roads.error.close" variant="link" onClick={st.clearError}>
              {t("common.close")}
            </Button>
          </div>
        )}
        <div className="edit-buttons">
          <GameFilesRead />
        </div>
      </section>

      <section data-guide="roads.tools">
        <Info help="help.roads.tools" block className="section-title">
          {t("roads.tools")}
        </Info>
        <div className="edit-buttons">
          {TOOLS.map((id, i) => (
            <Button key={id} help={TOOL_TEXT[id][1]} pressed={st.tool === id} onClick={() => st.setTool(id)}>
              <span className="edit-tool-key">{i + 1}</span>
              {t(TOOL_TEXT[id][0])}
            </Button>
          ))}
        </div>
        <Info help="help.roads.hint" block className="muted edit-hint">
          {t(`roads.hint.${st.tool}` as MessageKey)}
        </Info>
        <div className="edit-buttons">
          <Button help="help.roads.remove" disabledReason={nothing ?? (selAdded || selGame ? null : "reason.roads.noSelection")} onClick={p.onRemoveOrHide}>
            {selAdded && !selGame ? t("roads.remove") : selGame && !selAdded ? t("roads.hide") : t("roads.removeOrHide")}
          </Button>
          <Button
            help="help.roads.show"
            disabledReason={nothing ?? (selHidden ? null : "reason.roads.noneHidden")}
            onClick={() => st.change(unhide(st.edits, sel.nodes, sel.links))}
          >
            {t("roads.show")}
          </Button>
        </div>
        <div className="edit-buttons">
          <Button help="help.roads.bundled" disabledReason={Object.keys(st.bundled?.groups ?? {}).length === 0 ? "reason.roads.bundled.none" : null} onClick={() => setTakingIn(true)}>
            {t("roads.bundled")}
          </Button>
        </div>
        {takingIn && <BundledDialog onClose={() => setTakingIn(false)} />}
      </section>

      <section>
        <Info help="help.roads.display" block className="section-title">
          {t("roads.display")}
        </Info>
        <Select help="help.roads.background" label={t("roads.background")} value={p.background} options={p.backgrounds} onChange={p.onBackground} />
        <Select
          help="help.roads.shapesMap"
          label={t("roads.shapesMap")}
          value={p.shapesMap ?? ""}
          options={p.shapesMaps.length ? p.shapesMaps : [{ value: "", label: t("base.none") }]}
          disabledReason={p.shapesMaps.length ? null : "reason.roads.noShapesMap"}
          onChange={p.onShapesMap}
        />
        <Slider help="help.roads.shapesOpacity" label={t("roads.shapesOpacity")} value={p.shapesOpacity} min={0} max={100} display={`${p.shapesOpacity} %`} onChange={p.onShapesOpacity} />
      </section>

      <section data-guide="roads.selection">
        <Info help="help.roads.selection" block className="section-title">
          {t("roads.selection")}
        </Info>
        {st.base && st.view ? <SelectionValues base={st.base} view={st.view} /> : null}
      </section>

      <section data-guide="roads.list">
        <Info help="help.roads.list" block className="section-title">
          {t("roads.list")}
        </Info>
        {rows.length === 0 ? (
          <p className="muted">{t("roads.list.none")}</p>
        ) : (
          <div className="edit-list">
            {rows.map((r) => (
              <Row key={r.id} r={r} onFly={p.onFly} />
            ))}
          </div>
        )}
        {st.view && st.view.notApplied.length > 0 && (
          <>
            <Info help="help.roads.notApplied" block className="section-title edit-notapplied-title">
              {t("roads.notApplied", { n: st.view.notApplied.length })}
            </Info>
            <div className="edit-list">
              {st.view.notApplied.map((n) => (
                <NotAppliedRow key={`${n.kind}:${n.key}`} n={n} onFly={p.onFly} />
              ))}
            </div>
          </>
        )}
      </section>
    </aside>
  );
}

function Row({ r, onFly }: { r: EditRow; onFly(b: [number, number, number, number]): void }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = useRoadEditor();
  const group = r.group !== undefined ? st.edits.groups?.[r.group] : undefined;
  const text = group
    ? t("roads.group.bundled", { name: itemName(groupName(group), lang), nodes: r.nodes.length, links: r.links.length })
    : t(`roads.group.${r.kind}` as MessageKey, { nodes: r.nodes.length, links: r.links.length });
  return (
    <div className="edit-row">
      <Button
        help="help.roads.group"
        variant="row"
        onClick={() => {
          onFly(r.bounds);
          st.select({ nodes: r.nodes, links: r.links });
        }}
      >
        {text}
      </Button>
      <Button help="help.roads.revert" variant="link" onClick={() => st.change(revertRow(st.edits, r), { nodes: [], links: [] })}>
        {t("roads.revert")}
      </Button>
    </div>
  );
}

function NotAppliedRow({ n, onFly }: { n: NotAppliedEdit; onFly(b: [number, number, number, number]): void }) {
  const t = useT();
  const st = useRoadEditor();
  const where = (() => {
    const keys = n.kind === "node" ? [n.key] : n.key.split(" ");
    const pts = keys.map((k) => st.edits.nodes[k]?.original ?? st.edits.nodes[k]).filter((o) => o?.x !== undefined) as { x: number; y: number }[];
    if (pts.length === 0) return null;
    return [Math.min(...pts.map((q) => q.x)), Math.min(...pts.map((q) => q.y)), Math.max(...pts.map((q) => q.x)), Math.max(...pts.map((q) => q.y))] as [number, number, number, number];
  })();
  return (
    <div className="edit-row">
      <Button help="help.roads.notApplied.item" variant="row" disabledReason={where ? null : "reason.roads.noPlace"} onClick={() => where && onFly(where)}>
        {t(n.kind === "node" ? "roads.notApplied.node" : "roads.notApplied.link", { key: n.key.replace(" ", " – ") })}
        <span className="muted"> {t(`roads.reason.${n.reason}` as MessageKey)}</span>
      </Button>
      <Button help="help.roads.notApplied.drop" variant="link" onClick={() => st.change(dropNotApplied(st.edits, n))}>
        {t("roads.revert")}
      </Button>
    </div>
  );
}

/** A value shared by all (else null = mixed). */
function common<T>(values: T[]): T | null {
  return values.length > 0 && values.every((v) => v === values[0]) ? values[0] : null;
}

function SelectionValues({ base, view }: { base: Base; view: View }) {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const japanese = useProjectStore((s) => (s.project ? makesJapanese(s.project.file) : false));
  const st = useRoadEditor();
  // the window for a street name of one's own: a new one (hash null) or new words for one the edits add
  const [naming, setNaming] = useState<{ hash: number | null } | null>(null);
  const { nodes, links } = st.selection;
  if (nodes.length + links.length === 0) return <p className="muted">{t("roads.selection.none")}</p>;
  const added = st.edits.streets ?? {};
  // the Japanese street names only when the project makes Japanese maps
  const nameText = (s: { en: string; ja?: string | null }) => (japanese && lang === "ja" && s.ja ? `${s.ja}（${s.en}）` : s.en);
  const streetName = (h: number) => {
    const s = added[String(h)] ?? base.streets.get(h);
    return s ? nameText(s) : `#${h}`;
  };
  const shownNodes = nodes.map((k) => ({ key: k, n: nodePos(base, view, k) })).filter((x) => x.n);
  const hiddenNodes = shownNodes.some((x) => x.n!.hidden);
  const street = common(shownNodes.map((x) => x.n!.street));
  const streets = [...(street === null ? [{ value: "", label: t("roads.street.mixed") }] : []), { value: "0", label: t("roads.street.none") },
    { value: NEW_STREET, label: t("roads.street.add") },
    ...Object.entries(added).map(([h, s]) => ({ value: h, label: t("roads.street.added", { name: nameText(s) }) })),
    ...base.dto.streets.map((s) => ({ value: String(s.hash), label: nameText(s) }))];
  const addedStreet = street !== null && added[String(street)] ? street : null;
  const englishNames = (except: number | null) => ({
    game: new Set(base.dto.streets.map((s) => s.en.trim().toLowerCase())),
    added: new Set(Object.entries(added).filter(([h]) => Number(h) !== except).map(([, s]) => s.en.trim().toLowerCase())),
  });
  const named = (name: AddedStreetName) => {
    if (!naming) return;
    if (naming.hash === null) {
      const hash = newStreetHash(name.en, streetHashes(base, st.edits));
      st.change(setNodeValues(base, putStreet(st.edits, hash, name), nodes, { street: hash }));
    } else st.change(putStreet(st.edits, naming.hash, name));
    setNaming(null);
  };
  const flag = (bit: number) => common(shownNodes.map((x) => (x.n!.flags & bit) !== 0));
  const z = common(shownNodes.map((x) => Math.round(x.n!.z * 100) / 100));
  const nodeLock: Reason | null = hiddenNodes ? "reason.roads.hidden" : null;
  const setNodes = (change: Parameters<typeof setNodeValues>[3]) => st.change(setNodeValues(base, st.edits, nodes, change));

  // links: the lanes each way as each link goes (a game link: as its first record in the game's data)
  const linkValues = links.map((key) => {
    const added = view.addedLinks.find((l) => pairKey(l.from, l.to) === key);
    if (added) return { key, from: added.from, to: added.to, lf: added.lanesForward, lb: added.lanesBack, narrow: added.narrow, width: added.width, hidden: false, added: true };
    const j = base.pairs.get(key);
    if (j === undefined) return null;
    const d = base.dto;
    const s = view.links.get(j);
    const hidden = !!s?.hidden || !!view.nodes.get(d.linkA[j])?.hidden || !!view.nodes.get(d.linkB[j])?.hidden;
    return {
      key,
      from: d.keys[d.linkA[j]],
      to: d.keys[d.linkB[j]],
      lf: s?.lanesForward ?? d.lanesForward[j],
      lb: s?.lanesBack ?? d.lanesBack[j],
      narrow: s?.narrow ?? (d.linkFlags[j] & LINK.narrow) !== 0,
      width: s?.width ?? null,
      hidden,
      added: false,
    };
  }).filter((x) => !!x);
  const lf = common(linkValues.map((l) => l.lf)), lb = common(linkValues.map((l) => l.lb));
  const narrow = common(linkValues.map((l) => l.narrow)), width = common(linkValues.map((l) => l.width));
  // widths: all the same (a value, or all none), mixed, and whether any link has one
  const widthMixed = linkValues.some((l) => l.width !== linkValues[0].width);
  const anyWidth = linkValues.some((l) => l.width !== null);
  const linkLock: Reason | null = linkValues.some((l) => l.hidden) ? "reason.roads.hidden" : null;
  const setLinks = (change: Parameters<typeof setLinkValues>[3]) => st.change(setLinkValues(base, st.edits, links, change));
  const fromLanes = lf !== null && lb !== null && narrow !== null ? Math.max(1, lf + lb) * (narrow ? 4 : 5.5) : null;
  const one = linkValues.length === 1 ? linkValues[0] : null;

  return (
    <div className="edit-values">
      <Info help="help.roads.selection.count" block>
        {t("roads.selection.count", { nodes: nodes.length, links: links.length })}
      </Info>
      {shownNodes.length > 0 && (
        <div className="edit-group">
          {shownNodes.length === 1 && (
            <Info help="help.roads.node" block className="edit-item-title">
              {t(isAdded(shownNodes[0].key) ? "roads.node.added" : "roads.node.game", { key: shownNodes[0].key })}
              {stateText(t, shownNodes[0].n!)}
            </Info>
          )}
          <Select help="help.roads.street" label={t("roads.street")} value={street === null ? "" : String(street)} options={streets} disabledReason={nodeLock}
            onChange={(v) => (v === NEW_STREET ? setNaming({ hash: null }) : v !== "" && setNodes({ street: Number(v) }))} />
          {addedStreet !== null && (
            <Button help="help.roads.street.edit" variant="link" disabledReason={nodeLock} onClick={() => setNaming({ hash: addedStreet })}>
              {t("roads.street.edit")}
            </Button>
          )}
          {naming && (
            <StreetNameDialog
              name={naming.hash === null ? null : (added[String(naming.hash)] ?? null)}
              japanese={japanese}
              gameNames={englishNames(naming.hash).game}
              addedNames={englishNames(naming.hash).added}
              onClose={() => setNaming(null)}
              onDone={named}
            />
          )}
          <div className="edit-checks">
            {(
              [
                ["highway", NODE.highway, "help.roads.highway"],
                ["tunnel", NODE.tunnel, "help.roads.tunnel"],
                ["unpaved", NODE.unpaved, "help.roads.unpaved"],
                ["switchedOff", NODE.switchedOff, "help.roads.switchedOff"],
              ] as const
            ).map(([name, bit, help]) => (
              <Check key={name} help={help} checked={flag(bit) === true} disabledReason={nodeLock} onChange={(v) => setNodes({ [name]: v })}>
                {t(`roads.${name}` as MessageKey)}
                {flag(bit) === null ? t("roads.mixed") : ""}
              </Check>
            ))}
          </div>
          <NumberField help="help.roads.height" label={t("roads.height")} value={z} unit="m" step={0.1} min={-500} max={3000} placeholder={t("roads.mixedValue")}
            disabledReason={nodeLock} onChange={(v) => v !== null && setNodes({ z: v })} />
          {shownNodes.length === 1 && (
            <Info help="help.roads.position" block className="muted">
              {t("roads.position", { x: shownNodes[0].n!.x.toFixed(2), y: shownNodes[0].n!.y.toFixed(2) })}
            </Info>
          )}
          {shownNodes.length === 1 && !isAdded(shownNodes[0].key) && <GameNode k={shownNodes[0].key} streetName={streetName} />}
        </div>
      )}
      {linkValues.length > 0 && (
        <div className="edit-group">
          {one && (
            <Info help="help.roads.link" block className="edit-item-title">
              {t(one.added ? "roads.link.added" : "roads.link.game", { from: one.from, to: one.to })}
              {one.hidden ? t("roads.state.hidden") : ""}
            </Info>
          )}
          <NumberField help="help.roads.lanesForward" label={one ? t("roads.lanesForward.one", { from: one.from, to: one.to }) : t("roads.lanesForward")} value={lf}
            min={0} max={MAX_LANES} placeholder={t("roads.mixedValue")} disabledReason={linkLock} onChange={(v) => v !== null && setLinks({ lanesForward: v })} />
          <NumberField help="help.roads.lanesBack" label={one ? t("roads.lanesBack.one", { from: one.from, to: one.to }) : t("roads.lanesBack")} value={lb}
            min={0} max={MAX_LANES} placeholder={t("roads.mixedValue")} disabledReason={linkLock} onChange={(v) => v !== null && setLinks({ lanesBack: v })} />
          <Check help="help.roads.narrow" checked={narrow === true} disabledReason={linkLock} onChange={(v) => setLinks({ narrow: v })}>
            {t("roads.narrow")}
            {narrow === null ? t("roads.mixed") : ""}
          </Check>
          <NumberField help="help.roads.width" label={t("roads.width")} value={width} unit="m" step={0.5} min={0.5} max={MAX_WIDTH} allowEmpty
            placeholder={widthMixed ? t("roads.mixedValue") : fromLanes !== null ? t("roads.width.fromLanes", { w: fromLanes.toFixed(1) }) : t("roads.width.fromLanesEach")}
            disabledReason={linkLock} onChange={(v) => setLinks({ width: v })} />
          <Button help="help.roads.width.clear" variant="link" disabledReason={linkLock ?? (anyWidth ? null : "reason.roads.noWidth")} onClick={() => setLinks({ width: null })}>
            {t("roads.width.clear")}
          </Button>
          {one && !one.added && <GameLink from={one.from} to={one.to} />}
        </div>
      )}
    </div>
  );
}

function stateText(t: ReturnType<typeof useT>, n: { hidden: boolean; moved: boolean; changed: boolean }): string {
  const parts = [n.hidden ? t("roads.state.hidden") : "", n.moved ? t("roads.state.moved") : "", n.changed ? t("roads.state.changed") : ""].filter(Boolean);
  return parts.join("");
}

/** The node's values as the game's data holds them (all fields). */
function GameNode({ k, streetName }: { k: string; streetName(h: number): string }) {
  const t = useT();
  const [values, setValues] = useState<Record<string, unknown> | null>(null);
  useEffect(() => {
    let live = true;
    setValues(null);
    void api.roadNode(k).then((n) => live && setValues(n.values), () => live && setValues(null));
    return () => {
      live = false;
    };
  }, [k]);
  if (!values) return null;
  return (
    <Info help="help.roads.gameValues" block>
      <div className="edit-subtitle">{t("roads.gameValues")}</div>
      <table className="point-table">
        <tbody>
          {Object.entries(values).map(([name, v]) => (
            <tr key={name}>
              <td className="muted">{name}</td>
              <td>{name === "street" && typeof v === "number" && v !== 0 ? `${streetName(v)}` : JSON.stringify(v)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Info>
  );
}

/** The link's records as the game's data holds them (one per direction listed). */
function GameLink({ from, to }: { from: string; to: string }) {
  const t = useT();
  const [records, setRecords] = useState<Record<string, unknown>[] | null>(null);
  useEffect(() => {
    let live = true;
    setRecords(null);
    void api.roadLink(from, to).then((l) => live && setRecords(l.records), () => live && setRecords(null));
    return () => {
      live = false;
    };
  }, [from, to]);
  if (!records) return null;
  return (
    <Info help="help.roads.gameValues" block>
      <div className="edit-subtitle">{t("roads.gameValues")}</div>
      {records.map((r, i) => (
        <table key={i} className="point-table">
          <tbody>
            {Object.entries(r).map(([name, v]) => (
              <tr key={name}>
                <td className="muted">{name}</td>
                <td>{JSON.stringify(v)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ))}
    </Info>
  );
}

export { pairOf };
