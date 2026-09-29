#!/usr/bin/env node

/**
 * Alphacam MCP Server
 *
 * Bridges any MCP client (Claude Desktop, or any other MCP-capable LLM
 * agent or tool) to ALPHACAM CAM software via named pipe. The server
 * speaks standard Model Context Protocol over stdio — nothing in it is
 * client-specific.
 *
 * Architecture:
 *   MCP client <--stdio/MCP--> this server <--named pipe/JSON-RPC--> AlphacamBridge .NET DLL
 *
 * Usage:
 *   node src/index.js [--pipe AlphacamBridge] [--key anonymous]
 */

import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { PipeClient } from './pipe-client.js';
import { TOOL_DEFINITIONS, toBridgeMethod } from './tool-definitions.js';
import { parseArgs } from 'node:util';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

// ── API Reference (loaded once for alphacam_api_help) ────────────
const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
let apiReference = null;
try {
  const refPath = join(__dirname, '..', 'data', 'acamapi-reference.json');
  apiReference = JSON.parse(readFileSync(refPath, 'utf-8'));
  // Don't log here — stderr isn't ready yet
} catch {
  // Will be null if file not found; alphacam_api_help will report the error
}

// ── Parse CLI args ────────────────────────────────────────────

const { values: args } = parseArgs({
  options: {
    pipe: { type: 'string', default: 'AlphacamBridge' },
    key: { type: 'string', default: 'anonymous' },
  },
  strict: false,
});

const PIPE_NAME = args.pipe;
const API_KEY = args.key;

// ── Logging (to stderr so it doesn't interfere with stdio MCP) ──

function log(level, msg, ...extra) {
  const ts = new Date().toISOString();
  process.stderr.write(`[${ts}] [${level}] ${msg}\n`);
  if (extra.length) {
    for (const e of extra) {
      if (e instanceof Error) {
        process.stderr.write(`  ${e.stack ?? e.message}\n`);
      } else {
        process.stderr.write(`  ${JSON.stringify(e)}\n`);
      }
    }
  }
}

// ── Pipe client (lazy connect) ────────────────────────────────

const pipeClient = new PipeClient({ pipeName: PIPE_NAME, apiKey: API_KEY });
let connecting = null;

pipeClient.on('error', (err) => log('ERROR', 'Pipe error:', err));
pipeClient.on('close', () => log('WARN', 'Pipe connection closed'));

/**
 * Ensure the pipe client is connected. Reuses in-flight connect attempts.
 */
async function ensureConnected() {
  if (pipeClient.connected && pipeClient.authenticated) return;

  if (connecting) return connecting;

  connecting = (async () => {
    try {
      log('INFO', `Connecting to pipe: ${PIPE_NAME}`);
      await pipeClient.connect();
      log('INFO', 'Connected and authenticated to bridge');
    } finally {
      connecting = null;
    }
  })();

  return connecting;
}

// Per-method request timeout overrides (ms). user_select blocks on the
// user physically picking geometries in Alphacam, so it gets a long leash.
const METHOD_TIMEOUTS = {
  'alphacam.user_select': 5 * 60 * 1000,
  // DoNest computes for a while (>30 s observed on a 2-part/14-piece nest).
  'alphacam.nest': 10 * 60 * 1000,
  // These accept interactive:true (user picks geometries in Alphacam).
  'alphacam.transform_geometry': 5 * 60 * 1000,
  'alphacam.offset_geometry': 5 * 60 * 1000,
  'alphacam.fillet_geometry': 5 * 60 * 1000,
  'alphacam.join_geometries': 5 * 60 * 1000,
  'alphacam.explode_geometry': 5 * 60 * 1000,
  'alphacam.rough_finish': 5 * 60 * 1000,
  'alphacam.pocket': 5 * 60 * 1000,
  'alphacam.drill_tap': 5 * 60 * 1000,
  'alphacam.engrave': 5 * 60 * 1000,
  'alphacam.set_tool_side_auto': 5 * 60 * 1000,
  'alphacam.save_part': 5 * 60 * 1000,
  // Generic API calls can hit interactive members (UserSelect*, dialogs).
  'alphacam.api_invoke': 5 * 60 * 1000,
};

/**
 * Send a request to the bridge, reconnecting if needed.
 */
