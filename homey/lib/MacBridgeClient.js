'use strict';

const crypto = require('crypto');
const dgram = require('dgram');
const http = require('http');

const MAX_RESPONSE_BYTES = 256 * 1024;

const sha256 = data => crypto.createHash('sha256').update(data).digest('hex');
const sign = (token, message) => crypto.createHmac('sha256', token).update(message).digest('hex');

function safeEqual(a, b) {
  const x = Buffer.from(String(a));
  const y = Buffer.from(String(b));
  return x.length === y.length && x.length > 0 && crypto.timingSafeEqual(x, y);
}

/**
 * HTTP client for the MacBridge app.
 *
 * Requests are signed with HMAC-SHA256 ("MacBridge-v1"), so the token is never sent over the
 * network, and every response must carry the Mac's signature.
 */
class MacBridgeClient {
  constructor({ host, port = 8765, token }) {
    this.host = host;
    this.port = port;
    this.token = token;
  }

  request(method, path, body) {
    return new Promise((resolve, reject) => {
      const target = `/api${path}`;
      const payload = body ? Buffer.from(JSON.stringify(body)) : Buffer.alloc(0);
      const timestamp = String(Math.floor(Date.now() / 1000));
      const nonce = crypto.randomBytes(16).toString('hex');
      const signature = sign(this.token, `MacBridge-v1\n${method}\n${target}\n${timestamp}\n${nonce}\n${sha256(payload)}`);

      const req = http.request({
        host: this.host,
        port: this.port,
        path: target,
        method,
        timeout: 15000,
        headers: {
          'X-MacBridge-Timestamp': timestamp,
          'X-MacBridge-Nonce': nonce,
          'X-MacBridge-Signature': signature,
          ...(body ? { 'Content-Type': 'application/json', 'Content-Length': payload.length } : {}),
        },
      }, res => {
        const chunks = [];
        let size = 0;
        res.on('data', chunk => {
          size += chunk.length;
          if (size > MAX_RESPONSE_BYTES) {
            req.destroy(new Error('Response too large'));
            return;
          }
          chunks.push(chunk);
        });
        res.on('end', () => {
          if (res.statusCode === 401) return reject(new Error('Invalid token'));
          const raw = Buffer.concat(chunks);
          const expected = sign(this.token, `MacBridge-v1-response\n${nonce}\n${res.statusCode}\n${sha256(raw)}`);
          if (!safeEqual(res.headers['x-macbridge-signature'] || '', expected)) {
            return reject(new Error('Response not signed by this Mac'));
          }
          let json;
          try { json = JSON.parse(raw.toString('utf8') || '{}'); } catch (err) { return reject(new Error('Invalid response')); }
          if (res.statusCode !== 200) return reject(new Error((json && json.error) || `HTTP ${res.statusCode}`));
          return resolve(json);
        });
      });
      req.on('timeout', () => req.destroy(new Error('Timeout')));
      req.on('error', reject);
      if (body) req.write(payload);
      req.end();
    });
  }

  status() {
    return this.request('GET', '/status');
  }

  command(command, value) {
    return this.request('POST', '/command', value === undefined ? { command } : { command, value });
  }

  /** Sends a Wake-on-LAN magic packet (UDP 9) to the broadcast address and the Mac's last IP. */
  static wakeOnLan(mac, host) {
    const hex = String(mac || '').replace(/[:-]/g, '');
    if (!/^[0-9a-fA-F]{12}$/.test(hex)) return Promise.reject(new Error('Unknown MAC address'));
    const packet = Buffer.concat([Buffer.alloc(6, 0xff), ...Array(16).fill(Buffer.from(hex, 'hex'))]);
    return new Promise(resolve => {
      const socket = dgram.createSocket('udp4');
      socket.on('error', () => { socket.close(); resolve(); });
      socket.bind(() => {
        socket.setBroadcast(true);
        const targets = ['255.255.255.255', host].filter(Boolean);
        let pending = targets.length;
        targets.forEach(target => socket.send(packet, 9, target, () => {
          pending -= 1;
          if (pending === 0) { socket.close(); resolve(); }
        }));
      });
    });
  }
}

module.exports = MacBridgeClient;
