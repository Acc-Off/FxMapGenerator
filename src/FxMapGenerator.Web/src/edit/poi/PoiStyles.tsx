import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { api, ApiError } from "../../shared/api";
import { Button, Check, FileButton, Info, NumberField, type Reason, TextField } from "../../shared/controls";
import { errorText, type MessageKey, useI18nStore, useT } from "../../shared/i18n";
import { itemName, type PoiEditStyle, type PoiLabel, type PoiLook, type StyleValues } from "../../shared/types";
import { ColorBox } from "../styles/StyleControls";
import { freeStyleId, LABEL_SCALE, newStyle, pointKey, type Resolved, resolveAll, styleUse, withLook } from "./model";
import { IconPicker, IconSvg } from "./PoiDialogs";
import { drawPoint, forgetImages, type PoiLayerState } from "./poiLayer";
import { useLabelLang, usePoiEditor } from "./store";

const LOOKS: readonly PoiLook[] = ["text", "badge", "dot", "icon"];
const LOOK_TEXT: Record<PoiLook, MessageKey> = { text: "poi.look.text", badge: "poi.look.badge", dot: "poi.look.dot", icon: "poi.look.icon" };

/** A small picture of a style in the lists (its mark about 18 px, the text "A"). */
export function StyleSwatch({ style }: { style: PoiEditStyle }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const icons = usePoiEditor((s) => s.icons);
  const fonts = usePoiEditor((s) => s.dto?.fonts);
  useLayoutEffect(() => {
    const c = canvas.current;
    if (!c) return;
    const dpr = window.devicePixelRatio || 1;
    c.width = 22 * dpr;
    c.height = 22 * dpr;
    const ctx = c.getContext("2d")!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, 22, 22);
    // the mark (or the text) about 16 px high
    const across = style.look === "icon" && style.badgeColor ? 1.5 * style.size : style.look === "badge" ? 1.6 * style.size : style.size;
    const ppm = 16 / Math.max(0.1, across);
    const r = swatchPoint({ ...style, showLabel: false });
    const state: PoiLayerState = { points: [r], selection: new Set(), drag: null, fonts: fonts ?? {}, lang: "en", icons };
    drawPoint(ctx, r, state, 11, 11, ppm, icons);
  }, [style, icons, fonts]);
  return <canvas ref={canvas} className="poi-swatch" style={{ width: 22, height: 22 }} />;
}

/** A point of a style for its samples on the screen. */
function swatchPoint(style: PoiEditStyle): Resolved {
  const point = { id: "sample", name: "", label: { en: "A" }, x: 0, y: 0, style: style.id, show: null, color: null, size: null, visible: null, locked: null };
  const group = { path: "", points: [point], name: null, order: 0, style: null, show: null, visible: true, locked: false, credit: null };
  return {
    key: pointKey("", "sample"),
    group,
    point,
    style,
    show: { atlas: true, roadmap: false },
    color: style.color,
    size: style.size,
    visible: true,
    locked: false,
    labelSize: style.labelSize ?? LABEL_SCALE * style.size,
  };
}

/**
 * The POI style chosen on the styles side: its name, how it draws (text, text in a circle, circle, icon), the icon
 * (MDI or a PNG), colours, sizes, outline, the circle behind an icon, the label beside; samples drawn as the maps draw
 * it; adding, copying and deleting styles. The bundled styles are shown, not changed.
 */
