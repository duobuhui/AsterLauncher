'use strict';
// Narrow HTTPS worker for public Endfield distribution metadata and content.
// The URL arrives on stdin, never through argv or ordinary logs.
const https = require('node:https');
const { pipeline } = require('node:stream');

function allowed(raw) {
  const uri = new URL(raw);
  const host = uri.hostname.toLowerCase();
  if (uri.protocol !== 'https:' || uri.username || uri.password || (uri.port && uri.port !== '443') ||
      !(host === 'launcher.hypergryph.com' || host.endsWith('.hycdn.cn') ||
        host.endsWith('.hypergryph.com') || host.endsWith('.gryphline.com'))) {
    throw new Error('untrusted_host');
  }
  return uri;
}
function fail(reason) {
  if (!process.stdout.headersSentByAster) {
    process.stdout.write(JSON.stringify({ error: reason }) + '\n');
    process.stdout.headersSentByAster = true;
  }
  process.exitCode = 1;
}
function fetch(uri, method, headers, body, redirects) {
  let request;
  try {
    request = https.request(allowed(uri), { method, headers }, response => {
      if ([301, 302, 303, 307, 308].includes(response.statusCode) && response.headers.location) {
        response.resume();
        if (redirects >= 3 || method !== 'GET') return fail('redirect_rejected');
        try { return fetch(new URL(response.headers.location, uri).toString(), method, headers, body, redirects + 1); }
        catch { return fail('redirect_rejected'); }
      }
      const selected = {};
      for (const name of ['content-length', 'content-range', 'content-type', 'etag', 'last-modified']) {
        if (typeof response.headers[name] === 'string') selected[name] = response.headers[name];
      }
      process.stdout.write(JSON.stringify({ status: response.statusCode, headers: selected }) + '\n');
      process.stdout.headersSentByAster = true;
      pipeline(response, process.stdout, error => { if (error) process.exitCode = 1; });
    });
    request.setTimeout(180000, () => request.destroy(new Error('timeout')));
    request.on('error', error => fail(error.code || 'request_failed'));
    request.end(body);
  } catch { fail('request_failed'); }
}
let input = '';
let aborted = false;
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => {
  if (aborted) return;
  input += chunk;
  if (input.length > 2 * 1024 * 1024) { aborted = true; input = ''; fail('request_too_large'); process.stdin.destroy(); }
});
process.stdin.on('end', () => {
  if (aborted) return;
  try {
    const request = JSON.parse(input);
    const uri = allowed(request.url);
    if (request.method !== 'GET' && request.method !== 'POST') throw new Error('unsupported_method');
    const headers = {};
    for (const [name, value] of Object.entries(request.headers || {})) {
      const key = name.toLowerCase();
      if (!['range', 'content-type', 'accept', 'user-agent'].includes(key) || typeof value !== 'string')
        throw new Error('unsupported_header');
      headers[key] = value;
    }
    const body = request.body ? Buffer.from(request.body, 'base64') : undefined;
    if (body) headers['content-length'] = body.length;
    fetch(uri.toString(), request.method, headers, body, 0);
  } catch (error) { fail(error.message === 'untrusted_host' ? 'untrusted_host' : 'invalid_request'); }
});
