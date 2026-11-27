// Tao service worker: the app opens offline and updates quietly. It never touches /api, /auth or any
// request with credentials: those carry personal data and stay network-only. The build marker is
// stamped by scripts/release.mjs; a changed marker makes the browser fetch and install a new worker.
const BUILD = '__TAO_BUILD__';
const SHELL = 'tao-shell-' + BUILD;
const STATIC = 'tao-static-v1';
const MAX_STATIC_ENTRIES = 300;
const NETWORK_ONLY = ['api/', 'auth/', '.well-known/', 'connections/', 'version.json', 'sw.js'];

function scopePath() {
  return new URL(self.registration.scope).pathname;
}

/** The path under the scope ('' for the home page), or null when the request is not ours to handle. */
function relativePath(url) {
  if (url.origin !== self.location.origin) return null;
  const base = scopePath();
  return url.pathname.startsWith(base) ? url.pathname.slice(base.length) : null;
}

function handles(request) {
  if (request.method !== 'GET') return false;
  const rel = relativePath(new URL(request.url));
  if (rel === null) return false;
  return !NETWORK_ONLY.some(prefix => rel === prefix || rel.startsWith(prefix));
}

function isStaticAsset(rel) {
  return rel.startsWith('_expo/static/') || /\.(png|svg|ico|woff2?|ttf|webp|jpg)$/i.test(rel);
}

/** The hashed bundle, style and font addresses a page refers to, as absolute URLs within the scope. */
function assetUrls(html, scope) {
  const urls = new Set();
  for (const match of html.matchAll(/(?:src|href)="([^"]*\/_expo\/static\/[^"]+)"/g)) {
    try { urls.add(new URL(match[1], scope).href); } catch { /* ignore malformed */ }
  }
  return [...urls];
}

async function precache() {
  const scope = self.registration.scope;
  const response = await fetch(new Request(scope, { cache: 'reload' }));
  if (!response.ok) throw new Error('shell unavailable');
  const html = await response.clone().text();
  await (await caches.open(SHELL)).put(scope, response);
  const statics = await caches.open(STATIC);
  await Promise.all(assetUrls(html, scope).map(async url => {
    if (!(await statics.match(url))) await statics.add(url);
  }));
}

async function trim(cache) {
  const keys = await cache.keys();
  for (const key of keys.slice(0, Math.max(0, keys.length - MAX_STATIC_ENTRIES))) await cache.delete(key);
}

async function fromStaticCache(request) {
  const cache = await caches.open(STATIC);
  const hit = await cache.match(request);
  if (hit) return hit;
  const response = await fetch(request);
  if (response.ok) { await cache.put(request, response.clone()); await trim(cache); }
  return response;
}

async function navigate(request) {
  const scope = self.registration.scope;
  try {
    const response = await fetch(request);
    // Only the plain home page refreshes the offline shell: a URL with a query can carry a sign-in code.
    if (response.ok && request.url === scope) (await caches.open(SHELL)).put(scope, response.clone());
    return response;
  } catch {
    const shell = await caches.match(scope, { cacheName: SHELL });
    if (shell) return shell;
    throw new Error('offline');
  }
}

/** What to show for a push payload: plain text only, bounded, and always back to the app itself. */
function notificationOf(data, scope) {
  const text = (value, max) => (typeof value === 'string' ? value.slice(0, max) : '');
  const tag = text(data && data.tag, 64);
  return {
    title: text(data && data.title, 80) || 'Tao',
    options: {
      body: text(data && data.body, 200), icon: new URL('icon-192.png', scope).href,
      ...(tag ? { tag } : {}), data: { url: scope }
    }
  };
}

if (typeof self.addEventListener === 'function') {
  self.addEventListener('push', event => {
    let data = null;
    try { data = event.data ? event.data.json() : null; } catch { data = null; }
    const { title, options } = notificationOf(data, self.registration.scope);
    event.waitUntil(self.registration.showNotification(title, options));
  });
  self.addEventListener('notificationclick', event => {
    event.notification.close();
    const scope = self.registration.scope;
    event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(windows => {
      const open = windows.find(w => w.url.startsWith(scope));
      return open ? open.focus() : self.clients.openWindow(scope);
    }));
  });
  self.addEventListener('install', event => event.waitUntil(precache()));
  self.addEventListener('activate', event => event.waitUntil((async () => {
    for (const name of await caches.keys()) if (name !== SHELL && name !== STATIC) await caches.delete(name);
    await self.clients.claim();
  })()));
  self.addEventListener('message', event => {
    if (event.data && event.data.type === 'SKIP_WAITING') self.skipWaiting();
  });
  self.addEventListener('fetch', event => {
    const request = event.request;
    if (!handles(request)) return;
    if (request.mode === 'navigate') { event.respondWith(navigate(request)); return; }
    if (isStaticAsset(relativePath(new URL(request.url)))) event.respondWith(fromStaticCache(request));
  });
}

if (typeof module !== 'undefined') module.exports = { handles, isStaticAsset, assetUrls, relativePath, notificationOf, BUILD };
