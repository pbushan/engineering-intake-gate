import LockOutlined from '@mui/icons-material/LockOutlined';
import { Avatar, Box, Card, CardContent, Container, Stack, Typography } from '@mui/material';

export function AuthPageFrame({ eyebrow, title, description, children }: { eyebrow: string; title: string; description: string; children: React.ReactNode }) {
  return (
    <Box component="main" sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', py: 5, background: 'radial-gradient(circle at 15% 10%, #d8eef4 0, transparent 38%), linear-gradient(145deg, #f8fafc 0%, #edf3f6 100%)' }}>
      <Container maxWidth="sm">
        <Card>
          <CardContent sx={{ p: { xs: 3, sm: 5 } }}>
            <Stack spacing={3}>
              <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                <Avatar sx={{ bgcolor: 'primary.dark', width: 48, height: 48 }}><LockOutlined /></Avatar>
                <Box>
                  <Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>{eyebrow}</Typography>
                  <Typography component="h1" variant="h2">{title}</Typography>
                </Box>
              </Stack>
              <Typography color="text.secondary">{description}</Typography>
              {children}
            </Stack>
          </CardContent>
        </Card>
        <Typography variant="caption" color="text.secondary" sx={{ textAlign: 'center', display: 'block', mt: 2 }}>
          Engineering Intake Gate · Controlled Dry Run
        </Typography>
      </Container>
    </Box>
  );
}
