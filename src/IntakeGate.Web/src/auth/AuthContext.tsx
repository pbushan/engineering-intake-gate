import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { BootstrapRequest, LoginRequest, SafeUser, UserRole } from '../api/contracts';

type AuthStatus = 'loading' | 'authenticated' | 'unauthenticated' | 'unavailable';

interface AuthContextValue {
  status: AuthStatus;
  user: SafeUser | null;
  bootstrapAvailable: boolean | null;
  bootstrapConflict: boolean;
  restore: () => Promise<void>;
  login: (request: LoginRequest) => Promise<void>;
  bootstrap: (request: BootstrapRequest) => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [user, setUser] = useState<SafeUser | null>(null);
  const [bootstrapAvailable, setBootstrapAvailable] = useState<boolean | null>(null);
  const [bootstrapConflict, setBootstrapConflict] = useState(false);

  const resolveUnauthenticated = useCallback(async (signal?: AbortSignal) => {
    try {
      const bootstrap = await apiClient.getBootstrapStatus(signal);
      setBootstrapAvailable(bootstrap.available);
      setBootstrapConflict(false);
      setStatus('unauthenticated');
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') return;
      setStatus('unavailable');
    }
  }, []);

  const restore = useCallback(async () => {
    setStatus('loading');
    const controller = new AbortController();
    try {
      const restoredUser = await apiClient.getSession(controller.signal);
      setUser(restoredUser);
      setBootstrapAvailable(false);
      setBootstrapConflict(false);
      setStatus('authenticated');
    } catch (error) {
      setUser(null);
      if (error instanceof ApiError && error.kind === 'unauthenticated') {
        await resolveUnauthenticated(controller.signal);
      } else if (!(error instanceof DOMException && error.name === 'AbortError')) {
        setStatus('unavailable');
      }
    }
  }, [resolveUnauthenticated]);

  useEffect(() => {
    const controller = new AbortController();
    const restoreSession = async () => {
      try {
        const restoredUser = await apiClient.getSession(controller.signal);
        setUser(restoredUser);
        setBootstrapAvailable(false);
        setBootstrapConflict(false);
        setStatus('authenticated');
      } catch (error) {
        if (error instanceof ApiError && error.kind === 'unauthenticated') {
          await resolveUnauthenticated(controller.signal);
        } else if (!(error instanceof DOMException && error.name === 'AbortError')) {
          setStatus('unavailable');
        }
      }
    };
    void restoreSession();
    return () => controller.abort();
  }, [resolveUnauthenticated]);

  useEffect(
    () =>
      apiClient.onUnauthorized(() => {
        setUser(null);
        setBootstrapAvailable(false);
        setBootstrapConflict(false);
        setStatus('unauthenticated');
        apiClient.resetCsrf();
      }),
    [],
  );

  const login = useCallback(async (request: LoginRequest) => {
    const authenticatedUser = await apiClient.login(request);
    setUser(authenticatedUser);
    setBootstrapAvailable(false);
    setBootstrapConflict(false);
    setStatus('authenticated');
  }, []);

  const bootstrap = useCallback(async (request: BootstrapRequest) => {
    try {
      const authenticatedUser = await apiClient.bootstrap(request);
      setUser(authenticatedUser);
      setBootstrapAvailable(false);
      setBootstrapConflict(false);
      setStatus('authenticated');
    } catch (error) {
      if (error instanceof ApiError && error.kind === 'conflict') {
        setBootstrapAvailable(false);
        setBootstrapConflict(true);
      }
      throw error;
    }
  }, []);

  const logout = useCallback(async () => {
    try {
      await apiClient.logout();
    } finally {
      setUser(null);
      setBootstrapAvailable(false);
      setBootstrapConflict(false);
      setStatus('unauthenticated');
    }
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({ status, user, bootstrapAvailable, bootstrapConflict, restore, login, bootstrap, logout }),
    [status, user, bootstrapAvailable, bootstrapConflict, restore, login, bootstrap, logout],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (context === null) throw new Error('useAuth must be used inside AuthProvider.');
  return context;
}

export const hasRole = (user: SafeUser | null, role: UserRole): boolean => user?.role === role;

export function AdminOnly({ children }: { children: React.ReactNode }) {
  const { user } = useAuth();
  return hasRole(user, 'admin') ? <>{children}</> : null;
}
