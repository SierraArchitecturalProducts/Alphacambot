/**
 * Proves the server is a standard MCP server usable by ANY MCP client —
 * not just Claude Desktop. Drives it with the reference MCP SDK client
 * (the same protocol library other agent frameworks embed) over stdio:
 * initialize handshake, tools/list, and a tools/call that runs entirely
 * server-side (alphacam_api_help), plus graceful bridge-offline errors.
 *
 * Run:  node --test src/__tests__/
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const serverPath = join(__dirname, '..', 'index.js');

async function connect() {
  const client = new Client({ name: 'generic-mcp-client', version: '0.0.1' });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverPath],
    stderr: 'ignore',
  });
  await client.connect(transport);
  return client;
}

test('generic MCP client: initialize + tools/list', async () => {
  const client = await connect();
  try {
    const { tools } = await client.listTools();
    assert.ok(tools.length >= 60, `expected 60+ tools, got ${tools.length}`);
    const names = new Set(tools.map((t) => t.name));
    for (const required of [
      'alphacam_status',
      'alphacam_api_help',
      'alphacam_api_invoke',
      'alphacam_api_inspect',
      'alphacam_api_release',
      'alphacam_create_rectangle',
      'alphacam_nest',
    ]) {
      assert.ok(names.has(required), `missing tool: ${required}`);
    }
    // Every tool must carry a JSON-schema inputSchema (MCP requirement).
    for (const t of tools) {
      assert.equal(t.inputSchema?.type, 'object', `${t.name} lacks an object inputSchema`);
    }
  } finally {
    await client.close();
  }
});

test('generic MCP client: tools/call runs server-side (api_help)', async (t) => {
  const client = await connect();
  try {
    const res = await client.callTool({
      name: 'alphacam_api_help',
      arguments: { query: 'Drawing.CreateCircle' },
    });
    const text = res.content?.[0]?.text ?? '';
    if (text.startsWith('API reference not loaded')) {
      // Machine without a generated acamapi-reference.json — still a valid
      // MCP round-trip; the lookup itself just has no data.
      t.diagnostic('acamapi-reference.json not present; skipped content assertions');
      return;
    }
    assert.match(text, /CreateCircle/);
    assert.match(text, /Syntax/i);
  } finally {
    await client.close();
  }
});

test('generic MCP client: bridge-offline tool call fails gracefully', async () => {
  const client = await connect();
  try {
    // No ALPHACAM/bridge in a test environment: the call must come back as
    // a well-formed MCP error result, not a crash or a hung request.
    const res = await client.callTool({
      name: 'alphacam_api_inspect',
      arguments: {},
    });
    assert.equal(res.isError, true);
    assert.ok(res.content?.[0]?.text?.length > 0, 'expected an error message');
  } finally {
    await client.close();
  }
});
