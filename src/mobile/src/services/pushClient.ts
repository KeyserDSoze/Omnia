// Web Push in the browser: permission, the subscription of this device and its id on the server. Web only.
import { webRuntime } from './pwaClient';
import { keyBytes } from './pushKey';

export type PushSupport = 'unsupported' | 'denied' | 'ready';

const SUBSCRIPTION_ID_KEY = 'tao.push.subscription';

export function pushSupport(): PushSupport {
  if (!webRuntime() || !('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window))
    return 'unsupported';
  return Notification.permission === 'denied' ? 'denied' : 'ready';
}

export async function thisDeviceSubscription(): Promise<PushSubscription | null> {
  if (pushSupport() === 'unsupported') return null;
  const registration = await navigator.serviceWorker.getRegistration();
  return registration ? registration.pushManager.getSubscription() : null;
}

/** Asks for the permission (call it from a tap) and subscribes this device. */
export async function subscribeThisDevice(publicKey: string): Promise<PushSubscriptionJSON | null> {
  if (pushSupport() === 'unsupported') return null;
  if ((await Notification.requestPermission()) !== 'granted') return null;
  const registration = await navigator.serviceWorker.ready;
  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true, applicationServerKey: keyBytes(publicKey)
  });
  return subscription.toJSON();
}

export async function unsubscribeThisDevice(): Promise<void> {
  const subscription = await thisDeviceSubscription();
  await subscription?.unsubscribe();
}

export const storedSubscriptionId = (): string | null => {
  try { return window.localStorage.getItem(SUBSCRIPTION_ID_KEY); } catch { return null; }
};

export function storeSubscriptionId(id: string | null): void {
  try {
    if (id) window.localStorage.setItem(SUBSCRIPTION_ID_KEY, id);
    else window.localStorage.removeItem(SUBSCRIPTION_ID_KEY);
  } catch { /* private mode */ }
}
