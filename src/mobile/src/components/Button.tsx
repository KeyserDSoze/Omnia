import { Pressable, StyleSheet, Text } from 'react-native';
import type { Theme } from './theme';

export function Button({ label, onPress, p, primary, selected, disabled, testID }: {
  label: string; onPress: () => void; p: Theme; primary?: boolean; selected?: boolean; disabled?: boolean;
  testID?: string;
}) {
  return (
    <Pressable testID={testID} accessibilityRole="button" onPress={onPress} disabled={disabled}
      aria-pressed={selected}
      style={[styles.button, {
        backgroundColor: primary ? p.accent : selected ? p.card2 : 'transparent',
        borderColor: primary || selected ? p.accent : p.border, opacity: disabled ? 0.5 : 1
      }]}>
      <Text style={[styles.buttonText, { color: primary ? p.accentText : p.text }]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: { borderWidth: 1, borderRadius: 999, paddingHorizontal: 14, paddingVertical: 9 },
  buttonText: { fontSize: 13, fontWeight: '700' }
});
