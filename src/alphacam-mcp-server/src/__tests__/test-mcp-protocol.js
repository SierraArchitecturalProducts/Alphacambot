/**
 * Integration test for the MCP server.
 * Spawns the server as a child process with stdio transport and sends
 * MCP protocol messages to verify tools/list and tools/call work.
 */

import { test, describe } from 'node:test';
import assert from 'node:assert';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SERVER_PATH = join(__dirname, '..', 'index.js');

/**
 * Send a JSON-RPC message over MCP stdio transport.
 * MCP uses newline-delimited JSON on stdio.
 */
function sendMessage(proc, msg) {
  const json = JSON.stringify(msg);
  proc.stdin.write(json + '\n');
}

/**
 * Collect stdout lines from the process until we get a parseable JSON-RPC response.
 */
function waitForResponse(proc, timeoutMs = 5000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      reject(new Error('Timeout waiting for MCP response'));
    }, timeoutMs);

    let buffer = '';

    const onData = (chunk) => {
      buffer += chunk.toString();
      const lines = buffer.split('\n');

      for (let i = 0; i < lines.length - 1; i++) {
        const line = lines[i].trim();
        if (!line) continue;

        try {
          const parsed = JSON.parse(line);
          if (parsed.jsonrpc === '2.0' && parsed.id != null) {
            clearTimeout(timer);
            proc.stdout.removeListener('data', onData);
            resolve(parsed);
            return;
          }
        } catch {
          // Not JSON, skip
        }
      }
      buffer = lines[lines.length - 1];
    };

    proc.stdout.on('data', onData);
  });
}

describe('MCP Server Protocol', () => {
  test('tools/list returns all tool definitions', async () => {
    const proc = spawn('node', [SERVER_PATH, '--pipe', 'NonExistentPipe'], {
      stdio: ['pipe', 'pipe', 'pipe'],
    });

    try {
      // Wait a moment for the server to initialize
      await new Promise((r) => setTimeout(r, 500));

      // Send initialize request (required by MCP protocol)
      sendMessage(proc, {
        jsonrpc: '2.0',
        id: 1,
        method: 'initialize',
        params: {
          protocolVersion: '2024-11-05',
          capabilities: {},
          clientInfo: { name: 'test', version: '1.0.0' },
        },
      });

      const initResp = await waitForResponse(proc);
      assert.strictEqual(initResp.id, 1);
      assert.ok(initResp.result, 'Initialize should return a result');
      assert.ok(
        initResp.result.capabilities.tools,
        'Server should declare tools capability'
      );

      // Send initialized notification
      sendMessage(proc, {
        jsonrpc: '2.0',
        method: 'notifications/initialized',
      });

      await new Promise((r) => setTimeout(r, 100));

      // Request tools list
      sendMessage(proc, {
        jsonrpc: '2.0',
        id: 2,
        method: 'tools/list',
        params: {},
      });

      const listResp = await waitForResponse(proc);
      assert.strictEqual(listResp.id, 2);
      assert.ok(listResp.result, 'tools/list should return a result');
      assert.ok(Array.isArray(listResp.result.tools), 'result.tools should be an array');

      const tools = listResp.result.tools;
      // 28 static tools + 2 meta tools = 30
      assert.ok(tools.length >= 28, `Expected at least 28 tools, got ${tools.length}`);

      // Check a specific tool exists
      const newTool = tools.find((t) => t.name === 'alphacam_new');
      assert.ok(newTool, 'alphacam_new tool should exist');
      assert.ok(newTool.description, 'Tool should have a description');
      assert.ok(newTool.inputSchema, 'Tool should have an inputSchema');

      const rectTool = tools.find((t) => t.name === 'alphacam_create_rectangle');
      assert.ok(rectTool, 'alphacam_create_rectangle tool should exist');
      assert.deepStrictEqual(rectTool.inputSchema.required, [
        'x',
        'y',
        'width',
        'height',
      ]);

      // Check meta tools
      const statusTool = tools.find((t) => t.name === 'alphacam_status');
      assert.ok(statusTool, 'alphacam_status meta-tool should exist');

      console.log(`PASS: tools/list returned ${tools.length} tools`);
    } finally {
      proc.kill('SIGTERM');
    }
  });

  test('tools/call returns error when bridge not connected', async () => {
    const proc = spawn(
      'node',
      [SERVER_PATH, '--pipe', 'NonExistentPipe12345'],
      { stdio: ['pipe', 'pipe', 'pipe'] }
    );

    try {
      await new Promise((r) => setTimeout(r, 500));

      // Initialize
      sendMessage(proc, {
        jsonrpc: '2.0',
        id: 1,
        method: 'initialize',
        params: {
          protocolVersion: '2024-11-05',
          capabilities: {},
          clientInfo: { name: 'test', version: '1.0.0' },
        },
      });
      await waitForResponse(proc);

      sendMessage(proc, {
        jsonrpc: '2.0',
        method: 'notifications/initialized',
      });
      await new Promise((r) => setTimeout(r, 100));

      // Call a tool - should fail since no bridge is running
      sendMessage(proc, {
        jsonrpc: '2.0',
        id: 2,
        method: 'tools/call',
        params: {
          name: 'alphacam_status',
          arguments: {},
        },
      });

      const callResp = await waitForResponse(proc, 10000);
      assert.strictEqual(callResp.id, 2);

      // Should return an error content (not a JSON-RPC error, but isError in content)
      assert.ok(callResp.result, 'Should have a result (MCP wraps errors in content)');
      assert.ok(callResp.result.isError, 'Should be marked as error');
      assert.ok(
        callResp.result.content[0].text.includes('Error'),
        'Content should describe the error'
      );

      console.log('PASS: tools/call returns proper error when bridge unavailable');
    } finally {
      proc.kill('SIGTERM');
    }
  });
});
