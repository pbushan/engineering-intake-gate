import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { asApiError, type ApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import type { ProfileState, SetupStatus, VersionInfo } from '../api/contracts';

interface ApplicationStateValue {
  loading: boolean;
  error: ApiError | null;
  setup: SetupStatus | null;
  profile: ProfileState | null;
  version: VersionInfo | null;
  reload: () => Promise<void>;
}

const ApplicationStateContext = createContext<ApplicationStateValue | null>(null);

export function ApplicationStateProvider({ children }: { children: React.ReactNode }) {
  const [state, setState] = useState<Omit<ApplicationStateValue, 'reload'>>({
    loading: true,
    error: null,
    setup: null,
    profile: null,
    version: null,
  });

  const load = useCallback(async (signal?: AbortSignal) => {
    const [setup, profile, version] = await Promise.all([
      apiClient.getSetupStatus(signal),
      apiClient.getProfile(signal),
      apiClient.getVersion(signal),
    ]);
    setState({ loading: false, error: null, setup, profile, version });
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal).catch((error: unknown) => {
        if (!(error instanceof DOMException && error.name === 'AbortError')) {
          setState((current) => ({ ...current, loading: false, error: asApiError(error) }));
        }
      });
    return () => controller.abort();
  }, [load]);

  const reload = useCallback(async () => {
    setState((current) => ({ ...current, loading: true, error: null }));
    try {
      await load();
    } catch (error) {
      setState((current) => ({ ...current, loading: false, error: asApiError(error) }));
      throw error;
    }
  }, [load]);
  const value = useMemo(() => ({ ...state, reload }), [state, reload]);
  return <ApplicationStateContext.Provider value={value}>{children}</ApplicationStateContext.Provider>;
}

export function useApplicationState(): ApplicationStateValue {
  const context = useContext(ApplicationStateContext);
  if (context === null) throw new Error('useApplicationState must be used inside ApplicationStateProvider.');
  return context;
}
