import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { loadInviteAnswer, saveInviteAnswer, shouldInvite, type InviteAnswer } from '../services/pushInvite';
import type { PushNotifications } from '../services/usePushNotifications';
import { useT } from '../i18n';
import { Button } from './Button';
import type { Theme } from './theme';

/** Tells a signed-in user that reminders can arrive as notifications, and turns them on with one tap. */
export function NotificationInvite({ push, signedIn, otherPromptOpen, theme: p }: {
  push: PushNotifications; signedIn: boolean; otherPromptOpen: boolean; theme: Theme;
}) {
  const t = useT();
  const [answer, setAnswer] = useState<InviteAnswer>('later');
  const [ready, setReady] = useState(false);
  useEffect(() => {
    if (typeof window === 'undefined') return;
    setAnswer(loadInviteAnswer(window.localStorage, window.sessionStorage));
    // A moment after sign-in, so the offer does not land on top of the first screen.
    const timer = setTimeout(() => setReady(true), 4000);
    return () => clearTimeout(timer);
  }, []);
  if (!ready || !shouldInvite({ signedIn, loaded: push.loaded, support: push.support, on: push.on, answer, otherPromptOpen }))
    return null;

  const reply = (value: 'never' | 'later') => { saveInviteAnswer(window.localStorage, window.sessionStorage, value); setAnswer(value); };
  return (
    <View testID="notification-invite" accessibilityRole="alert" accessibilityLiveRegion="polite"
      style={[styles.banner, { backgroundColor: p.card2, borderColor: p.accent }]}>
      <Text style={[styles.title, { color: p.text }]}>{t('notify.inviteTitle')}</Text>
      <Text style={[styles.text, { color: p.subtle }]}>{t('notify.inviteText')}</Text>
      <View style={styles.row}>
        <Button testID="invite-enable" p={p} primary disabled={push.busy} label={t('reminders.enable')}
          onPress={() => { void push.enable().then(() => undefined); }} />
        <Button testID="invite-later" p={p} label={t('update.later')} onPress={() => reply('later')} />
        <Button testID="invite-never" p={p} label={t('update.never')} onPress={() => reply('never')} />
      </View>
      {!!push.message && <Text style={[styles.text, { color: p.text }]}>{push.message}</Text>}
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    position: 'absolute', left: 16, right: 16, bottom: 16, maxWidth: 520, alignSelf: 'center', zIndex: 40,
    borderWidth: 1, borderRadius: 20, padding: 18, gap: 4
  },
  title: { fontSize: 16, fontWeight: '800' },
  text: { fontSize: 13, lineHeight: 19 },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 12 }
});
