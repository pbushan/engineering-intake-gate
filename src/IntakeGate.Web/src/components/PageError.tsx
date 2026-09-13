import RefreshRounded from '@mui/icons-material/RefreshRounded';
import { Alert, AlertTitle, Button, Stack } from '@mui/material';
import type { ApiError } from '../api/api-error';

export function PageError({ error, retry }: { error: ApiError; retry?: () => void | Promise<void> }) {
  return (
    <Alert severity="error" role="alert">
      <AlertTitle>We couldn’t load this page</AlertTitle>
      <Stack spacing={1.5} sx={{ alignItems: 'flex-start' }}>
        <span>{error.message}</span>
        {retry ? <Button color="inherit" size="small" startIcon={<RefreshRounded />} onClick={() => void retry()}>Try again</Button> : null}
      </Stack>
    </Alert>
  );
}
