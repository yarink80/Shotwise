'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Shotwise.Platform/Pwa/app.js'), 'utf8');
async function run(fingerprint, approved = true) {
  const nodes = new Map();
  function node(id) {
    if (!nodes.has(id)) nodes.set(id, { disabled: true, textContent: '', after() {}, addEventListener() {} });
    return nodes.get(id);
  }
  const requests = [], storage = new Map();
  const expected = 'A'.repeat(64);
  const context = {
    document: { querySelector: node, createElement: () => node('#fingerprint') },
    sessionStorage: { getItem: k => storage.get(k) || null, setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
    location: { hash: '#t=one-time-token&fp=' + expected },
    history: { replaceState() {} }, URLSearchParams,
    confirm: () => approved,
    fetch: async (url, options) => {
      requests.push({ url, options });
      return { ok: true, json: async () => url.endsWith('/info') ? { fingerprint, name: 'Desktop', maxUploadBytes: 123 } : { token: 'session' } };
    }
  };
  vm.runInNewContext(source, context);
  await new Promise(resolve => setImmediate(resolve));
  return { requests, storage, nodes };
}
(async () => {
  let result = await run('B'.repeat(64));
  assert.equal(result.requests.length, 1, 'mismatch must prevent handshake');
  assert.match(result.nodes.get('#status').textContent, /mismatch/);
  assert.equal(result.nodes.get('#files').disabled, true);
  result = await run(undefined);
  assert.equal(result.requests.length, 1, 'anonymous version-only info must prevent handshake');
  result = await run('A'.repeat(64), false);
  assert.equal(result.requests.length, 0, 'certificate comparison rejection must prevent sending the pairing credential');
  result = await run('A'.repeat(64));
  assert.deepEqual(result.requests.map(r => r.url), ['/api/v1/info', '/api/v1/handshake']);
  assert.equal(result.requests[0].options.headers.Authorization, 'Pairing one-time-token');
  assert.equal(result.storage.get('shotwise-session'), 'session');
  assert.equal(result.nodes.get('#files').disabled, false);
  assert.match(result.nodes.get('#fingerprint').textContent, /cannot inspect the TLS certificate/);
  process.stdout.write('PWA security: 4 scenarios passed.\n');
})().catch(error => { process.stderr.write(String(error) + '\n'); process.exitCode = 1; });
