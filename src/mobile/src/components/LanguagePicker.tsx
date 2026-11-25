import { Pressable, StyleSheet, Text, View } from 'react-native';
import { LOCALES, setLocale, useLocale } from '../i18n';
import { web, type Theme } from './theme';

/** Every language in its own name; the choice is kept in this browser only. */
export function LanguagePicker({ theme, onPick }: { theme: Theme; onPick?: () => void }) {
  const locale = useLocale();
  return (
    <View testID="language-picker" style={styles.row}>
      {LOCALES.map(item => {
        const selected = item.code === locale;
        return (
          <Pressable key={item.code} testID={'language-' + item.code} accessibilityRole="button"
            aria-pressed={selected} accessibilityLabel={item.english}
            onPress={() => { setLocale(item.code); onPick?.(); }}
            style={[styles.chip, {
              backgroundColor: selected ? theme.accent : theme.surface,
              borderColor: selected ? theme.accent : theme.border
            }, web({ backdropFilter: 'blur(14px)', WebkitBackdropFilter: 'blur(14px)' })]}>
            <Text {...({ lang: item.tag } as object)}
              style={[styles.text, { color: selected ? theme.accentText : theme.text }]}>{item.name}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, justifyContent: 'center' },
  chip: { borderWidth: 1, borderRadius: 999, paddingHorizontal: 14, paddingVertical: 8 },
  text: { fontSize: 13, fontWeight: '700' }
});
