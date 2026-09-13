import { Alert, AlertTitle, Button, List, ListItem } from '@mui/material';
import { useEffect, useMemo, useRef } from 'react';
import type { ApiError } from '../api/api-error';

interface ValidationSummaryProps {
  error: ApiError;
  labels: Readonly<Record<string, string>>;
  focusTarget: (key: string) => void;
}

export function ValidationSummary({ error, labels, focusTarget }: ValidationSummaryProps) {
  const summaryRef = useRef<HTMLDivElement>(null);
  const items = useMemo(() => [
    ...Object.entries(error.fieldErrors),
    ...Object.entries(error.sectionErrors),
  ], [error]);

  useEffect(() => {
    queueMicrotask(() => {
      summaryRef.current?.focus();
      summaryRef.current?.scrollIntoView?.({ block: 'nearest' });
    });
  }, []);

  if (!items.length) return null;
  const count = items.length;
  return (
    <Alert ref={summaryRef} tabIndex={-1} severity="error" role="alert" aria-live="assertive">
      <AlertTitle>Complete the highlighted fields</AlertTitle>
      {count} {count === 1 ? 'field or section needs' : 'fields or sections need'} attention before you can continue.
      <List dense disablePadding sx={{ mt: 1 }}>
        {items.slice(0, 8).map(([key, messages]) => (
          <ListItem disableGutters key={key}>
            <Button size="small" onClick={() => focusTarget(key)} sx={{ justifyContent: 'flex-start', textAlign: 'left' }}>
              {labels[key] ?? key} — {messages[0]}
            </Button>
          </ListItem>
        ))}
      </List>
      {items.length > 8 ? `${String(items.length - 8)} more highlighted fields also need attention.` : null}
    </Alert>
  );
}
