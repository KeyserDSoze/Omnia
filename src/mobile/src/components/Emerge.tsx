import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Animated, Easing, Platform, StyleSheet, Text, View, type StyleProp, type TextStyle } from 'react-native';
import { smokeDelays, smokeUnits, wisp } from './effects';
import { web, type Theme } from './theme';

/**
 * Keyframes live in one global stylesheet and are chosen with a data attribute: react-native-web
 * compiles dynamic styles inline, where keyframes are not available. Only the delay is inline.
 */
const CSS = `
@keyframes tao-smoke-a{0%{opacity:0;filter:blur(22px);transform:translate(-14px,26px) scale(1.8) rotate(-8deg)}50%{opacity:.8;filter:blur(7px)}100%{opacity:1;filter:blur(0);transform:none}}
@keyframes tao-smoke-b{0%{opacity:0;filter:blur(24px);transform:translate(16px,32px) scale(1.5) rotate(7deg)}50%{opacity:.8;filter:blur(8px)}100%{opacity:1;filter:blur(0);transform:none}}
@keyframes tao-smoke-c{0%{opacity:0;filter:blur(26px);transform:translate(-4px,42px) scale(2.1)}50%{opacity:.8;filter:blur(9px)}100%{opacity:1;filter:blur(0);transform:none}}
@keyframes tao-smoke-d{0%{opacity:0;filter:blur(20px);transform:translate(24px,18px) scale(1.6) rotate(10deg)}50%{opacity:.8;filter:blur(6px)}100%{opacity:1;filter:blur(0);transform:none}}
@keyframes tao-smoke-e{0%{opacity:0;filter:blur(20px);transform:translate(-22px,20px) scale(1.6) rotate(-10deg)}50%{opacity:.8;filter:blur(6px)}100%{opacity:1;filter:blur(0);transform:none}}
@keyframes tao-puff{0%{opacity:0;transform:translateY(24px) scale(.4)}30%{opacity:.7}100%{opacity:0;transform:translateY(-46px) scale(2.3)}}
@keyframes tao-dissolve{0%{opacity:0;filter:blur(16px);transform:translateY(14px) scale(.9)}100%{opacity:1;filter:blur(0);transform:none}}
@keyframes tao-pulse-dark{0%{box-shadow:0 0 0 0 rgba(143,227,207,.6)}70%,100%{box-shadow:0 0 0 22px rgba(143,227,207,0)}}
@keyframes tao-pulse-light{0%{box-shadow:0 0 0 0 rgba(31,122,103,.5)}70%,100%{box-shadow:0 0 0 22px rgba(31,122,103,0)}}
:root [data-tao^="smoke-"]{display:inline-block;animation-duration:1300ms;animation-timing-function:cubic-bezier(.2,.7,.2,1);animation-fill-mode:both}
:root [data-tao="smoke-a"]{animation-name:tao-smoke-a}
:root [data-tao="smoke-b"]{animation-name:tao-smoke-b}
:root [data-tao="smoke-c"]{animation-name:tao-smoke-c}
:root [data-tao="smoke-d"]{animation-name:tao-smoke-d}
:root [data-tao="smoke-e"]{animation-name:tao-smoke-e}
:root [data-tao="puff"]{animation:tao-puff 2400ms ease-out both}
:root [data-tao="dissolve"]{animation:tao-dissolve 1100ms cubic-bezier(.2,.7,.2,1) both}
:root [data-tao^="pulse-"]{animation-duration:2400ms;animation-timing-function:ease-out;animation-iteration-count:infinite}
:root [data-tao="pulse-dark"]{animation-name:tao-pulse-dark}
:root [data-tao="pulse-light"]{animation-name:tao-pulse-light}
`;

if (Platform.OS === 'web' && typeof document !== 'undefined' && !document.getElementById('tao-effects')) {
  const sheet = document.createElement('style');
  sheet.id = 'tao-effects';
  sheet.textContent = CSS;
  document.head.appendChild(sheet);
}

