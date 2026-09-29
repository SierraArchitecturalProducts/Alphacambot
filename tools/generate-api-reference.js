#!/usr/bin/env node
/**
 * generate-api-reference.js
 *
 * Builds data/acamapi-reference.json from YOUR OWN copy of ALPHACAM's API
 * help file (ACAMAPI.chm). The JSON powers the optional alphacam_api_help
 * tool; everything else in Alphacambot works without it.
 *
 * The CHM ships with ALPHACAM and is Hexagon's copyrighted documentation,
 * which is why the parsed JSON is not distributed with this repository —
 * you generate it locally from your licensed installation, for your own use.
 *
 * Usage (Windows):
 *   node tools/generate-api-reference.js "C:\path\to\ACAMAPI.chm"
 *   node tools/generate-api-reference.js            (searches ALPHACAM installs)
 *   node tools/generate-api-reference.js --extracted C:\already\extracted\dir
 *
 * Decompiling uses hh.exe, which is built into Windows. On other platforms,
 * extract the CHM yourself (7-Zip opens it) and use --extracted.
 *
 * Output: src/alphacam-mcp-server/data/acamapi-reference.json
 */

import { execFileSync } from 'node:child_process';
import {
  existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT_PATH = resolve(__dirname, '..', 'src', 'alphacam-mcp-server', 'data', 'acamapi-reference.json');

// ── Locate / decompile the CHM ─────────────────────────────────────

function findChm() {
  const roots = ['C:/Program Files/Hexagon', 'C:/Program Files (x86)/Hexagon', 'C:/Program Files/Vero Software'];
  for (const root of roots) {
    if (!existsSync(root)) continue;
    const stack = [root];
    while (stack.length) {
      const dir = stack.pop();
      let entries;
      try { entries = readdirSync(dir, { withFileTypes: true }); } catch { continue; }
      for (const e of entries) {
        const p = join(dir, e.name);
        if (e.isDirectory()) stack.push(p);
        else if (/^acamapi\.chm$/i.test(e.name)) return p;
      }
    }
  }
  return null;
}

function decompile(chmPath) {
  const outDir = mkdtempSync(join(tmpdir(), 'acamapi-'));
  // hh.exe is part of Windows (HTML Help). -decompile extracts the CHM.
  execFileSync('hh.exe', ['-decompile', outDir, chmPath], { stdio: 'ignore' });
  // hh.exe returns before finishing on some systems; poll briefly for content.
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    if (existsSync(join(outDir, 'Objects'))) break;
    execFileSync(process.platform === 'win32' ? 'cmd' : 'sleep',
      process.platform === 'win32' ? ['/c', 'ping', '-n', '2', '127.0.0.1', '>nul'] : ['1'],
      { stdio: 'ignore' });
  }
  if (!existsSync(join(outDir, 'Objects'))) {
    throw new Error(`hh.exe did not produce an Objects folder in ${outDir}`);
  }
  return outDir;
}

// ── HTML helpers (the CHM pages are regular MadCap output; no DOM needed) ──

function decodeEntities(s) {
  return s
    .replace(/&#160;|&nbsp;/g, ' ')
    .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"').replace(/&#39;|&apos;/g, "'");
}

function stripTags(s) {
  return decodeEntities(s.replace(/<[^>]+>/g, '')).replace(/\s+/g, ' ').trim();
}

/** Return [{cls, text}] for every <p class="...">...</p> in document order. */
function paragraphs(html) {
  const out = [];
  const re = /<p class="([^"]+)"[^>]*>([\s\S]*?)<\/p>/g;
  let m;
  while ((m = re.exec(html)) !== null) {
    out.push({ cls: m[1], text: stripTags(m[2]) });
  }
  return out;
}

function readHtml(p) {
  // Pages declare Windows-1252; latin1 is byte-compatible for our purposes.
  return readFileSync(p, 'latin1');
}

// ── Page parsers ───────────────────────────────────────────────────

function parseObjectPage(html) {
  const ps = paragraphs(html);
  const body = ps.filter((p) => p.cls === 'Body-Text' && p.text);
  return body.map((p) => p.text).join(' ');
}

