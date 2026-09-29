/**
 * User preferences for the Alphacam bridge.
 *
 * A small JSON file, per Windows user, that any connecting MCP agent can read
 * and update — so the agent stops needing reminders about this shop's setup
 * (tool-catalog folder, a safe routing tool, the post processor and where NC
 * goes, units and material defaults).
 *
 * Location: %LOCALAPPDATA%\AlphacamBridge\preferences.json  (same folder as
 * the logs). Falls back to ~/.alphacambridge/preferences.json off Windows.
 *
 * Nothing here talks to Alphacam — preferences are advisory metadata the agent
 * consults; the agent still issues the normal tools (select_tool, select_post…)
 * using these values.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';

function prefsDir() {
  const local = process.env.LOCALAPPDATA;
  if (local) return join(local, 'AlphacamBridge');
  return join(homedir(), '.alphacambridge');
}

export const PREFS_PATH = join(prefsDir(), 'preferences.json');

// The recognised keys, with one-line docs — surfaced to the agent so it knows
// what it may store. Unknown keys are allowed too (free-form), but these are
// the ones the tools advertise.
export const KNOWN_KEYS = {
  toolCatalogFolder:
    'Full path to your .art/.amt tool library folder. list_tools scans here first.',
  defaultRoutingTool:
    'Full path to a tool file to prefer for routing/profiling. Avoids post-reserved (e.g. drill) tool numbers.',
  postFile: 'Full path to your post processor (.arb). Do not assume Xilog.',
  ncOutputDir: 'Directory where posted NC files should be written.',
  units: 'Working units: "mm" or "inch".',
  defaultMaterial: 'Default material name for set_material.',
  defaultPanelThickness: 'Default panel/stock thickness for specify_panel.',
  notes: 'Free-form notes the agent should keep in mind for this shop.',
};

function emptyPrefs() {
  return { _schema: 1, _path: PREFS_PATH, updatedAt: null };
}

/** Read preferences from disk. Never throws; returns an object always. */
export function loadPreferences() {
  try {
    if (!existsSync(PREFS_PATH)) return emptyPrefs();
    const raw = readFileSync(PREFS_PATH, 'utf-8');
    const obj = JSON.parse(raw);
    if (obj && typeof obj === 'object') {
      obj._path = PREFS_PATH;
      return obj;
    }
  } catch {
    // fall through to empty on parse/read error
  }
  return emptyPrefs();
}

/** Persist preferences to disk, creating the folder if needed. */
function savePreferences(prefs) {
  const dir = dirname(PREFS_PATH);
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  prefs.updatedAt = new Date().toISOString();
  const toWrite = { ...prefs };
  delete toWrite._path; // derived, not stored
  writeFileSync(PREFS_PATH, JSON.stringify(toWrite, null, 2), 'utf-8');
  return loadPreferences();
}

/**
 * Set one key (optionally a dotted path like "post.file") to a JSON value,
 * or deep-merge a whole patch object. Returns the new full preferences.
 *   setPreference({ key: "toolCatalogFolder", value: "C:\\..." })
 *   setPreference({ patch: { units: "mm", defaultMaterial: "MDF" } })
 *   setPreference({ key: "toolCatalogFolder", clear: true })   // remove a key
 */
export function setPreference(args = {}) {
  const prefs = loadPreferences();

  if (args.patch && typeof args.patch === 'object') {
    for (const [k, v] of Object.entries(args.patch)) {
      if (v === null) delete prefs[k];
      else prefs[k] = v;
    }
    return savePreferences(prefs);
  }

  const key = args.key;
  if (!key || typeof key !== 'string') {
    throw new Error('Pass { key, value }, or { patch: {…} }. key must be a string.');
  }

  const parts = key.split('.');
  if (args.clear) {
    // walk to parent and delete leaf
    let node = prefs;
    for (let i = 0; i < parts.length - 1; i++) {
      if (node[parts[i]] == null || typeof node[parts[i]] !== 'object') return prefs;
      node = node[parts[i]];
    }
    delete node[parts[parts.length - 1]];
    return savePreferences(prefs);
  }

  if (!('value' in args)) {
    throw new Error('Pass a "value" (or "clear": true) alongside "key".');
  }
  let node = prefs;
  for (let i = 0; i < parts.length - 1; i++) {
    if (node[parts[i]] == null || typeof node[parts[i]] !== 'object') node[parts[i]] = {};
    node = node[parts[i]];
  }
  node[parts[parts.length - 1]] = args.value;
  return savePreferences(prefs);
}

/** A compact, human-readable summary for embedding in status output. */
export function summarisePreferences(prefs) {
  const p = prefs || loadPreferences();
  const keys = Object.keys(p).filter((k) => !k.startsWith('_') && k !== 'updatedAt');
  if (keys.length === 0) {
    return {
      configured: false,
      path: PREFS_PATH,
      hint: 'No user preferences set. If the user names their tool-catalog folder, preferred post, units, etc., store them with alphacam_set_preference so future sessions need no reminder.',
    };
  }
  const out = { configured: true, path: PREFS_PATH };
  for (const k of keys) out[k] = p[k];
  return out;
}
