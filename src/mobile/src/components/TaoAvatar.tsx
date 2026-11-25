import { useEffect, useRef } from 'react';
import { Animated, Easing, Platform, Pressable, StyleSheet, View } from 'react-native';
import { nextWhim, type Whim } from './effects';
import { web, type Theme } from './theme';

export type AvatarState = 'idle' | 'thinking' | 'speaking' | 'listening';

const useNativeDriver = Platform.OS !== 'web';
const turnMs: Record<AvatarState, number> = { idle: 36000, thinking: 2400, speaking: 9000, listening: 16000 };
const beatMs: Record<AvatarState, number> = { idle: 2600, thinking: 900, speaking: 480, listening: 1300 };
const swell: Record<AvatarState, number> = { idle: 0.025, thinking: 0.04, speaking: 0.075, listening: 0.03 };

/**
 * The Tao symbol that talks: it turns slowly while idle, faster while thinking, pulses while
 * speaking and sends out ripples while listening. Only the inner symbol moves, so the pressable
 * area keeps a stable box.
 */
export function TaoAvatar({ size, state, theme, still, label, pressed, onPress, testID }: {
  size: number;
  state: AvatarState;
  theme: Theme;
  /** Reduced motion: no rotation, pulse or ripples. */
  still: boolean;
  label: string;
  pressed?: boolean;
  onPress?: () => void;
  testID?: string;
}) {
  const spin = useRef(new Animated.Value(0)).current;
  const breath = useRef(new Animated.Value(0)).current;
  const ripple = useRef(new Animated.Value(0)).current;
  const flipY = useRef(new Animated.Value(0)).current;
  const flipX = useRef(new Animated.Value(0)).current;
  const pop = useRef(new Animated.Value(0)).current;
  const direction = useRef(1);
  const turning = useRef<() => void>(() => { });
  const whimBusy = useRef(false);
  const pressFlip = useRef<() => void>(() => { });
  const current = useRef(state);
  current.current = state;

  useEffect(() => {
    if (still) return;
    let active = true;
    // Restart from the current angle, so a change of speed never jumps.
    const turn = () => spin.stopAnimation(angle => {
      if (!active) return;
      Animated.timing(spin, { toValue: angle + direction.current, duration: turnMs[state], easing: Easing.linear, useNativeDriver })
        .start(({ finished }) => { if (finished && active) turn(); });
    });
    turning.current = turn;
    turn();
    return () => { active = false; spin.stopAnimation(); };
  }, [state, still]);

  // Now and then the symbol does something unexpected: turns the other way, flips, tumbles, spins a lap.
  useEffect(() => {
    if (still) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let previous: Whim | undefined;
    const done = () => { whimBusy.current = false; };
    const flip = (axis: Animated.Value, ms: number) => {
      whimBusy.current = true;
      Animated.parallel([
        Animated.timing(axis, { toValue: 1, duration: ms, easing: Easing.inOut(Easing.cubic), useNativeDriver }),
        Animated.sequence([
          Animated.timing(pop, { toValue: 1, duration: ms / 2, easing: Easing.out(Easing.quad), useNativeDriver }),
          Animated.timing(pop, { toValue: 0, duration: ms / 2, easing: Easing.in(Easing.quad), useNativeDriver })
        ])
      ]).start(() => { axis.setValue(0); done(); });
    };
    const redirect = (laps: number, ms: number, reverse: boolean) => {
      whimBusy.current = true;
      spin.stopAnimation(angle => {
        Animated.timing(spin, {
          toValue: angle + direction.current * laps, duration: ms,
          easing: laps > 0.5 ? Easing.inOut(Easing.cubic) : Easing.out(Easing.quad), useNativeDriver
        }).start(({ finished }) => {
          done();
          if (!finished) return;
          if (reverse) direction.current = -direction.current;
          turning.current();
        });
      });
    };
    pressFlip.current = () => flip(flipY, 1000);
    const schedule = () => {
      const whim = nextWhim(Math.random, previous);
      timer = setTimeout(() => {
        if (!whimBusy.current && current.current !== 'thinking') {
          previous = whim.kind;
          if (whim.kind === 'flip') flip(flipY, 1200);
          else if (whim.kind === 'tumble') flip(flipX, 1200);
          else if (whim.kind === 'burst') redirect(1, 1300, false);
          else redirect(0.06, 700, true);
        }
        schedule();
      }, whim.waitMs);
    };
    schedule();
    return () => { clearTimeout(timer); pressFlip.current = () => { }; };
  }, [still]);

  useEffect(() => {
    if (still) { breath.setValue(0); return; }
    const half = { duration: beatMs[state], easing: Easing.inOut(Easing.sin), useNativeDriver };
    const loop = Animated.loop(Animated.sequence([
      Animated.timing(breath, { toValue: 1, ...half }),
      Animated.timing(breath, { toValue: 0, ...half })
    ]));
    loop.start();
    return () => loop.stop();
  }, [state, still]);

  useEffect(() => {
    if (state !== 'listening' || still) { ripple.setValue(0); return; }
    const loop = Animated.loop(Animated.timing(ripple, {
      toValue: 1, duration: 1700, easing: Easing.out(Easing.quad), useNativeDriver
    }));
    loop.start();
    return () => { loop.stop(); ripple.setValue(0); };
  }, [state, still]);

  const rotate = spin.interpolate({ inputRange: [0, 1], outputRange: ['0deg', '360deg'] });
  const turnY = flipY.interpolate({ inputRange: [0, 1], outputRange: ['0deg', '360deg'] });
  const turnX = flipX.interpolate({ inputRange: [0, 1], outputRange: ['0deg', '360deg'] });
  const flourish = pop.interpolate({ inputRange: [0, 1], outputRange: [1, 1.16] });
  const scale = breath.interpolate({ inputRange: [0, 1], outputRange: [1, 1 + swell[state]] });
  const haloOpacity = breath.interpolate({ inputRange: [0, 1], outputRange: [state === 'idle' ? 0.5 : 0.75, 1] });
  const lobe = size / 2;
  const dot = size * 0.13;
  const echo = Animated.modulo(Animated.add(ripple, 0.5), 1);
  const rings = [
    { opacity: ripple.interpolate({ inputRange: [0, 1], outputRange: [0.7, 0] }),
      scale: ripple.interpolate({ inputRange: [0, 1], outputRange: [1, 1.85] }) },
    { opacity: echo.interpolate({ inputRange: [0, 1], outputRange: [0.7, 0] }),
      scale: echo.interpolate({ inputRange: [0, 1], outputRange: [1, 1.85] }) }
  ];

  return (
    <Pressable testID={testID} onPress={onPress ? () => { if (!whimBusy.current) pressFlip.current(); onPress(); } : undefined}
      disabled={!onPress} accessibilityRole="button"
      accessibilityLabel={label} aria-pressed={pressed}
      style={[styles.box, { width: size * 1.7, height: size * 1.7 }]}>
      <Animated.View pointerEvents="none" style={[styles.center, {
        width: size * 2.1, height: size * 2.1, borderRadius: size * 1.05,
        opacity: haloOpacity, transform: [{ scale }]
      }, Platform.OS === 'web'
        ? web({ backgroundImage: `radial-gradient(circle at center, ${theme.glow} 0%, transparent 66%)` })
        : { backgroundColor: theme.glow, width: size * 1.4, height: size * 1.4 }]} />
      {state === 'listening' && rings.map((ring, i) => (
        <Animated.View key={i} pointerEvents="none" style={[styles.center, {
          width: size, height: size, borderRadius: lobe, borderWidth: 2, borderColor: theme.accent,
          opacity: ring.opacity, transform: [{ scale: ring.scale }]
        }]} />
      ))}
      <Animated.View style={[styles.symbol, {
        width: size, height: size, borderRadius: lobe, transform: [{ perspective: 800 }, { rotateX: turnX },
          { rotateY: turnY }, { rotate }, { scale }, { scale: flourish }],
        boxShadow: `0 0 ${Math.round(size * 0.35)}px ${theme.glow}`
      }]}>
        <View style={{ flex: 1, backgroundColor: theme.yin }} />
        <View style={{ flex: 1, backgroundColor: theme.yang }} />
        <View style={[styles.lobe, { top: 0, left: lobe / 2, width: lobe, height: lobe, borderRadius: lobe / 2,
          backgroundColor: theme.yang }]}>
          <View style={{ width: dot, height: dot, borderRadius: dot / 2, backgroundColor: theme.yin }} />
        </View>
        <View style={[styles.lobe, { top: lobe, left: lobe / 2, width: lobe, height: lobe, borderRadius: lobe / 2,
          backgroundColor: theme.yin }]}>
          <View style={{ width: dot, height: dot, borderRadius: dot / 2, backgroundColor: theme.yang }} />
        </View>
        <View pointerEvents="none" style={[StyleSheet.absoluteFill, { borderRadius: lobe, borderWidth: 1,
          borderColor: theme.mode === 'dark' ? 'rgba(255,255,255,0.22)' : 'rgba(21,23,29,0.22)' }]} />
      </Animated.View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  box: { alignItems: 'center', justifyContent: 'center' },
  center: { position: 'absolute' },
  symbol: { overflow: 'hidden', flexDirection: 'row' },
  lobe: { position: 'absolute', alignItems: 'center', justifyContent: 'center' }
});
