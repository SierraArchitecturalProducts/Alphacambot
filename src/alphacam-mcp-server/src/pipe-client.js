/**
 * Named pipe client for communicating with the AlphacamBridge .NET server.
 *
 * Protocol:
 * 1. Connect to \\.\pipe\AlphacamBridge
 * 2. Send auth JSON (raw bytes, no framing): {"key":"..."}
 * 3. Send/receive JSON-RPC 2.0 messages with 4-byte big-endian length prefix
 */

import net from 'node:net';
import { EventEmitter } from 'node:events';

const DEFAULT_PIPE_NAME = 'AlphacamBridge';
const AUTH_SETTLE_MS = 200;
const CONNECT_TIMEOUT_MS = 5000;
const REQUEST_TIMEOUT_MS = 30000;

export class PipeClient extends EventEmitter {
  /** @type {net.Socket | null} */
  #socket = null;
  #pipeName;
  #apiKey;
  #authenticated = false;
  #requestId = 0;
  /** @type {Map<number, {resolve: Function, reject: Function, timer: NodeJS.Timeout}>} */
  #pending = new Map();
  /** @type {Buffer} */
  #readBuffer = Buffer.alloc(0);

  /**
   * @param {object} opts
   * @param {string} [opts.pipeName]
   * @param {string} [opts.apiKey]
   */
  constructor(opts = {}) {
    super();
    this.#pipeName = opts.pipeName ?? DEFAULT_PIPE_NAME;
    this.#apiKey = opts.apiKey ?? 'anonymous';
  }

  get connected() {
    return this.#socket !== null && !this.#socket.destroyed;
  }

  get authenticated() {
    return this.#authenticated;
  }

  /**
   * Connect to the bridge pipe server and authenticate.
   * @returns {Promise<void>}
   */
  async connect() {
    if (this.connected) return;

    const pipePath = `\\\\.\\pipe\\${this.#pipeName}`;

    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        reject(new Error(`Connection timeout after ${CONNECT_TIMEOUT_MS}ms`));
      }, CONNECT_TIMEOUT_MS);

      this.#socket = net.createConnection(pipePath, () => {
        clearTimeout(timer);
        resolve();
      });

      this.#socket.on('error', (err) => {
        clearTimeout(timer);
        reject(err);
      });
    });

    // Set up data handling
    this.#socket.on('data', (chunk) => this.#onData(chunk));
    this.#socket.on('close', () => this.#onClose());
    this.#socket.on('error', (err) => this.emit('error', err));

    // Authenticate
    await this.#authenticate();
  }

  /**
   * Send raw auth handshake (no length prefix).
   */
  async #authenticate() {
    const authMsg = JSON.stringify({ key: this.#apiKey });
    const authBuf = Buffer.from(authMsg, 'utf8');

    await new Promise((resolve, reject) => {
      this.#socket.write(authBuf, (err) => {
        if (err) reject(err);
        else resolve();
      });
    });

    // The bridge server doesn't send an auth response - it just starts
    // accepting length-prefixed JSON-RPC requests after auth.
    // Brief pause to let the server process the auth message.
    await new Promise((r) => setTimeout(r, AUTH_SETTLE_MS));
    this.#authenticated = true;
  }

  /**
   * Send a JSON-RPC request and wait for the response.
   * @param {string} method
   * @param {object} [params]
   * @param {number} [timeoutMs]
   * @returns {Promise<object>} The result field from the response
   */
  async request(method, params = {}, timeoutMs = REQUEST_TIMEOUT_MS) {
    if (!this.connected) {
      throw new Error('Not connected to bridge');
    }

    const id = ++this.#requestId;
    const msg = {
      jsonrpc: '2.0',
      id,
      method,
      params,
    };

    const body = Buffer.from(JSON.stringify(msg), 'utf8');
    const frame = Buffer.alloc(4 + body.length);
    frame.writeUInt32BE(body.length, 0);
    body.copy(frame, 4);

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`Request timeout for ${method} (id=${id}) after ${timeoutMs}ms`));
      }, timeoutMs);

      this.#pending.set(id, { resolve, reject, timer });

      this.#socket.write(frame, (err) => {
        if (err) {
          clearTimeout(timer);
          this.#pending.delete(id);
          reject(err);
        }
      });
    });
  }

  /**
   * Process incoming data from the pipe. Messages are length-prefixed:
   * [4 bytes big-endian length][JSON payload]
   */
  #onData(chunk) {
    this.#readBuffer = Buffer.concat([this.#readBuffer, chunk]);

    while (this.#readBuffer.length >= 4) {
      const payloadLen = this.#readBuffer.readUInt32BE(0);

      if (payloadLen <= 0 || payloadLen > 10 * 1024 * 1024) {
        // Invalid frame - reset buffer and emit error
        this.#readBuffer = Buffer.alloc(0);
        this.emit('error', new Error(`Invalid frame length: ${payloadLen}`));
        return;
      }

      const totalLen = 4 + payloadLen;
      if (this.#readBuffer.length < totalLen) {
        break; // Wait for more data
      }

      const jsonBuf = this.#readBuffer.subarray(4, totalLen);
      this.#readBuffer = this.#readBuffer.subarray(totalLen);

      let parsed;
      try {
        parsed = JSON.parse(jsonBuf.toString('utf8'));
      } catch (e) {
        this.emit('error', new Error(`Failed to parse response JSON: ${e.message}`));
        continue;
      }

      this.#handleResponse(parsed);
    }
  }

  /**
   * Route a parsed JSON-RPC response to its pending request.
   */
  #handleResponse(msg) {
    // Notifications (no id) are emitted as events
    if (msg.id == null) {
      this.emit('notification', msg);
      return;
    }

    const pending = this.#pending.get(msg.id);
    if (!pending) {
      this.emit('error', new Error(`Received response for unknown request id: ${msg.id}`));
      return;
    }

    clearTimeout(pending.timer);
    this.#pending.delete(msg.id);

    if (msg.error) {
      const err = new Error(msg.error.message ?? 'Bridge error');
      err.code = msg.error.code;
      err.data = msg.error.data;
      pending.reject(err);
    } else {
      pending.resolve(msg.result);
    }
  }

  #onClose() {
    this.#authenticated = false;

    // Reject all pending requests
    for (const [id, pending] of this.#pending) {
      clearTimeout(pending.timer);
      pending.reject(new Error('Pipe connection closed'));
    }
    this.#pending.clear();

    this.emit('close');
  }

  /**
   * Disconnect from the bridge.
   */
  async disconnect() {
    if (this.#socket) {
      this.#socket.destroy();
      this.#socket = null;
    }
    this.#authenticated = false;
    this.#readBuffer = Buffer.alloc(0);
  }
}