function parsePropertiesPage(html) {
  const props = {};
  for (const p of paragraphs(html)) {
    if (p.cls !== 'Body-Text' || !p.text) continue;
    // Formats seen in the wild:
    //   "Name - (type) description"        "Name -(type) description"
    //   "Name (type) description"          "Name (Type) - description"
    //   "Name (see X) - (type) description"
    const m = p.text.match(
      /^([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(see [^)]*\)\s*)?(-?)\s*(?:\(([^)]+)\)\s*)?(-?)\s*(.*)$/,
    );
    if (!m) continue;
    const [, name, dash1, type, dash2, rawDesc] = m;
    // Require a separator (dash or a parenthesised type) so plain prose
    // paragraphs on the page don't get mistaken for property rows.
    if (!dash1 && !dash2 && !type) continue;
    const desc = rawDesc.trim();
    // Pure cross-reference rows ("Attribute - See Attributes") aren't properties.
    if (!type && /^See\s+[A-Za-z]+$/.test(desc)) continue;
    props[name] = {
      type: (type || '').trim(),
      description: desc,
      readOnly: /read[\s-]?only/i.test(desc),
      writeOnly: /write[\s-]?only/i.test(desc),
    };
  }
  return props;
}

function parseMethodPage(html) {
  const ps = paragraphs(html);
  const titleMatch = html.match(/<h4[^>]*>([\s\S]*?)<\/h4>/);
  const title = titleMatch ? stripTags(titleMatch[1]) : '';

  const method = {
    description: '', syntax: '', parameters: [],
    returnType: '', returnDescription: '', remarks: '', example: '',
  };

  let section = 'description';
  const descParts = [], remarkParts = [], exampleParts = [];

  for (const p of ps) {
    const t = p.text;
    if (!t) continue;
    if (/^Syntax$/i.test(t)) { section = 'syntax'; continue; }
    if (/^Remarks$/i.test(t)) { section = 'remarks'; continue; }
    if (/^Example[s]?:?$/i.test(t)) { section = 'example'; continue; }

    if (p.cls === 'CodeExplained' || p.cls === 'Code') {
      if (section === 'syntax' && !method.syntax) method.syntax = t;
      else if (section === 'example' || section === 'remarks' || method.syntax) exampleParts.push(t);
      continue;
    }

    // Body-Text lines
    if (section === 'description') { descParts.push(t); continue; }
    if (section === 'syntax') {
      const ret = t.match(/^\[Return\]\s+(\S+)\s*(.*)$/);
      if (ret) {
        method.returnType = ret[1];
        method.returnDescription = `[Return] ${ret[1]} ${ret[2]}`.trim();
        continue;
      }
      // "type Name  description" parameter lines
      const pm = t.match(/^([A-Za-z_][A-Za-z0-9_.]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s{1,}(.*)$/);
      if (pm) {
        method.parameters.push({ type: pm[1], name: pm[2], description: pm[3].trim() });
      }
      continue;
    }
    if (section === 'remarks') { remarkParts.push(t); continue; }
    if (section === 'example') { exampleParts.push(t); continue; }
  }

  method.description = descParts.join(' ');
  method.remarks = remarkParts.join(' ');
  method.example = exampleParts.join('\n');
  return { title, method };
}

function parseEnumPage(html) {
  const lines = [];
  for (const p of paragraphs(html)) {
    if (p.cls !== 'Body-Text' || !p.text) continue;
    // First line: "(EnumName) description" -> normalize to "( EnumName ) description"
    const head = p.text.match(/^\(\s*([A-Za-z0-9_]+)\s*\)\s*(.*)$/);
    if (head && lines.length === 0) {
      lines.push(`( ${head[1]} ) ${head[2]}`.trim());
      continue;
    }
    lines.push(p.text);
  }
  return lines;
}

function parseEventPage(html) {
  const ps = paragraphs(html);
  const ev = { description: '', syntax: '', parameters: [] };
  let section = 'description';
  const descParts = [];
  for (const p of ps) {
    const t = p.text;
    if (!t) continue;
    if (/^Syntax$/i.test(t)) { section = 'syntax'; continue; }
    if (p.cls === 'CodeExplained' || p.cls === 'Code') {
      if (!ev.syntax) ev.syntax = t;
      continue;
    }
    if (section === 'description') descParts.push(t);
  }
  ev.description = descParts.join(' ');
  return ev;
}

// ── Walk the extracted tree ────────────────────────────────────────

function build(extractedDir) {
  const objectsDir = join(extractedDir, 'Objects');
  const enumsDir = join(extractedDir, 'Enums');
  const eventsDir = join(extractedDir, 'Events');

  const objects = {};
  for (const objName of readdirSync(objectsDir)) {
    const dir = join(objectsDir, objName);
    let files;
    try { files = readdirSync(dir); } catch { continue; }
    const obj = { description: '', properties: {}, methods: {} };
    for (const f of files) {
      if (!/\.htm/i.test(f)) continue;
      const html = readHtml(join(dir, f));
      if (new RegExp(`^${objName}Object\\.htm`, 'i').test(f)) {
        obj.description = parseObjectPage(html);
      } else if (new RegExp(`^${objName}Properties\\.htm`, 'i').test(f)) {
        Object.assign(obj.properties, parsePropertiesPage(html));
      } else if (f.startsWith(`${objName}_`)) {
        const { title, method } = parseMethodPage(html);
        // The filename is the cleanest source for the method name: page
        // titles sometimes carry qualifiers ("CreateWireData (Wire only)")
        // or stray casing. Keep any title qualifier in the description.
        const mName = f.replace(`${objName}_`, '').replace(/\.htm.?$/i, '')
          || (title.includes('.') ? title.split('.').pop().trim() : '');
        const qual = title.match(/\(([^)]+)\)\s*$/);
        if (qual && !method.description.includes(qual[1])) {
          method.description = `(${qual[1]}) ${method.description}`.trim();
        }
        if (mName) obj.methods[mName] = method;
      }
    }
    if (obj.description || Object.keys(obj.methods).length || Object.keys(obj.properties).length) {
      objects[objName] = obj;
    }
  }

  const enums = {};
  if (existsSync(enumsDir)) {
    for (const f of readdirSync(enumsDir)) {
      if (!/\.htm/i.test(f)) continue;
      const name = f.replace(/\.htm.?$/i, '');
      const lines = parseEnumPage(readHtml(join(enumsDir, f)));
      if (lines.length) enums[name] = lines;
    }
  }

  const events = {};
  if (existsSync(eventsDir)) {
    for (const f of readdirSync(eventsDir)) {
      if (!/\.htm/i.test(f)) continue;
      const name = f.replace(/\.htm.?$/i, '').replace(/Event$/, 'Event');
      events[name] = parseEventPage(readHtml(join(eventsDir, f)));
    }
  }

  return {
    _meta: {
      source: 'ACAMAPI.chm (parsed locally from your ALPHACAM installation)',
      generated: new Date().toISOString(),
      generator: 'tools/generate-api-reference.js',
      note: 'Content derived from Hexagon documentation for local use; do not redistribute.',
    },
    objects, enums, events,
  };
}

