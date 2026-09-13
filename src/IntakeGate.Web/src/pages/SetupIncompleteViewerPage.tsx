import SupportAgentRounded from '@mui/icons-material/SupportAgentRounded';
import { Alert, Box, Card, CardContent, Stack, Typography } from '@mui/material';

export function SetupIncompleteViewerPage() {
  return (
    <Stack spacing={3} sx={{ maxWidth: 760 }}>
      <Box><Typography variant="overline" color="warning.main" sx={{ fontWeight: 800 }}>Setup incomplete</Typography><Typography component="h1" variant="h1">This installation is not ready yet</Typography></Box>
      <Alert severity="warning">An administrator must finish the initial configuration before operational pages become available.</Alert>
      <Card><CardContent><Stack direction="row" spacing={2}><SupportAgentRounded color="primary" /><Box><Typography component="h2" variant="h3">What to do</Typography><Typography color="text.secondary" sx={{ mt: 0.5 }}>Contact an Engineering Intake Gate administrator. Your Viewer account remains read-only.</Typography></Box></Stack></CardContent></Card>
    </Stack>
  );
}
