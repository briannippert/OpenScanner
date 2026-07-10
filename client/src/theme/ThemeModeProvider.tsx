import React from 'react';
import { ThemeProvider, CssBaseline } from '@mui/material';
import theme from './theme';

/** Provides the (dark) MUI theme + CssBaseline for the whole app. */
export const ThemeModeProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <ThemeProvider theme={theme}>
    <CssBaseline />
    {children}
  </ThemeProvider>
);
