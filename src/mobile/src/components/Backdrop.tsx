import { useEffect, useRef } from 'react';
import { Animated, Easing, Platform, StyleSheet, View } from 'react-native';
import { web, type Theme } from './theme';

const useNativeDriver = Platform.OS !== 'web';

/** Ink canvas with two slow, blurred auras; still under reduced motion. */
export function Backdrop({ theme, still }: { theme: Theme; still: boolean }) {
  const drift = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    if (still) return;
    const half = { duration: 26000, easing: Easing.inOut(Easing.sin), useNativeDriver };
    const loop = Animated.loop(Animated.sequence([
      Animated.timing(drift, { toValue: 1, ...half }),
      Animated.timing(drift, { toValue: 0, ...half })
    ]));
    loop.start();
    return () => loop.stop();
  }, [still]);
  const a = drift.interpolate({ inputRange: [0, 1], outputRange: [-50, 70] });
  const b = drift.interpolate({ inputRange: [0, 1], outputRange: [60, -40] });
  // Radial gradients rather than blurred circles: no filter layers, no seams under glass.
  const aura = (color: string) => Platform.OS === 'web'
    ? web({ backgroundImage: `radial-gradient(circle at center, ${color} 0%, transparent 68%)` })
    : { backgroundColor: color, opacity: 0.35 };
  return (
    <View pointerEvents="none" style={[StyleSheet.absoluteFill, styles.canvas, { backgroundColor: theme.bg }]}>
      <Animated.View style={[styles.blob, styles.top, aura(theme.glow),
        { transform: [{ translateX: a }, { translateY: b }] }]} />
      <Animated.View style={[styles.blob, styles.bottom, aura(theme.glow2),
        { transform: [{ translateX: b }, { translateY: a }] }]} />
      <View style={[StyleSheet.absoluteFill, web({
        backgroundImage: theme.mode === 'dark'
          ? 'radial-gradient(ellipse at 50% -20%, rgba(255,255,255,0.07), transparent 55%)'
          : 'radial-gradient(ellipse at 50% -20%, rgba(255,255,255,0.85), transparent 55%)'
      })]} />
    </View>
  );
}

const styles = StyleSheet.create({
  canvas: { overflow: 'hidden' },
  blob: { position: 'absolute', width: 900, height: 900, borderRadius: 450 },
  top: { top: -480, left: '-6%' },
  bottom: { bottom: -520, right: '-12%' }
});
