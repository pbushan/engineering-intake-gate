import { useState } from 'react';
import { Alert, Button, Stack, TextField, Typography } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import { ApiError } from '../api/api-error';
import { useAuth } from '../auth/AuthContext';
import { AuthPageFrame } from './AuthPageFrame';

interface Errors { username?: string; password?: string; displayName?: string }

export function BootstrapPage() {
  const { bootstrap } = useAuth();
  const navigate = useNavigate();
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [errors, setErrors] = useState<Errors>({});
  const [formError, setFormError] = useState<string | null>(null);

  const submit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const nextErrors: Errors = {};
    if (username.trim().length < 3 || username.trim().length > 64) nextErrors.username = 'Use 3 to 64 characters.';
    if (displayName.trim().length > 100) nextErrors.displayName = 'Use no more than 100 characters.';
    if (password.length < 12 || password.length > 256) nextErrors.password = 'Use 12 to 256 characters.';
    setErrors(nextErrors);
    if (Object.keys(nextErrors).length > 0) return;

    setSubmitting(true);
    setFormError(null);
    try {
      await bootstrap({ username, displayName: displayName.trim() || null, password });
      await navigate('/', { replace: true });
    } catch (error) {
      if (error instanceof ApiError && error.kind === 'conflict') {
        await navigate('/login', { replace: true });
      } else {
        setFormError('The administrator account could not be created. Review the form and try again.');
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <AuthPageFrame eyebrow="First-run security" title="Create the first administrator" description="Establish the local administrator account. Service configuration comes in the next setup phase.">
      <Stack component="form" spacing={2.25} onSubmit={(event) => void submit(event)} noValidate>
        {formError ? <Alert severity="error" id="bootstrap-error">{formError}</Alert> : null}
        <TextField label="Username" autoComplete="username" required autoFocus value={username} onChange={(event) => setUsername(event.target.value)} error={Boolean(errors.username)} helperText={errors.username} slotProps={{ htmlInput: { 'aria-describedby': errors.username ? 'bootstrap-username-helper-text' : formError ? 'bootstrap-error' : undefined } }} />
        <TextField label="Display name (optional)" autoComplete="name" value={displayName} onChange={(event) => setDisplayName(event.target.value)} error={Boolean(errors.displayName)} helperText={errors.displayName} />
        <TextField label="Password" type="password" autoComplete="new-password" required value={password} onChange={(event) => setPassword(event.target.value)} error={Boolean(errors.password)} helperText={errors.password ?? 'Use at least 12 characters.'} />
        <Button type="submit" variant="contained" size="large" disabled={submitting}>{submitting ? 'Creating administrator…' : 'Create administrator'}</Button>
        <Typography variant="caption" color="text.secondary">Passwords are sent only to the backend for this request and are never stored by the browser application.</Typography>
      </Stack>
    </AuthPageFrame>
  );
}
