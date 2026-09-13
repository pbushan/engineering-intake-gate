import { Button, Stack, Typography } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';

export function NotFoundPage() {
  return <Stack spacing={2} sx={{ alignItems: 'flex-start' }}><Typography component="h1" variant="h1">Page not found</Typography><Typography color="text.secondary">That page is not part of the current Engineering Intake Gate experience.</Typography><Button component={RouterLink} to="/" variant="contained">Return home</Button></Stack>;
}
