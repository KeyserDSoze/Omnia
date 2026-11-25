import { Platform, type ViewStyle } from 'react-native';
import type { Palette } from './TodayPanel';

/** Ink and rice paper: the palette of the chat and of every widget shown inside it. */
export type Theme = Palette & {
  mode: 'dark' | 'light';
  /** Aura colors behind the symbol. */
  glow: string;
  glow2: string;
  /** User bubble: solid color, plus a CSS gradient on the web. */
  user: string;
  userGradient: string;
  /** The two halves of the Tao symbol. */
  yin: string;
  yang: string;
};

export const themes: Record<'dark' | 'light', Theme> = {
  dark: {
    mode: 'dark',
    bg: '#06070B', surface: 'rgba(255,255,255,0.05)', card2: 'rgba(255,255,255,0.09)',
    text: '#EDEFF5', subtle: '#8F96AB', border: 'rgba(255,255,255,0.12)',
    accent: '#8FE3CF', accentText: '#04140F',
    glow: 'rgba(72,214,183,0.40)', glow2: 'rgba(124,110,255,0.30)',
    user: '#8FE3CF', userGradient: 'linear-gradient(135deg, #8FE3CF 0%, #6CC5F0 100%)',
    yin: '#0B0D14', yang: '#F4F1E8'
  },
  light: {
    mode: 'light',
    bg: '#F6F2E9', surface: 'rgba(255,255,255,0.62)', card2: 'rgba(21,23,29,0.06)',
    text: '#15171D', subtle: '#686D7A', border: 'rgba(21,23,29,0.12)',
    accent: '#1F7A67', accentText: '#FFFFFF',
    glow: 'rgba(31,122,103,0.26)', glow2: 'rgba(214,160,80,0.24)',
    user: '#1F7A67', userGradient: 'linear-gradient(135deg, #1F7A67 0%, #2F6FA8 100%)',
    yin: '#15171D', yang: '#FBF8F1'
  }
};

/** Tao speaks in a book face; the interface stays in the system face. */
export const serif = Platform.select({
  web: '"Iowan Old Style", "Palatino Linotype", Palatino, "Book Antiqua", Georgia, serif',
  ios: 'Georgia',
  default: 'serif'
});

/** CSS that only the web understands (gradients, blur, glass); ignored on native. */
export function web(style: Record<string, string | number>): ViewStyle {
  return Platform.OS === 'web' ? style as unknown as ViewStyle : {};
}
