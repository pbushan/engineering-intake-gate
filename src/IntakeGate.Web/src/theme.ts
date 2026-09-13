import { createTheme } from '@mui/material/styles';

export const appTheme = createTheme({
  palette: {
    mode: 'light',
    primary: { main: '#176b87', dark: '#0d465b', light: '#d8eef4' },
    secondary: { main: '#5f6caf' },
    background: { default: '#f4f7f9', paper: '#ffffff' },
    success: { main: '#287a57' },
    warning: { main: '#a05a00' },
    error: { main: '#b42318' },
    text: { primary: '#102a43', secondary: '#52606d' },
    divider: '#d8e2ea',
  },
  shape: { borderRadius: 12 },
  spacing: 8,
  typography: {
    fontFamily: 'Inter, ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    h1: { fontSize: 'clamp(1.75rem, 3vw, 2.4rem)', fontWeight: 750, letterSpacing: '-0.035em' },
    h2: { fontSize: '1.35rem', fontWeight: 700, letterSpacing: '-0.015em' },
    h3: { fontSize: '1rem', fontWeight: 700 },
    button: { fontWeight: 700, textTransform: 'none' },
  },
  components: {
    MuiButtonBase: { defaultProps: { disableRipple: false } },
    MuiButton: { styleOverrides: { root: { minHeight: 42 } } },
    MuiCard: { styleOverrides: { root: { border: '1px solid #d8e2ea', boxShadow: '0 12px 32px rgba(16, 42, 67, 0.06)' } } },
    MuiCssBaseline: {
      styleOverrides: {
        ':focus-visible': { outline: '3px solid #f5a623', outlineOffset: '3px' },
        body: { minWidth: 320 },
      },
    },
  },
});
