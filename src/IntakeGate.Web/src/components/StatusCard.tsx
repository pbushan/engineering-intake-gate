import CheckCircleOutlineRounded from '@mui/icons-material/CheckCircleOutlineRounded';
import InfoOutlined from '@mui/icons-material/InfoOutlined';
import WarningAmberRounded from '@mui/icons-material/WarningAmberRounded';
import { Card, CardContent, Chip, Stack, Typography } from '@mui/material';

type StatusTone = 'positive' | 'attention' | 'neutral';

const icons = {
  positive: <CheckCircleOutlineRounded fontSize="small" />,
  attention: <WarningAmberRounded fontSize="small" />,
  neutral: <InfoOutlined fontSize="small" />,
};

export function StatusCard({ label, value, detail, tone = 'neutral' }: { label: string; value: string; detail: string; tone?: StatusTone }) {
  const color = tone === 'positive' ? 'success' : tone === 'attention' ? 'warning' : 'default';
  return (
    <Card variant="outlined" sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5}>
          <Typography variant="overline" color="text.secondary">{label}</Typography>
          <Chip icon={icons[tone]} label={value} color={color} variant={tone === 'neutral' ? 'outlined' : 'filled'} sx={{ alignSelf: 'flex-start', fontWeight: 700 }} />
          <Typography variant="body2" color="text.secondary">{detail}</Typography>
        </Stack>
      </CardContent>
    </Card>
  );
}
