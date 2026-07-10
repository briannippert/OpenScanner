import { createTheme, alpha } from '@mui/material/styles';
import { surface, accent, status, text, radius, typography, shadow, motion } from './tokens';

/**
 * Central MUI theme, built from design tokens. Dark-only by design (the app is a
 * "tactical" scanner dashboard). Custom palette additions (surface ramp +
 * semantic status colors) are exposed via module augmentation so they are usable
 * from `sx` as `theme.palette.surface.raised`, `theme.palette.statusColors.info`.
 */

declare module '@mui/material/styles' {
  interface Palette {
    surface: {
      base: string;
      surface: string;
      raised: string;
      overlay: string;
      border: string;
      borderStrong: string;
    };
    statusColors: {
      info: string;
      warn: string;
      error: string;
      success: string;
      muted: string;
    };
    accentGlow: string;
  }
  interface PaletteOptions {
    surface?: Palette['surface'];
    statusColors?: Palette['statusColors'];
    accentGlow?: string;
  }
  interface TypographyVariants {
    mono: React.CSSProperties;
  }
  interface TypographyVariantsOptions {
    mono?: React.CSSProperties;
  }
}

declare module '@mui/material/Typography' {
  interface TypographyPropsVariantOverrides {
    mono: true;
  }
}

export function createAppTheme() {
  return createTheme({
    palette: {
      mode: 'dark',
      primary: {
        main: accent.main,
        light: accent.bright,
        dark: accent.dim,
        contrastText: text.onAccent,
      },
      info: { main: status.info },
      warning: { main: status.warn },
      error: { main: status.error },
      success: { main: status.success },
      background: {
        default: surface.base,
        paper: surface.surface,
      },
      text: {
        primary: text.primary,
        secondary: text.secondary,
        disabled: text.disabled,
      },
      divider: surface.border,
      surface: { ...surface },
      statusColors: { ...status },
      accentGlow: shadow.accentGlow,
    },
    shape: {
      borderRadius: radius.md,
    },
    typography: {
      fontFamily: typography.sans,
      h5: { fontWeight: 600, letterSpacing: 0.2 },
      h6: { fontWeight: 600, letterSpacing: 0.3 },
      subtitle2: { fontWeight: 600, letterSpacing: 0.2 },
      button: { fontWeight: 600, letterSpacing: 0.8, textTransform: 'none' },
      mono: {
        fontFamily: typography.mono,
        letterSpacing: 0.4,
      },
    },
    transitions: {
      duration: { shortest: 120, shorter: 160, short: 200, standard: 200 },
    },
    components: {
      MuiCssBaseline: {
        styleOverrides: {
          body: { backgroundColor: surface.base },
          '*::-webkit-scrollbar': { width: 8, height: 8 },
          '*::-webkit-scrollbar-track': { background: surface.base },
          '*::-webkit-scrollbar-thumb': { background: surface.borderStrong, borderRadius: radius.sm },
          '*::-webkit-scrollbar-thumb:hover': { background: status.muted },
          '*': { scrollbarWidth: 'thin', scrollbarColor: `${surface.borderStrong} ${surface.base}` },
          '@keyframes pulse': {
            '0%': { opacity: 0.5 },
            '50%': { opacity: 1 },
            '100%': { opacity: 0.5 },
          },
          '@keyframes onair': {
            '0%': { boxShadow: `0 0 0 0 ${alpha(status.error, 0.5)}` },
            '70%': { boxShadow: `0 0 0 6px ${alpha(status.error, 0)}` },
            '100%': { boxShadow: `0 0 0 0 ${alpha(status.error, 0)}` },
          },
          '@keyframes rowEnter': {
            from: { opacity: 0, transform: 'translateY(-6px)' },
            to: { opacity: 1, transform: 'translateY(0)' },
          },
          '@media (prefers-reduced-motion: reduce)': {
            '*, *::before, *::after': {
              animationDuration: '0.001ms !important',
              animationIterationCount: '1 !important',
              transitionDuration: '0.001ms !important',
            },
          },
        },
      },
      MuiPaper: { styleOverrides: { root: { backgroundImage: 'none' } } },
      MuiCard: {
        styleOverrides: {
          root: {
            backgroundImage: 'none',
            backgroundColor: surface.surface,
            border: `1px solid ${surface.border}`,
            borderRadius: radius.md,
            boxShadow: shadow.card,
          },
        },
      },
      MuiAppBar: {
        defaultProps: { elevation: 0 },
        styleOverrides: {
          root: { backgroundColor: surface.raised, borderBottom: `1px solid ${surface.border}` },
        },
      },
      MuiButton: {
        defaultProps: { disableElevation: true },
        styleOverrides: {
          root: {
            borderRadius: radius.sm,
            transition: `background-color ${motion.base} ${motion.easing}, border-color ${motion.base} ${motion.easing}`,
          },
          outlined: { borderColor: surface.borderStrong },
        },
      },
      MuiIconButton: {
        styleOverrides: {
          root: { transition: `background-color ${motion.fast} ${motion.easing}, color ${motion.fast} ${motion.easing}` },
        },
      },
      MuiChip: {
        styleOverrides: {
          root: { borderRadius: radius.sm, fontWeight: 600 },
          outlined: { borderColor: surface.borderStrong },
        },
      },
      MuiDialog: {
        styleOverrides: {
          paper: {
            backgroundColor: surface.surface,
            backgroundImage: 'none',
            border: `1px solid ${surface.border}`,
            borderRadius: radius.lg,
          },
        },
      },
      MuiTooltip: {
        styleOverrides: {
          tooltip: { backgroundColor: surface.overlay, color: text.primary, border: `1px solid ${surface.border}`, fontSize: '0.72rem' },
          arrow: { color: surface.overlay },
        },
      },
      MuiOutlinedInput: {
        styleOverrides: {
          root: {
            backgroundColor: surface.base,
            '& .MuiOutlinedInput-notchedOutline': { borderColor: surface.border },
            '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: surface.borderStrong },
          },
        },
      },
      MuiListItemButton: {
        styleOverrides: {
          root: {
            borderRadius: radius.sm,
            transition: `background-color ${motion.fast} ${motion.easing}`,
            '&:hover': { backgroundColor: alpha(accent.main, 0.08) },
            '&.Mui-selected': { backgroundColor: alpha(accent.main, 0.14) },
            '&.Mui-selected:hover': { backgroundColor: alpha(accent.main, 0.18) },
          },
        },
      },
      MuiMenu: {
        styleOverrides: {
          paper: { backgroundColor: surface.overlay, border: `1px solid ${surface.border}`, boxShadow: shadow.overlay },
        },
      },
      MuiDivider: { styleOverrides: { root: { borderColor: surface.border } } },
    },
  });
}

export const theme = createAppTheme();

/**
 * Alias kept for the "device readout" surfaces (scanner hero, canvas
 * visualizers). Same dark theme; a distinct name documents the intent that
 * these surfaces are always the dark readout look.
 */
export const readoutTheme = theme;

export default theme;
