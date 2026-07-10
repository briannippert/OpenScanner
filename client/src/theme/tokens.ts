/**
 * Design tokens for OpenScanner.
 *
 * Single source of truth for color, spacing, radius, typography and motion.
 * The MUI theme (theme.ts), component `sx` props and index.css all read from
 * here so there is exactly one place to change the look. Structured so a light
 * theme could be layered on later without touching component code.
 */

/** Dark neutral elevation ramp — very dark, faint cool tint. */
export const surface = {
  /** App background, behind everything. */
  base: '#08090b',
  /** Default panel/card background. */
  surface: '#111317',
  /** Raised surfaces (headers, hovered rows, popovers). */
  raised: '#181b21',
  /** Highest elevation (menus, active/selected). */
  overlay: '#20242c',
  /** Hairline dividers and card borders. */
  border: '#282d36',
  /** Slightly brighter border for hover/focus affordance. */
  borderStrong: '#3a414d',
} as const;

/** Signal green — the scanner identity, used for "live / active". */
export const accent = {
  main: '#39ff88',
  bright: '#5bffa0',
  dim: '#1f9d5a',
  /** Translucent green for glows / selected backgrounds. */
  glow: 'rgba(57, 255, 136, 0.14)',
} as const;

/** Semantic status colors — replace the scattered one-off hexes. */
export const status = {
  info: '#3bc9ff', // cyan
  warn: '#ffb638', // amber
  error: '#ff5d5d',
  success: accent.main,
  muted: '#6b7280',
} as const;

export const text = {
  primary: '#e7ecf2',
  secondary: '#9aa4b2',
  disabled: '#5c6673',
  /** On top of the accent color (green buttons etc.). */
  onAccent: '#04120a',
} as const;

/** 8px base spacing scale (MUI spacing unit stays 8). */
export const spacing = {
  unit: 8,
  /** Per-depth indent for the transmission-log tree. */
  treeIndent: 2.25,
} as const;

export const radius = {
  sm: 6,
  md: 9,
  lg: 14,
  pill: 999,
} as const;

export const typography = {
  mono: '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace',
  sans: '"Inter", system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
} as const;

export const shadow = {
  card: '0 1px 2px rgba(0,0,0,0.4)',
  raised: '0 4px 16px rgba(0,0,0,0.5)',
  overlay: '0 12px 32px rgba(0,0,0,0.6)',
  /** Green glow for the live/active hero readout. */
  accentGlow: `0 0 0 1px ${accent.glow}, 0 0 24px ${accent.glow}`,
} as const;

export const motion = {
  fast: '120ms',
  base: '200ms',
  slow: '320ms',
  easing: 'cubic-bezier(0.4, 0, 0.2, 1)',
} as const;
