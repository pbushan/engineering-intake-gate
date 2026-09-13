import { Alert, Card, CardContent, Grid, Stack, Typography } from '@mui/material';

export function HelpPage() {
  const topics = [
    ['Azure DevOps PAT', 'Use a PAT with only the read access needed to execute the governed saved query. A local PAT is encrypted by the backend and never redisplayed. An environment reference stores only a variable name.'],
    ['AI provider and model', 'Choose OpenAI or Anthropic explicitly. Verify its credential, discover models or enter an ID manually, validate the candidate, and then confirm it. Discovery never guesses quality, price, or capability.'],
    ['Intake criteria', 'Each criterion describes evidence or context needed to begin Engineering investigation. Required criteria always apply; contextual criteria may permit a documented not-applicable outcome.'],
    ['Saved query', 'A saved-query URL or GUID defines the installation’s only governed work-item population. Validation is performed by the backend and shows at most ten safe preview rows before confirmation.'],
    ['Controlled Dry Run', 'Dry Run evaluates intake without modifying Azure DevOps. Production activation is not enabled in this release.'],
    ['Schedule and timezone', 'Manual Only disables automatic runs. Presets and custom six-field cron expressions are validated by the backend in the configured TimeZoneInfo timezone.'],
  ] as const;
  return <Stack spacing={3}><Typography component="h1" variant="h1">Help</Typography><Alert severity="info">Engineering Ready means only that an intake contains enough relevant evidence and context for Engineering to begin investigation without avoidable clarification. It does not confirm a defect.</Alert><Grid container spacing={2}>{topics.map(([title, body]) => <Grid key={title} size={{ xs: 12, md: 6 }}><Card sx={{ height: '100%' }}><CardContent><Typography component="h2" variant="h3">{title}</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>{body}</Typography></CardContent></Card></Grid>)}</Grid></Stack>;
}
