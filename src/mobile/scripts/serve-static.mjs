// Production-like local static server for the exported web app (Playwright only).
// TAO_WEB_BASE_PATH must match the export: '' for the gateway, '/Tao' for GitHub Pages.
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const base = (process.env.TAO_WEB_BASE_PATH ?? '').replace(/\/+$/, '');
const mime = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg',
  '.woff': 'font/woff', '.woff2': 'font/woff2', '.ttf': 'font/ttf',
  '.ico': 'image/x-icon', '.webp': 'image/webp'
};

async function resolveFile(relative) {
  const file = path.resolve(root, relative || 'index.html');
  if (file !== root && !file.startsWith(root + path.sep)) return null;
  for (const candidate of [file, `${file}.html`, path.join(file, 'index.html')]) {
    try {
      if ((await stat(candidate)).isFile()) return candidate;
    } catch { /* try next */ }
  }
  return null;
}

const server = createServer(async (req, res) => {
  try {
    const pathname = new URL(req.url ?? '/', 'http://localhost').pathname;
    if (base && pathname !== base && !pathname.startsWith(`${base}/`)) {
      res.writeHead(404).end('Not found');
      return;
    }
    const relative = decodeURIComponent(pathname.slice(base.length)).replace(/^\/+/, '');
    const file = await resolveFile(relative);
    if (!file) {
      res.writeHead(404).end('Not found');
      return;
    }
    res.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream',
      'Cache-Control': 'no-store' });
    res.end(await readFile(file));
  } catch {
    res.writeHead(404).end('Not found');
  }
});
server.listen(19006, '127.0.0.1', () => console.log(`Tao static web: http://127.0.0.1:19006${base}/`));
