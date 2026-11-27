import { useCallback, useEffect, useRef, useState } from 'react';
import {
  decideUpdate, loadDismissed, loadPreference, saveDismissed, savePreference,
  type UpdateDecision, type UpdatePreference, type VersionManifest
} from './pwa';
import {
  activateUpdate, assetBase, currentBuild, fetchLatest, isIosBrowser, isStandalone, registerWorker, webRuntime,
  type InstallPromptEvent
} from './pwaClient';

export type CheckOutcome = 'idle' | 'current' | 'available' | 'failed';

export type AppUpdates = {
  /** False outside the browser (and before the page has hydrated): nothing to show. */
  enabled: boolean;
  manifest: VersionManifest | null;
  decision: UpdateDecision;
  preference: UpdatePreference;
  outcome: CheckOutcome;
  checking: boolean;
  installAvailable: boolean;
  installed: boolean;
  iosHint: boolean;
  check(): Promise<void>;
  applyNow(): Promise<void>;
  later(): void;
  /** "Don't ask again": from now on updates are installed quietly in the background. */
  never(): void;
  setPreference(value: UpdatePreference): void;
  install(): Promise<void>;
};

const CHECK_EVERY_MS = 30 * 60 * 1000;

export const disabledUpdates: AppUpdates = {
  enabled: false, manifest: null, decision: 'none', preference: 'ask', outcome: 'idle', checking: false,
  installAvailable: false, installed: false, iosHint: false,
  check: async () => undefined, applyNow: async () => undefined, later: () => undefined, never: () => undefined,
  setPreference: () => undefined, install: async () => undefined
};

export function useAppUpdates(): AppUpdates {
  const [enabled, setEnabled] = useState(false);
  const [manifest, setManifest] = useState<VersionManifest | null>(null);
  const [preference, setPreferenceState] = useState<UpdatePreference>('ask');
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<CheckOutcome>('idle');
  const [checking, setChecking] = useState(false);
  const [installAvailable, setInstallAvailable] = useState(false);
  const [installed, setInstalled] = useState(false);
  const [iosHint, setIosHint] = useState(false);
  const registration = useRef<ServiceWorkerRegistration | null>(null);
  const installEvent = useRef<InstallPromptEvent | null>(null);
  const base = useRef('/');
  const build = useRef<string | null>(null);

  const decision: UpdateDecision = manifest
    ? decideUpdate({ runningBuild: build.current, latest: manifest, preference, dismissedBuild: dismissed })
    : 'none';

  const check = useCallback(async () => {
    if (!webRuntime()) return;
    setChecking(true);
    const latest = await fetchLatest(base.current);
    setChecking(false);
    if (!latest) { setOutcome('failed'); return; }
    setManifest(latest);
    setOutcome(build.current !== null && build.current !== latest.build ? 'available' : 'current');
    void registration.current?.update().catch(() => undefined);
  }, []);

  useEffect(() => {
    if (!webRuntime()) return;
    base.current = assetBase();
    build.current = currentBuild();
    setPreferenceState(loadPreference(window.localStorage));
    setDismissed(loadDismissed(window.sessionStorage));
    setInstalled(isStandalone());
    setIosHint(isIosBrowser() && !isStandalone());
    setEnabled(true);

    const onPrompt = (event: Event) => { event.preventDefault(); installEvent.current = event as InstallPromptEvent; setInstallAvailable(true); };
    const onInstalled = () => { installEvent.current = null; setInstallAvailable(false); setInstalled(true); };
    window.addEventListener('beforeinstallprompt', onPrompt);
    window.addEventListener('appinstalled', onInstalled);
    const onVisible = () => { if (document.visibilityState === 'visible') void check(); };
    document.addEventListener('visibilitychange', onVisible);

    // A first visit has no running build to compare with when the page is served by a dev server.
    void registerWorker(base.current).then(r => { registration.current = r; });
    void check();
    const timer = setInterval(() => { void check(); }, CHECK_EVERY_MS);
    return () => {
      window.removeEventListener('beforeinstallprompt', onPrompt);
      window.removeEventListener('appinstalled', onInstalled);
      document.removeEventListener('visibilitychange', onVisible);
      clearInterval(timer);
    };
  }, [check]);

  // Quiet mode: the new worker takes over underneath; the page keeps running and the next start is the new one.
  useEffect(() => {
    if (decision === 'silent') void activateUpdate(registration.current);
  }, [decision, manifest?.build]);

  return {
    enabled, manifest, decision, preference, outcome, checking, installAvailable, installed, iosHint, check,
    async applyNow() {
      await activateUpdate(registration.current);
      window.location.reload();
    },
    later() {
      if (!manifest) return;
      saveDismissed(window.sessionStorage, manifest.build);
      setDismissed(manifest.build);
    },
    never() {
      savePreference(window.localStorage, 'silent');
      setPreferenceState('silent');
    },
    setPreference(value) {
      savePreference(window.localStorage, value);
      setPreferenceState(value);
    },
    async install() {
      const event = installEvent.current;
      if (!event) return;
      await event.prompt();
      const choice = await event.userChoice;
      if (choice.outcome === 'accepted') setInstallAvailable(false);
    }
  };
}
