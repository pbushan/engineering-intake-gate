import RefreshRounded from '@mui/icons-material/RefreshRounded';
import { Box, Button, CircularProgress, Container, Stack, Typography } from '@mui/material';

interface FullPageStateProps {
  title: string;
  message: string;
  loading?: boolean;
  actionLabel?: string;
  onAction?: () => void;
}

export function FullPageState({ title, message, loading = false, actionLabel, onAction }: FullPageStateProps) {
  return (
    <Container maxWidth="sm" sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', py: 4 }}>
      <Stack spacing={2.5} role={loading ? 'status' : 'alert'} aria-live="polite" sx={{ alignItems: 'center', textAlign: 'center' }}>
        {loading ? <CircularProgress aria-label="Loading" /> : <Box aria-hidden sx={{ width: 56, height: 6, borderRadius: 99, bgcolor: 'warning.main' }} />}
        <Typography component="h1" variant="h2">{title}</Typography>
        <Typography color="text.secondary">{message}</Typography>
        {actionLabel && onAction ? (
          <Button variant="contained" startIcon={<RefreshRounded />} onClick={onAction}>{actionLabel}</Button>
        ) : null}
      </Stack>
    </Container>
  );
}