const tag = (name: string) => (Platform.OS === 'web' ? { dataSet: { tao: name } } : {}) as object;
const variants = ['a', 'b', 'c', 'd', 'e'] as const;
const delayStyle = (ms: number) => web({ animationDelay: `${Math.round(ms)}ms` });

type Group = { space: true } | { space: false; units: { text: string; index: number }[] };

/** Words made of units; a space between words stays a normal, breakable space. */
function groupWords(units: string[]): Group[] {
  const groups: Group[] = [];
  let current: { text: string; index: number }[] = [];
  const close = () => { if (current.length > 0) groups.push({ space: false, units: current }); current = []; };
  units.forEach((unit, index) => {
    const body = unit.trimEnd();
    if (body.length > 0) current.push({ text: body, index });
    if (body.length < unit.length) { close(); groups.push({ space: true }); }
  });
  close();
  return groups;
}

/** A greeting whose letters condense out of smoke, one after the other (web); plain text elsewhere. */
export function SmokeText({ text, delay, still, glow, style }: {
  text: string;
  /** Milliseconds from now to the first letter. */
  delay: number;
  still: boolean;
  /** Color of the wisps behind the text. */
  glow: string;
  style: StyleProp<TextStyle>;
}) {
  const [start] = useState(() => Math.max(0, delay));
  const units = smokeUnits(text);
  if (still || Platform.OS !== 'web') return <Text style={style}>{text}</Text>;
  const delays = smokeDelays(units.length);
  return (
    <View style={styles.smokeBox}>
      {[0, 1, 2, 3, 4, 5].map(i => (
        <View key={i} pointerEvents="none" {...tag('puff')} style={[styles.puff, {
          left: `${Math.round(wisp(i, 1) * 60)}%`, top: `${Math.round(20 + wisp(i, 2) * 30)}%`,
          width: 90 + Math.round(wisp(i, 3) * 70), height: 90 + Math.round(wisp(i, 3) * 70),
          ...web({ backgroundImage: `radial-gradient(circle at center, ${glow} 0%, transparent 68%)`, filter: 'blur(8px)' })
        }, delayStyle(start + i * 140)]} />
      ))}
      <Text style={style}>
        {groupWords(units).map((group, g) => group.space ? ' ' : (
          <Text key={g} style={styles.word}>
            {group.units.map(unit => (
              <Text key={unit.index} {...tag('smoke-' + variants[Math.floor(wisp(unit.index, 4) * variants.length)])}
                style={delayStyle(start + delays[unit.index])}>{unit.text}</Text>
            ))}
          </Text>
        ))}
      </Text>
    </View>
  );
}

/** Dissolves into view and then, when asked, keeps sending out a soft ring that invites a tap. */
export function Emerge({ delay, still, pulse, theme, children }: {
  delay: number;
  still: boolean;
  /** Radius of the ring; omit for no pulse. */
  pulse?: number;
  theme: Theme;
  children: ReactNode;
}) {
  const [start] = useState(() => Math.max(0, delay));
  const shown = useRef(new Animated.Value(still ? 1 : 0)).current;
  useEffect(() => {
    if (still || Platform.OS === 'web') return;
    Animated.timing(shown, { toValue: 1, duration: 700, delay: start, easing: Easing.out(Easing.cubic), useNativeDriver: true }).start();
  }, []);
  if (still) return <View>{children}</View>;
  if (Platform.OS !== 'web') return <Animated.View style={{ opacity: shown }}>{children}</Animated.View>;
  return (
    <View {...tag('dissolve')} style={delayStyle(start)}>
      {pulse === undefined ? children : (
        <View {...tag('pulse-' + theme.mode)} style={[{ borderRadius: pulse }, delayStyle(start + 1100)]}>{children}</View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  smokeBox: { alignItems: 'center', justifyContent: 'center' },
  puff: { position: 'absolute', borderRadius: 999 },
  word: web({ display: 'inline-block', whiteSpace: 'nowrap' }) as TextStyle
});
