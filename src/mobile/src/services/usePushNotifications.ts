import { useCallback, useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { CloudApiError, PUSH_PURPOSE, PUSH_SOURCE, type MemoryApi } from './memoryApi';
import {
  pushSupport, storeSubscriptionId, storedSubscriptionId, subscribeThisDevice, thisDeviceSubscription,
  unsubscribeThisDevice, type PushSupport
} from './pushClient';

export type PushNotifications = {
  /** False until the first look at this device and the server has finished. */
  loaded: boolean;
  support: PushSupport;
  /** This device is subscribed and the user's consent is active. */
  on: boolean;
  busy: boolean;
  message: string;
  enable(): Promise<void>;
  disable(): Promise<void>;
  test(): Promise<void>;
};

/** Turning notifications on for this device: permission, consent and subscription, in that order (ADR 0113). */
export function usePushNotifications(api: MemoryApi | null): PushNotifications {
  const t = useT();
  const [loaded, setLoaded] = useState(false);
  const [support, setSupport] = useState<PushSupport>('unsupported');
  const [on, setOn] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const publicKey = useRef<string | null>(null);
  const live = useRef(true);

  const fail = useCallback((e: unknown) => {
    if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('settings.saveFailed'));
  }, [t]);

  useEffect(() => {
    live.current = true;
    if (!api) { setLoaded(false); setOn(false); return () => { live.current = false; }; }
    void (async () => {
      try {
        const [grants, device, config] = await Promise.all([api.listConsents(), thisDeviceSubscription(), api.pushConfig().catch(() => null)]);
        if (!live.current) return;
        publicKey.current = config?.publicKey ?? null;
        setSupport(config ? pushSupport() : 'unsupported');
        setOn(device !== null && grants.some(g => g.source === PUSH_SOURCE && g.purpose === PUSH_PURPOSE && g.active));
      } catch (e) { fail(e); } finally { if (live.current) setLoaded(true); }
    })();
    return () => { live.current = false; };
  }, [api, fail]);

  const run = useCallback(async (work: () => Promise<void>) => {
    setBusy(true);
    setMessage('');
    try { await work(); } catch (e) { fail(e); } finally { if (live.current) setBusy(false); }
  }, [fail]);

  return {
    loaded, support, on, busy, message,
    enable: () => run(async () => {
      if (!api || !publicKey.current) return;
      // The permission prompt comes first, straight from the tap; the key was fetched when the page opened.
      const subscription = await subscribeThisDevice(publicKey.current);
      if (!subscription?.endpoint || !subscription.keys?.p256dh || !subscription.keys.auth) {
        if (live.current) setSupport(pushSupport());
        return;
      }
      await api.grantPushNotifications();
      const created = await api.subscribePush({
        endpoint: subscription.endpoint, keys: { p256dh: subscription.keys.p256dh, auth: subscription.keys.auth }
      });
      storeSubscriptionId(created.id);
      if (live.current) setOn(true);
    }),
    disable: () => run(async () => {
      if (!api) return;
      const id = storedSubscriptionId();
      await unsubscribeThisDevice();
      if (id) await api.unsubscribePush(id).catch(() => undefined);
      storeSubscriptionId(null);
      const grant = (await api.listConsents()).find(g => g.source === PUSH_SOURCE && g.purpose === PUSH_PURPOSE && g.active);
      if (grant) await api.revokeConsent(grant.id);
      if (live.current) setOn(false);
    }),
    test: () => run(async () => {
      if (!api) return;
      const { delivered } = await api.sendTestPush();
      if (live.current) setMessage(t(delivered > 0 ? 'reminders.testSent' : 'reminders.testNone'));
    })
  };
}