export function PoiStyleEditor() {
  const t = useT();
  const lang = useI18nStore((s) => s.lang);
  const st = usePoiEditor();
  const [picking, setPicking] = useState(false);
  const set = st.set!;
  const bundled = st.dto!.bundledStyles;
  const all = [...bundled, ...set.styles];
  const style = all.find((s) => s.id === st.styleId) ?? null;
  const isBundled = !!style && bundled.some((b) => b.id === style.id);
  const taken = useMemo(() => new Set(all.map((s) => s.id)), [all]);
  const disabled: Reason | null = isBundled ? "reason.poi.style.bundled" : null;
  const use = style ? styleUse(set, style.id) : null;
  const used = !!use && use.folders + use.groups + use.points > 0;
  const colors = { colors: all.flatMap((s) => [s.color, s.outline, s.badgeColor]) } as unknown as StyleValues;

  const change = (next: PoiEditStyle) => st.change({ ...set, styles: set.styles.map((s) => (s.id === next.id ? next : s)) });
  const add = () => {
    const id = freeStyleId("New style", taken);
    const made = newStyle(id, t("poi.style.new"));
    st.change({ ...set, styles: [...set.styles, made] });
    st.setStyleId(id);
  };
  const copy = () => {
    if (!style) return;
    // one name in the screen's language (a bundled style's copy too)
    const id = freeStyleId(`${style.id}-copy`, taken);
    const made: PoiEditStyle = { ...style, id, name: t("poi.style.copyOf", { name: itemName(style.name, lang) || style.id }) };
    st.change({ ...set, styles: [...set.styles, made] });
    st.setStyleId(id);
  };
  const remove = () => {
    if (!style || !window.confirm(t("poi.style.delete.confirm", { name: itemName(style.name, lang) || style.id }))) return;
    st.change({ ...set, styles: set.styles.filter((s) => s.id !== style.id) });
    st.setStyleId(null);
  };
  const pickImage = async (file: File) => {
    if (!style) return;
    try {
      const r = await api.poiImage(file);
      forgetImages();
      change({ ...style, image: r.image, icon: null });
    } catch (e) {
      st.setNotice(e instanceof ApiError ? errorText(e.code, e.message) : String(e));
    }
  };

  return (
    <>
      <Info help="help.poi.style.title" block className="section-title">
        {t("poi.style.title")}
      </Info>
      <div className="edit-buttons">
        <Button help="help.poi.style.add" onClick={add}>
          {t("poi.style.add")}
        </Button>
        <Button help="help.poi.style.copy" disabledReason={!style ? "reason.poi.style.none" : null} onClick={copy}>
          {t("poi.style.copy")}
        </Button>
        <Button help="help.poi.style.delete" variant="danger" disabledReason={!style ? "reason.poi.style.none" : disabled ?? (used ? { key: "reason.poi.style.inUse", params: { ...use! } } : null)} onClick={remove}>
          {t("poi.style.delete")}
        </Button>
      </div>
      {!style ? (
        <Info help="help.poi.style.choose" block className="muted">
          {t("poi.style.choose")}
        </Info>
      ) : (
        <>
          {isBundled && (
            <Info help="help.poi.style.bundledNote" block className="style-bundled-note">
              {t("poi.style.bundledNote")}
            </Info>
          )}
          <Info help="help.poi.style.use" block className="muted">
            {t("poi.style.use", { id: style.id, folders: use!.folders, groups: use!.groups, points: use!.points })}
          </Info>
          {isBundled ? (
            <div className="poi-field">
              <Info help="help.poi.style.name" className="field-label">
                {t("poi.name")}
              </Info>
              <span className="poi-field-control">{itemName(style.name, lang) || style.id}</span>
            </div>
          ) : (
            <TextField help="help.poi.style.name" label={t("poi.name")} value={itemName(style.name, lang)} onChange={(v) => change({ ...style, name: v })} wide />
          )}
          <div className="poi-field">
            <Info help="help.poi.style.look" className="field-label">
              {t("poi.style.look")}
            </Info>
            <span className="style-choice-buttons">
              {LOOKS.map((look) => (
                <Button key={look} help="help.poi.style.look" pressed={style.look === look} disabledReason={disabled} onClick={() => change(withLook(style, look))}>
                  {t(LOOK_TEXT[look])}
                </Button>
              ))}
            </span>
          </div>
          {style.look === "icon" && (
            <div className="poi-field">
              <Info help="help.poi.style.icon" className="field-label">
                {t("poi.style.icon")}
              </Info>
              <span className="poi-field-control poi-icon-choice">
                {style.image ? (
                  <>
                    <img className="poi-icon-image" src={api.poiImageUrl(style.image)} alt="" />
                    <code>{style.image}</code>
                  </>
                ) : style.icon && st.icons?.icons[style.icon] ? (
                  <>
                    <IconSvg path={st.icons.icons[style.icon].path} />
                    <code>{style.icon}</code>
                  </>
                ) : (
                  <code>{style.icon ?? "—"}</code>
                )}
              </span>
              <span className="edit-buttons">
                <Button help="help.poi.style.pickIcon" disabledReason={disabled} onClick={() => setPicking(true)}>
                  {t("poi.style.pickIcon")}
                </Button>
                <FileButton help="help.poi.style.pickImage" accept=".png,image/png" disabledReason={disabled} onFile={(f) => void pickImage(f)}>
                  {t("poi.style.pickImage")}
                </FileButton>
              </span>
            </div>
          )}
          <div className="poi-field">
            <Info help="help.poi.style.color" className="field-label">
              {t(style.look === "badge" ? "poi.style.textColor" : "poi.color")}
            </Info>
            <ColorBox value={style.color} values={colors} disabled={disabled} help="help.poi.style.color" onSet={(hex) => change({ ...style, color: hex })} />
          </div>
          <NumberField help="help.poi.style.size" label={t("poi.size")} value={style.size} min={0.1} step={0.1} unit="m" disabledReason={disabled}
            onChange={(v) => v !== null && v > 0 && change({ ...style, size: v })} />
          {(style.look === "text" || style.look === "badge" || style.showLabel) && (
            <div className="poi-field">
              <Info help="help.poi.style.weight" className="field-label">
                {t("poi.style.weight")}
              </Info>
              <span className="style-choice-buttons">
                <Button help="help.poi.style.weight" pressed={style.weight === "normal"} disabledReason={disabled} onClick={() => change({ ...style, weight: "normal" })}>
                  {t("poi.weight.normal")}
                </Button>
                <Button help="help.poi.style.weight" pressed={style.weight === "bold"} disabledReason={disabled} onClick={() => change({ ...style, weight: "bold" })}>
                  {t("poi.weight.bold")}
                </Button>
              </span>
            </div>
          )}
          {style.look !== "badge" && (
            <div className="poi-field">
              <Check help="help.poi.style.outline" checked={style.outline !== null} disabledReason={disabled}
                onChange={(v) => change({ ...style, outline: v ? "#ffffff" : null, outlineWidth: v ? Math.max(style.outlineWidth, 2) : 0 })}>
                {t("poi.style.outline")}
              </Check>
              {style.outline !== null && (
                <>
                  <ColorBox value={style.outline} values={colors} disabled={disabled} help="help.poi.style.outlineColor" onSet={(hex) => change({ ...style, outline: hex })} />
                  <NumberField help="help.poi.style.outlineWidth" label={t("poi.style.outlineWidth")} value={style.outlineWidth} min={0} step={0.1} unit="m" disabledReason={disabled}
                    onChange={(v) => v !== null && v >= 0 && change({ ...style, outlineWidth: v })} />
                </>
              )}
            </div>
          )}
          {(style.look === "badge" || style.look === "icon") && (
            <div className="poi-field">
              {style.look === "icon" ? (
                <Check help="help.poi.style.badge" checked={style.badgeColor !== null} disabledReason={disabled} onChange={(v) => change({ ...style, badgeColor: v ? "#ffffff" : null })}>
                  {t("poi.style.badge")}
                </Check>
              ) : (
                <Info help="help.poi.style.badgeColor" className="field-label">
                  {t("poi.style.badgeColor")}
                </Info>
              )}
              {style.badgeColor !== null && (
                <ColorBox value={style.badgeColor} values={colors} disabled={disabled} help="help.poi.style.badgeColor" onSet={(hex) => change({ ...style, badgeColor: hex })} />
              )}
            </div>
          )}
          {(style.look === "dot" || style.look === "icon") && (
            <div className="poi-field">
              <Check help="help.poi.style.showLabel" checked={style.showLabel} disabledReason={disabled} onChange={(v) => change({ ...style, showLabel: v, labelSize: v ? style.labelSize : null })}>
                {t("poi.style.showLabel")}
              </Check>
              {style.showLabel && (
                <NumberField help="help.poi.style.labelSize" label={t("poi.style.labelSize")} value={style.labelSize} min={0.1} step={0.1} unit="m" allowEmpty
                  placeholder={String(Math.round(LABEL_SCALE * style.size * 10) / 10)} disabledReason={disabled}
                  onChange={(v) => change({ ...style, labelSize: v !== null && v > 0 ? v : null })} />
              )}
            </div>
          )}
          <Samples style={style} />
          {picking && (
            <IconPicker
              value={style.icon}
              onPick={(name) => {
                change({ ...style, icon: name, image: null });
                setPicking(false);
              }}
              onClose={() => setPicking(false)}
            />
          )}
        </>
      )}
    </>
  );
}

