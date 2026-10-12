import { useColorScheme } from 'react-native';

const light = {
  background: '#fafafa',
  card: '#ffffff',
  text: '#0a0a0a',
  muted: '#737373',
  border: '#e5e5e5',
  primary: '#0a0a0a',
  primaryText: '#fafafa',
  bubble: '#f0f0f0',
  danger: '#dc2626',
  success: '#16a34a',
};

const dark: typeof light = {
  background: '#0a0a0a',
  card: '#171717',
  text: '#fafafa',
  muted: '#a3a3a3',
  border: '#262626',
  primary: '#fafafa',
  primaryText: '#0a0a0a',
  bubble: '#262626',
  danger: '#f87171',
  success: '#4ade80',
};

export type Palette = typeof light;

/** 与 Web 端 vercel 主题一致的黑白配色，跟随系统深浅色。 */
export function usePalette(): Palette {
  return useColorScheme() === 'dark' ? dark : light;
}