// ── Main ───────────────────────────────────────────────────────────

const args = process.argv.slice(2);
let extractedDir = null;
let cleanup = null;

const exIdx = args.indexOf('--extracted');
if (exIdx !== -1) {
  extractedDir = resolve(args[exIdx + 1] || '');
} else {
  let chm = args.find((a) => /\.chm$/i.test(a));
  if (!chm) {
    console.log('No CHM given; searching ALPHACAM installations...');
    chm = findChm();
    if (!chm) {
      console.error('Could not find ACAMAPI.chm. Pass its path:\n  node tools/generate-api-reference.js "C:\\...\\ACAMAPI.chm"');
      process.exit(1);
    }
    console.log(`Found: ${chm}`);
  }
  console.log('Decompiling with hh.exe ...');
  extractedDir = decompile(resolve(chm));
  cleanup = extractedDir;
}

if (!existsSync(join(extractedDir, 'Objects'))) {
  console.error(`No Objects/ folder under ${extractedDir} - is this an extracted ACAMAPI.chm?`);
  process.exit(1);
}

const ref = build(extractedDir);
mkdirSync(dirname(OUT_PATH), { recursive: true });
writeFileSync(OUT_PATH, JSON.stringify(ref, null, 1));

const nObj = Object.keys(ref.objects).length;
const nMeth = Object.values(ref.objects).reduce((n, o) => n + Object.keys(o.methods).length, 0);
const nProp = Object.values(ref.objects).reduce((n, o) => n + Object.keys(o.properties).length, 0);
console.log(`Wrote ${OUT_PATH}`);
console.log(`  objects: ${nObj}, methods: ${nMeth}, properties: ${nProp}, enums: ${Object.keys(ref.enums).length}, events: ${Object.keys(ref.events).length}`);

if (cleanup) { try { rmSync(cleanup, { recursive: true, force: true }); } catch { } }
