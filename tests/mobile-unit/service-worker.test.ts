import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { describe, expect, it } from 'vitest';

// The worker is a plain script: run it with a stub scope and test the decisions it exports.
function loadWorker(scope: string) {
  const source = readFileSync(new URL('../../src/mobile/public/sw.js', import.meta.url), 'utf8');
  const origin = new URL(scope).origin;
  const sandbox: Record<string, unknown> = {
    self: { registration: { scope }, location: { origin }, addEventListener: undefined },
    module: { exports: {} }, URL, Set, Promise
  };
  vm.runInNewContext(source, sandbox);
  return (sandbox.module as { exports: Record<string, (...a: unknown[]) => unknown> }).exports;
}

const get = (url: string, method = 'GET') => ({ url, method });

describe('what the service worker may touch', () => {
  const worker = loadWorker('https://taoai.me/');

  it('handles the app shell and its static files', () => {
    expect(worker.handles!(get('https://taoai.me/'))).toBe(true);
    expect(worker.handles!(get('https://taoai.me/_expo/static/js/web/entry-abc.js'))).toBe(true);
    expect(worker.handles!(get('https://taoai.me/favicon.svg'))).toBe(true);
  });

  it.each(['api/v1/today', 'auth/realms/tao/protocol/openid-connect/auth', '.well-known/oauth-protected-resource',
    'connections/callback?code=1', 'version.json', 'sw.js'])('never touches %s', path =>
    expect(worker.handles!(get('https://taoai.me/' + path))).toBe(false));

  it('ignores writes and other origins', () => {
    expect(worker.handles!(get('https://taoai.me/', 'POST'))).toBe(false);
    expect(worker.handles!(get('https://api.taoai.me/api/v1/today'))).toBe(false);
    expect(worker.handles!(get('https://evil.example/_expo/static/x.js'))).toBe(false);
  });

  it('respects a base path', () => {
    const project = loadWorker('https://example.github.io/Tao/');
    expect(project.handles!(get('https://example.github.io/Tao/_expo/static/x.js'))).toBe(true);
    expect(project.handles!(get('https://example.github.io/Tao/api/v1/x'))).toBe(false);
    expect(project.handles!(get('https://example.github.io/other/x.js'))).toBe(false);
    expect(project.relativePath!(new URL('https://example.github.io/Tao/'))).toBe('');
  });

  it('caches only hashed bundles and media, and finds them in the page', () => {
    expect(worker.isStaticAsset!('_expo/static/js/web/entry-abc.js')).toBe(true);
    expect(worker.isStaticAsset!('icon-192.png')).toBe(true);
    expect(worker.isStaticAsset!('index.html')).toBe(false);
    const html = '<script src="/_expo/static/js/web/entry-abc.js" defer></script><link href="/_expo/static/css/a.css">' +
      '<img src="/photo.png"><a href="/other">x</a>';
    expect(worker.assetUrls!(html, 'https://taoai.me/')).toEqual([
      'https://taoai.me/_expo/static/js/web/entry-abc.js', 'https://taoai.me/_expo/static/css/a.css']);
  });
});