async function bridgeRequest(method, params = {}) {
  await ensureConnected();
  const timeoutMs = METHOD_TIMEOUTS[method];
  return timeoutMs
    ? pipeClient.request(method, params, timeoutMs)
    : pipeClient.request(method, params);
}

// ── Build MCP Server ──────────────────────────────────────────

const server = new McpServer({
  name: 'alphacam',
  version: '1.0.0',
});

// Register each Alphacam tool with the MCP server.
// We use z.object({}).passthrough() style by registering with raw schemas
// and handling validation ourselves (the bridge does its own validation).

for (const toolDef of TOOL_DEFINITIONS) {
  // MCP SDK's server.tool() expects a Zod schema, but we can also register
  // tools at a lower level. Since the SDK supports raw JSON schema via
  // server.resource and server.tool with annotations, we'll register using
  // the lower-level setRequestHandler approach.
  //
  // Actually, the simpler approach: use server.tool() with a callback and
  // pass parameters through. The MCP SDK allows registering tools with
  // just name + description + callback when no schema validation is needed
  // on the server side (the bridge validates).
}

// We need to use the lower-level Server API to register tools with
// raw JSON schemas (not Zod). Import what we need.
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js';

// Create lower-level Server (McpServer wraps this, but we need raw access)
const rawServer = new Server(
  { name: 'alphacam', version: '1.0.0' },
  {
    capabilities: {
      tools: {},
    },
  }
);

// Handle tools/list
rawServer.setRequestHandler(ListToolsRequestSchema, async () => {
  // First try to get live tool list from bridge (if connected)
  // Fall back to static definitions if bridge is unavailable
  let tools = TOOL_DEFINITIONS.map((t) => ({
    name: t.name,
    description: t.description,
    inputSchema: t.inputSchema,
  }));

  // Add meta-tools
  tools.push({
    name: 'alphacam_status',
    description:
      'Get the current status of the Alphacam bridge connection, including whether Alphacam is connected, object cache count, and version info.',
    inputSchema: { type: 'object', properties: {} },
  });

  tools.push({
    name: 'alphacam_api_help',
    description:
      'Look up detailed Alphacam COM API documentation from ACAMAPI.chm. Query by object name (e.g., "Drawing", "MillTool", "MillData"), method name (e.g., "Drawing.CreateCircle"), property name, or keyword. Returns full signatures, parameters, return types, descriptions, and examples. Use this to discover API capabilities beyond the primary tool set.',
    inputSchema: {
      type: 'object',
      properties: {
        query: {
          type: 'string',
          description:
            'What to look up. Examples: "Drawing" (list all Drawing methods/properties), "MillData" (all milling parameters), "Drawing.CreateCircle" (specific method), "MillTool.Diameter" (specific property), "pocket" (keyword search across all objects)',
        },
        category: {
          type: 'string',
          enum: ['objects', 'enums', 'events', 'all'],
          description: 'Category to search in (default: all)',
        },
      },
      required: ['query'],
    },
  });

  return { tools };
});

// Handle tools/call
rawServer.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: toolArgs } = request.params;

  log('INFO', `Tool call: ${name}`, toolArgs);

  // Handle alphacam_api_help locally (no bridge call needed)
  if (name === 'alphacam_api_help') {
    try {
      const result = handleApiHelp(toolArgs ?? {});
      return { content: [{ type: 'text', text: result }] };
    } catch (err) {
      return {
        content: [{ type: 'text', text: `Error: ${err.message}` }],
        isError: true,
      };
    }
  }

  try {
    // Convert MCP tool name to bridge method name
    const bridgeMethod = toBridgeMethod(name);
    const result = await bridgeRequest(bridgeMethod, toolArgs ?? {});

    // Format result for MCP - must return content array
    const text =
      typeof result === 'string' ? result : JSON.stringify(result, null, 2);

    return {
      content: [{ type: 'text', text }],
    };
  } catch (err) {
    log('ERROR', `Tool ${name} failed:`, err);

    return {
      content: [
        {
          type: 'text',
          text: `Error: ${err.message}${err.code ? ` (code: ${err.code})` : ''}`,
        },
      ],
      isError: true,
    };
  }
});

// ── API Help Handler ─────────────────────────────────────────

/**
 * Handle alphacam_api_help queries against the parsed ACAMAPI reference.
 * Supports: object names, "Object.Method", "Object.Property", keyword search.
 */
