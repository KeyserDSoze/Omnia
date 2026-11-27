import { StyleSheet, Text, View } from 'react-native';
import { notesOf } from '../services/pwa';
import { APP_VERSION } from '../services/pwa';
import type { AppUpdates } from '../services/useAppUpdates';
import { intlTag, useLocale, useT } from '../i18n';
import { Button } from './Button';
import { web, type Theme } from './theme';

/** Version, what is new, the way updates are installed and the install-as-an-app button. */
export function AppPanel({ updates, theme: p }: { updates: AppUpdates; theme: Theme }) {
  const t = useT();
  const locale = useLocale();
  if (!updates.enabled) return null;
  const current = updates.manifest ? notesOf(updates.manifest, APP_VERSION, locale) : [];
  const card = [styles.card, { backgroundColor: p.surface, borderColor: p.border },
    web({ backdropFilter: 'blur(18px)', WebkitBackdropFilter: 'blur(18px)' })];
  const outcome = updates.outcome === 'current' ? t('settings.updatesCurrent')
    : updates.outcome === 'available' && updates.manifest
      ? t('settings.updatesAvailable', { version: updates.manifest.version })
      : updates.outcome === 'failed' ? t('settings.updatesFailed') : '';
  return (
    <View testID="settings-app" style={card}>
      <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.appKicker')}</Text>
      <Text testID="app-version" style={[styles.title, { color: p.text }]}>{t('settings.versionLine', { version: APP_VERSION })}</Text>
      {current.length > 0 && (
        <View testID="app-notes" style={{ marginTop: 8 }}>
          <Text style={[styles.hint, { color: p.subtle, marginTop: 0 }]}>{t('settings.whatsNew')}</Text>
          {current.map(line => <Text key={line} style={[styles.hint, { color: p.text, marginTop: 4 }]}>{'\u2022 ' + line}</Text>)}
        </View>
      )}
      <Text testID="app-update-mode" style={[styles.hint, { color: p.text }]}>
        {t(updates.preference === 'silent' ? 'settings.updatesModeSilent' : 'settings.updatesModeAsk')}
      </Text>
      <View style={styles.row}>
        <Button testID="app-update-preference" p={p}
          label={t(updates.preference === 'silent' ? 'settings.updatesAsk' : 'settings.updatesSilent')}
          onPress={() => updates.setPreference(updates.preference === 'silent' ? 'ask' : 'silent')} />
        <Button testID="app-update-check" p={p} disabled={updates.checking} label={t('settings.updatesCheck')}
          onPress={() => { void updates.check(); }} />
      </View>
      {!!outcome && <Text testID="app-update-outcome" accessibilityRole="alert" style={[styles.hint, { color: p.text }]}>{outcome}</Text>}
      {updates.installed ? <Text testID="app-installed" style={[styles.hint, { color: p.text }]}>{t('settings.installed')}</Text>
        : updates.installAvailable ? (
          <View style={styles.row}>
            <Button testID="app-install" p={p} primary label={t('settings.install')} onPress={() => { void updates.install(); }} />
          </View>
        ) : <Text testID="app-install-hint" style={[styles.hint, { color: p.subtle }]}>
          {t(updates.iosHint ? 'settings.installHintIos' : 'settings.installHint')}</Text>}
      {!!updates.manifest && (
        <Text style={[styles.hint, { color: p.subtle }]}>
          {new Date(updates.manifest.builtAt).toLocaleDateString(intlTag())}
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { borderWidth: 1, borderRadius: 24, padding: 22 },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.4 },
  title: { fontSize: 20, fontWeight: '700', marginTop: 8 },
  hint: { fontSize: 13, lineHeight: 19, marginTop: 8 },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 12 }
});