/**
 * The style drawn by the app as the maps draw it: at the in-game map's scale (zoom 6) and at the web map's closest
 * (zoom 8), its labels in the language of the labels shown.
 */
function Samples({ style }: { style: PoiEditStyle }) {
  const t = useT();
  const lang = useLabelLang();
  const [urls, setUrls] = useState<{ z6: string | null; z8: string | null; error: string | null }>({ z6: null, z8: null, error: null });
  const set = usePoiEditor((s) => s.set);
  const bundled = usePoiEditor((s) => s.dto?.bundledStyles);
  // the label of the first point drawn with the style (a marker's letter, a shop's name), else a sample text of each
  // language (the style's name is in the user's words: in the English font its letters may be missing)
  const first = useMemo(() => (set && bundled ? resolveAll(set, bundled).find((r) => r.style?.id === style.id)?.point.label : undefined), [set, bundled, style.id]);
  const name = useMemo<PoiLabel>(() => (first && (first.en || first.ja) ? first : { en: "Sample", ja: "見本" }), [first]);
  useEffect(() => {
    const abort = new AbortController();
    const made: string[] = [];
    const timer = window.setTimeout(() => {
      Promise.all([6, 8].map((z) => api.poiSample(style, name, lang, z, abort.signal))).then(
        ([a, b]) => {
          const [u6, u8] = [URL.createObjectURL(a), URL.createObjectURL(b)];
          made.push(u6, u8);
          setUrls({ z6: u6, z8: u8, error: null });
        },
        (e) => {
          if (abort.signal.aborted) return;
          setUrls({ z6: null, z8: null, error: e instanceof ApiError ? errorText(e.code, e.message) : String(e) });
        },
      );
    }, 300);
    return () => {
      abort.abort();
      window.clearTimeout(timer);
      for (const u of made) URL.revokeObjectURL(u);
    };
  }, [style, name, lang]);
  return (
    <div className="poi-samples">
      <Info help="help.poi.style.samples" block className="field-label">
        {t("poi.style.samples")}
      </Info>
      {urls.error ? (
        <Info help="help.poi.style.samples" block className="text-error">
          {urls.error}
        </Info>
      ) : (
        <div className="poi-sample-row">
          <figure>
            {urls.z6 && <img src={urls.z6} alt="" />}
            <figcaption>{t("poi.style.sample.z6")}</figcaption>
          </figure>
          <figure>
            {urls.z8 && <img src={urls.z8} alt="" />}
            <figcaption>{t("poi.style.sample.z8")}</figcaption>
          </figure>
        </div>
      )}
    </div>
  );
}