function handleApiHelp({ query, category }) {
  if (!apiReference) {
    return 'API reference not loaded. Ensure data/acamapi-reference.json exists next to src/.';
  }

  const q = (query || '').trim();
  if (!q) return 'Please provide a query (e.g., "Drawing", "MillData.AutoZ", "pocket").';

  const cat = category || 'all';
  const lines = [];

  // Case 1: "Object.Method" or "Object.Property" — direct lookup
  if (q.includes('.')) {
    const [objName, memberName] = q.split('.', 2);
    const obj = findObject(objName);
    if (!obj) return `Object "${objName}" not found. Available: ${listObjectNames().join(', ')}`;

    // Check methods first, then properties
    const method = obj.data.methods[memberName];
    if (method) {
      lines.push(`## ${obj.name}.${memberName} (Method)\n`);
      if (method.description) lines.push(method.description);
      if (method.syntax) lines.push(`\nSyntax: ${method.syntax}`);
      if (method.parameters?.length) {
        lines.push('\nParameters:');
        for (const p of method.parameters) {
          lines.push(`  ${p.type} ${p.name} — ${p.description}`);
        }
      }
      if (method.returnType) lines.push(`\nReturns: ${method.returnType} ${method.returnDescription || ''}`);
      if (method.remarks) lines.push(`\nRemarks: ${method.remarks}`);
      if (method.example) lines.push(`\nExample:\n${method.example}`);
      return lines.join('\n');
    }

    const prop = obj.data.properties[memberName];
    if (prop) {
      lines.push(`## ${obj.name}.${memberName} (Property)\n`);
      lines.push(`Type: ${prop.type}`);
      if (prop.description) lines.push(prop.description);
      if (prop.readOnly) lines.push('(read-only)');
      if (prop.writeOnly) lines.push('(write-only)');
      return lines.join('\n');
    }

    // Fuzzy match on member name
    const memberLower = memberName.toLowerCase();
    const methodMatches = Object.keys(obj.data.methods).filter(
      (m) => m.toLowerCase().includes(memberLower)
    );
    const propMatches = Object.keys(obj.data.properties).filter(
      (p) => p.toLowerCase().includes(memberLower)
    );

    if (methodMatches.length || propMatches.length) {
      lines.push(`No exact match for "${memberName}" in ${obj.name}. Similar:`);
      if (methodMatches.length) lines.push(`\nMethods: ${methodMatches.join(', ')}`);
      if (propMatches.length) lines.push(`\nProperties: ${propMatches.join(', ')}`);
      return lines.join('\n');
    }

    return `"${memberName}" not found in ${obj.name}. Use query "${obj.name}" to see all members.`;
  }

  // Case 2: Exact object name match
  const obj = findObject(q);
  if (obj) {
    lines.push(`## ${obj.name} Object\n`);
    if (obj.data.description) lines.push(obj.data.description + '\n');

    const propNames = Object.keys(obj.data.properties);
    if (propNames.length) {
      lines.push(`\nProperties (${propNames.length}):`);
      for (const pName of propNames.sort()) {
        const p = obj.data.properties[pName];
        const flags = [p.readOnly ? 'ro' : '', p.writeOnly ? 'wo' : ''].filter(Boolean).join(',');
        const flagStr = flags ? ` [${flags}]` : '';
        lines.push(`  ${pName} (${p.type})${flagStr} — ${(p.description || '').substring(0, 100)}`);
      }
    }

    const methNames = Object.keys(obj.data.methods);
    if (methNames.length) {
      lines.push(`\nMethods (${methNames.length}):`);
      for (const mName of methNames.sort()) {
        const m = obj.data.methods[mName];
        const desc = (m.description || '').substring(0, 100);
        const syn = m.syntax ? ` — ${m.syntax.substring(0, 60)}` : '';
        lines.push(`  ${mName}${syn}`);
        if (desc) lines.push(`    ${desc}`);
      }
    }
    return lines.join('\n');
  }

  // Case 3: Enum lookup
  if (cat === 'enums' || cat === 'all') {
    const enumKey = Object.keys(apiReference.enums || {}).find(
      (k) => k.toLowerCase() === q.toLowerCase()
    );
    if (enumKey) {
      lines.push(`## ${enumKey} (Enum)\n`);
      for (const val of apiReference.enums[enumKey]) {
        lines.push(`  ${val}`);
      }
      return lines.join('\n');
    }
  }

  // Case 4: Keyword search across all objects
  const qLower = q.toLowerCase();
  const results = [];

  if (cat === 'objects' || cat === 'all') {
    for (const [objName, objData] of Object.entries(apiReference.objects || {})) {
      // Search methods
      for (const [mName, mData] of Object.entries(objData.methods || {})) {
        const haystack = `${mName} ${mData.description || ''} ${mData.syntax || ''} ${mData.remarks || ''}`.toLowerCase();
        if (haystack.includes(qLower)) {
          results.push({
            type: 'method',
            object: objName,
            name: mName,
            desc: (mData.description || '').substring(0, 120),
          });
        }
      }
      // Search properties
      for (const [pName, pData] of Object.entries(objData.properties || {})) {
        const haystack = `${pName} ${pData.description || ''} ${pData.type || ''}`.toLowerCase();
        if (haystack.includes(qLower)) {
          results.push({
            type: 'property',
            object: objName,
            name: pName,
            desc: `(${pData.type || '?'}) ${(pData.description || '').substring(0, 100)}`,
          });
        }
      }
    }
  }

  if (cat === 'enums' || cat === 'all') {
    for (const [eName, eVals] of Object.entries(apiReference.enums || {})) {
      const haystack = `${eName} ${eVals.join(' ')}`.toLowerCase();
      if (haystack.includes(qLower)) {
        results.push({ type: 'enum', object: '', name: eName, desc: eVals.slice(0, 3).join(', ') });
      }
    }
  }

  if (cat === 'events' || cat === 'all') {
    for (const [evName, evData] of Object.entries(apiReference.events || {})) {
      const haystack = `${evName} ${evData.description || ''}`.toLowerCase();
      if (haystack.includes(qLower)) {
        results.push({ type: 'event', object: '', name: evName, desc: (evData.description || '').substring(0, 100) });
      }
    }
  }

  if (results.length === 0) {
    return `No results for "${q}". Try a broader term, or query an object name directly (e.g., "Drawing", "MillData", "MillTool").`;
  }

  // Cap results
  const shown = results.slice(0, 50);
  lines.push(`## Search results for "${q}" (${results.length} matches${results.length > 50 ? ', showing first 50' : ''}):\n`);
  for (const r of shown) {
    const prefix = r.object ? `${r.object}.` : '';
    lines.push(`  [${r.type}] ${prefix}${r.name} — ${r.desc}`);
  }

  if (results.length > 50) {
    lines.push(`\n... and ${results.length - 50} more. Try a more specific query.`);
  }

  return lines.join('\n');
}

