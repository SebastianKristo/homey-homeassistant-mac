'use strict';

const http = require('http');

/** HTTP client for the MacBridge app. */
class MacBridgeClient {
  constructor({ host, port = 8765, token }) {
    this.host = host;
    this.port = port;
    this.token = token;
  }

  request(method, path, body) {
    return new Promise((resolve, reject) => {
      const payload = body ? JSON.stringify(body) : null;
      const req = http.request({
        host: this.host,
        port: this.port,
        path: `/api${path}`,
        method,
        timeout: 15000,
        headers: {
          Authorization: `Bearer ${this.token}`,
          'Content-Type': 'application/json',
          ...(payload ? { 'Content-Length': Buffer.byteLength(payload) } : {}),
        },
      }, res => {
        let data = '';
        res.on('data', chunk => { data += chunk; });
        res.on('end', () => {
          let json = {};
          try { json = JSON.parse(data || '{}'); } catch (err) { /* ignore */ }
          if (res.statusCode === 401) return reject(new Error('Invalid token'));
          if (res.statusCode !== 200) return reject(new Error(json.error || `HTTP ${res.statusCode}`));
          return resolve(json);
        });
      });
      req.on('timeout', () => req.destroy(new Error('Timeout')));
      req.on('error', reject);
      if (payload) req.write(payload);
      req.end();
    });
  }

  status() {
    return this.request('GET', '/status');
  }

  command(command, value) {
    return this.request('POST', '/command', value === undefined ? { command } : { command, value });
  }
}

module.exports = MacBridgeClient;
