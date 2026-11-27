// Browser glue for the installable app: service worker, update checks, install prompt. Web only.
import { Platform } from 'react-native';
import { parseVersionManifest, runningBuild, type VersionManifest } from './pwa';

export const webRuntime = (): boolean =>
  Platform.OS === 'web' && typeof document !== 'undefined' && typeof navigator !== 'undefined';

/** The folder the app is served from, with a trailing slash, taken from the manifest link ('/' or '/Tao/'). */
export function assetBase(): string {
  const href = document.querySelector<HTMLLinkElement>('link[rel="manifest"]')?.href;
  try { return new URL('.', href ?? window.location.href).href; } catch { return window.location.origin + '/'; }
}

export const scriptSources = (): string[] =>
  Array.from(document.scripts).map(s => s.src).filter(Boolean);

export const currentBuild = (): string | null => runningBuild(scriptSources());

/** The published version manifest, or null when it cannot be read (offline, blocked or malformed). */
export async function fetchLatest(base: string): Promise<VersionManifest | null> {
  try {
    const response = await fetch(new URL('version.json', base).href + '?t=' + Date.now(),
      { cache: 'no-store', credentials: 'omit', redirect: 'error' });
    if (!response.ok) return null;
    return parseVersionManifest(await response.json());
  } catch { return null; }
}

export async function registerWorker(base: string): Promise<ServiceWorkerRegistration | null> {
  if (!('serviceWorker' in navigator)) return null;
  try {
    return await navigator.serviceWorker.register(new URL('sw.js', base).href, { scope: new URL(base).pathname });
  } catch { return null; }
}

/** Fetches the new worker and lets it take over; resolves when it is active or after the timeout. */
export async function activateUpdate(registration: ServiceWorkerRegistration | null, timeoutMs = 8000): Promise<boolean> {
  if (!registration) return false;
  try { await registration.update(); } catch { /* offline: whatever is waiting can still be activated */ }
  const worker = registration.waiting ?? registration.installing;
  if (!worker) return false;
  await new Promise<void>(resolve => {
    const timer = setTimeout(resolve, timeoutMs);
    const finish = () => { clearTimeout(timer); resolve(); };
    const ask = () => worker.postMessage({ type: 'SKIP_WAITING' });
    worker.addEventListener('statechange', () => {
      if (worker.state === 'installed') ask();
      if (worker.state === 'activated') finish();
    });
    if (worker.state === 'installed') ask();
    if (worker.state === 'activated') finish();
  });
  return true;
}

export type InstallPromptEvent = Event & {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
};

export const isStandalone = (): boolean =>
  window.matchMedia?.('(display-mode: standalone)').matches === true ||
  (navigator as Navigator & { standalone?: boolean }).standalone === true;

export const isIosBrowser = (): boolean =>
  /iPhone|iPad|iPod/.test(navigator.userAgent) ||
  (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
