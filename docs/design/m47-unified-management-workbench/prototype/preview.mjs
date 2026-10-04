// Loopback-only preview. Only the named, non-secret prototype assets are served.
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.dirname(fileURLToPath(import.meta.url));
const assets = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/index.html', ['index.html', 'text/html; charset=utf-8']],
  ['/styles.css', ['styles.css', 'text/css; charset=utf-8']],
  ['/catalog.js', ['catalog.js', 'text/javascript; charset=utf-8']],
  ['/task-details.js', ['task-details.js', 'text/javascript; charset=utf-8']],
  ['/app.js', ['app.js', 'text/javascript; charset=utf-8']],
  ['/logo.svg', ['logo.svg', 'image/svg+xml']],
]);
const sockets = new Set();
let requests = 0;
const server = http.createServer(async (request, response) => {
  const asset = assets.get(request.url);
  if (++requests > 10_000 || request.method !== 'GET' || !asset) {
    response.writeHead(404).end('Not found');
    return;
  }
  try {
    const content = await readFile(path.join(root, asset[0]));
    response.writeHead(200, {
      'Content-Type': asset[1], 'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
      'Content-Security-Policy': "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; base-uri 'none'; form-action 'none'",
    }).end(content);
  } catch { response.writeHead(500).end('Prototype asset unavailable'); }
});
server.requestTimeout = 5_000;
server.headersTimeout = 5_000;
server.keepAliveTimeout = 1_000;
server.maxConnections = 32;
server.on('connection', (socket) => { sockets.add(socket); socket.once('close', () => sockets.delete(socket)); });
const maximumRun = setTimeout(stop, 30 * 60_000);
function stop() {
  clearTimeout(maximumRun);
  server.close();
  for (const socket of [...sockets].slice(0, 32)) socket.destroy();
}
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
server.on('error', (error) => { clearTimeout(maximumRun); console.error(error.message); process.exitCode = 1; });
server.listen(0, '127.0.0.1', () => {
  console.log(JSON.stringify({ url: `http://127.0.0.1:${server.address().port}`, pid: process.pid,
    parentPid: process.ppid, startedAtUtc: new Date().toISOString(),
    command: [process.execPath, ...process.argv.slice(1)], maximumMinutes: 30, root }));
});
