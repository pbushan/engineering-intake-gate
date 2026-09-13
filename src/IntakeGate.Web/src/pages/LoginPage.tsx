import { useState } from 'react';
import { Alert, Button, Stack, TextField } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { AuthPageFrame } from './AuthPageFrame';

export function LoginPage() {
  const { bootstrapConflict, login } = useAuth();
  const navigate = useNavigate();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<{ username?: string; password?: string }>({});

  const submit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const nextFieldErrors = {
      ...(!username.trim() ? { username: 'Username is required.' } : {}),
      ...(!password ? { password: 'Password is required.' } : {}),
    };
    setFieldErrors(nextFieldErrors);
    if (Object.keys(nextFieldErrors).length) {
      setError('Complete the highlighted fields.');
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      await login({ username, password });
      await navigate('/', { replace: true });
    } catch {
      setError('Sign-in failed. Check your credentials and try again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <AuthPageFrame eyebrow="Operational console" title="Welcome back" description="Sign in with your local Engineering Intake Gate account.">
      <Stack component="form" spacing={2.25} onSubmit={(event) => void submit(event)} noValidate>
        {bootstrapConflict && !error ? <Alert severity="warning">Another administrator completed bootstrap first. Sign in with an existing account.</Alert> : null}
        {error ? <Alert severity="error" id="login-error">{error}</Alert> : null}
        <TextField id="login-username" label="Username" name="username" autoComplete="username" required autoFocus value={username} onChange={(event) => { setUsername(event.target.value); setFieldErrors((current) => current.password ? { password: current.password } : {}); }} error={Boolean(fieldErrors.username)} helperText={fieldErrors.username} aria-describedby={fieldErrors.username ? 'login-username-helper-text' : error ? 'login-error' : undefined} />
        <TextField id="login-password" label="Password" name="password" type="password" autoComplete="current-password" required value={password} onChange={(event) => { setPassword(event.target.value); setFieldErrors((current) => current.username ? { username: current.username } : {}); }} error={Boolean(fieldErrors.password)} helperText={fieldErrors.password} aria-describedby={fieldErrors.password ? 'login-password-helper-text' : error ? 'login-error' : undefined} />
        <Button type="submit" variant="contained" size="large" disabled={submitting}>{submitting ? 'Signing in…' : 'Sign in'}</Button>
      </Stack>
    </AuthPageFrame>
  );
}
