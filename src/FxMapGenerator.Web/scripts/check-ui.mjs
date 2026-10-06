// Checks that every screen item has hover help, before the type check and the bundle:
//  1. raw <button>, <input>, <select>, <textarea>, <a> and <th> may only appear in src/shared/controls/, whose
//     components require a help key; everywhere else the controls must be used;
//  2. every help.* and reason.* text in the dictionary is used somewhere (no stale help);
//  3. every step of the screens' guides (src/guide/guides.ts) points at an item a screen has: a help key a screen uses
//     (the guide table itself does not count), or exactly one data-guide="<step name>" on a screen; and every guide text
//     in the dictionary (guide.<step name>.title / .text) belongs to a step.
// That both dictionaries have every key is checked by the type checker (en.ts is Record<MessageKey, string>).
// usage: node scripts/check-ui.mjs
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const src = join(root, "src");
const controls = join(src, "shared", "controls") + sep;
const locales = join(src, "shared", "locales") + sep;
const guideTable = join(src, "guide", "guides.ts");

function walk(dir) {
  return readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}

const files = walk(src).filter((p) => /\.(ts|tsx)$/.test(p));
const errors = [];

const raw = /<(button|input|select|textarea|a|th)[\s>/]/g;
for (const file of files) {
  if (!file.endsWith(".tsx") || file.startsWith(controls)) continue;
  const text = readFileSync(file, "utf8");
  for (const m of text.matchAll(raw)) {
    const line = text.slice(0, m.index).split("\n").length;
    errors.push(`${relative(root, file)}:${line}: raw <${m[1]}>; use the controls in src/shared/controls (they carry the hover help)`);
  }
}

const dictionary = readFileSync(join(locales, "ja.ts"), "utf8");
const keys = [...dictionary.matchAll(/^\s*"((?:help|reason)\.[^"]+)":/gm)].map((m) => m[1]);
const code = files.filter((f) => !f.startsWith(locales) && f !== guideTable).map((f) => readFileSync(f, "utf8")).join("\n");
for (const key of keys) {
  if (!code.includes(`"${key}"`)) errors.push(`src/shared/locales/ja.ts: "${key}" is not used by any screen item`);
}

const table = readFileSync(guideTable, "utf8");
const steps = [...table.matchAll(/step\("([\w.]+)",\s*(?:AREA|"([^"]+)")/g)].map((m) => ({ name: m[1], help: m[2] ?? null }));
const tsx = files.filter((f) => f.endsWith(".tsx")).map((f) => readFileSync(f, "utf8")).join("\n");
const names = new Set();
for (const s of steps) {
  if (names.has(s.name)) errors.push(`src/guide/guides.ts: step "${s.name}" is there twice`);
  names.add(s.name);
  if (s.help) {
    if (!code.includes(`"${s.help}"`)) errors.push(`src/guide/guides.ts: step "${s.name}" points at "${s.help}", which no screen item has`);
  } else {
    const n = tsx.split(`data-guide="${s.name}"`).length - 1;
    if (n !== 1) errors.push(`src/guide/guides.ts: step "${s.name}" needs data-guide="${s.name}" on one screen element (found ${n})`);
  }
}
for (const m of dictionary.matchAll(/^\s*"guide\.([\w.]+)\.(title|text)":/gm)) {
  if (!names.has(m[1])) errors.push(`src/shared/locales/ja.ts: "guide.${m[1]}.${m[2]}" belongs to no step of src/guide/guides.ts`);
}

if (errors.length) {
  console.error(errors.join("\n"));
  console.error(`check-ui: ${errors.length} problem(s)`);
  process.exit(1);
}
console.log(`check-ui: ${files.length} files, ${keys.length} help texts, ${steps.length} guide steps, all good`);
