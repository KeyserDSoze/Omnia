import { StyleSheet, Text, View } from 'react-native';
import { notesSince } from '../services/pwa';
import { APP_VERSION } from '../services/pwa';
import type { AppUpdates } from '../services/useAppUpdates';
import { useLocale, useT } from '../i18n';
import { Button } from './Button';
import type { Theme } from './theme';

/** The prompt for a new version: what changed, update now, later, or never ask again (quiet updates). */
export function UpdateBanner({ updates, theme: p }: { updates: AppUpdates; theme: Theme }) {
  const t = useT();
  const locale = useLocale();
  if (!updates.enabled || updates.decision !== 'prompt' || !updates.manifest) return null;
  const releases = notesSince(updates.manifest, APP_VERSION, locale);
  return (
    <View testID="update-banner" accessibilityRole="alert" accessibilityLiveRegion="polite"
      style={[styles.banner, { backgroundColor: p.card2, borderColor: p.accent }]}>
      <Text style={[styles.title, { color: p.text }]}>{t('update.title')}</Text>
      <Text testID="update-version" style={[styles.version, { color: p.subtle }]}>
        {t('update.version', { version: updates.manifest.version })}
      </Text>
      {releases.map(r => (
        <View key={r.version} style={{ marginTop: 6 }}>
          {releases.length > 1 && <Text style={[styles.version, { color: p.subtle }]}>{r.version}</Text>}
          {r.notes.map(line => <Text key={line} style={[styles.note, { color: p.text }]}>{'\u2022 ' + line}</Text>)}
        </View>
      ))}
      <View style={styles.row}>
        <Button testID="update-now" p={p} primary label={t('update.now')} onPress={() => { void updates.applyNow(); }} />
        <Button testID="update-later" p={p} label={t('update.later')} onPress={updates.later} />
        <Button testID="update-never" p={p} label={t('update.never')} onPress={updates.never} />
      </View>
      <Text style={[styles.version, { color: p.subtle }]}>{t('update.neverHint')}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    position: 'absolute', left: 16, right: 16, bottom: 16, maxWidth: 520, alignSelf: 'center', zIndex: 50,
    borderWidth: 1, borderRadius: 20, padding: 18, gap: 4
  },
  title: { fontSize: 16, fontWeight: '800' },
  version: { fontSize: 12, lineHeight: 17 },
  note: { fontSize: 13, lineHeight: 19, marginTop: 2 },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 12 }
});