function findObject(name) {
  if (!apiReference?.objects) return null;
  // Exact match
  if (apiReference.objects[name]) return { name, data: apiReference.objects[name] };
  // Case-insensitive
  const key = Object.keys(apiReference.objects).find(
    (k) => k.toLowerCase() === name.toLowerCase()
  );
  if (key) return { name: key, data: apiReference.objects[key] };
  return null;
}

function listObjectNames() {
  return Object.keys(apiReference?.objects || {}).sort();
}

// ── Start ─────────────────────────────────────────────────────

async function main() {
  log('INFO', `Alphacam MCP Server starting`);
  log('INFO', `Bridge pipe: \\\\.\\pipe\\${PIPE_NAME}`);
  log('INFO', `API key: ${API_KEY === 'anonymous' ? '(anonymous)' : '(set)'}`);
  log('INFO', `Tools registered: ${TOOL_DEFINITIONS.length + 2}`);
  log('INFO', `API reference: ${apiReference ? 'loaded' : 'NOT FOUND'}`);

  const transport = new StdioServerTransport();
  await rawServer.connect(transport);

  log('INFO', 'MCP server running on stdio');

  // Handle graceful shutdown
  process.on('SIGINT', async () => {
    log('INFO', 'Shutting down...');
    await pipeClient.disconnect();
    await rawServer.close();
    process.exit(0);
  });

  process.on('SIGTERM', async () => {
    log('INFO', 'Shutting down...');
    await pipeClient.disconnect();
    await rawServer.close();
    process.exit(0);
  });
}

main().catch((err) => {
  log('ERROR', 'Fatal error:', err);
  process.exit(1);
});
