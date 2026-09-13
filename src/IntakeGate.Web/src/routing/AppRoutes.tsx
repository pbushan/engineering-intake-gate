import { Box, CircularProgress, Typography } from '@mui/material';
import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { FullPageState } from '../components/FullPageState';
import { PageError } from '../components/PageError';
import { AppShell } from '../layout/AppShell';
import { BootstrapPage } from '../pages/BootstrapPage';
import { LoginPage } from '../pages/LoginPage';
import { NotFoundPage } from '../pages/NotFoundPage';
import { SetupIncompleteViewerPage } from '../pages/SetupIncompleteViewerPage';
import { ApplicationStateProvider, useApplicationState } from '../state/ApplicationStateContext';

const AnalyzeTicketPage = lazy(() => import('../pages/AnalyzeTicketPage').then((module) => ({ default: module.AnalyzeTicketPage })));
const HomePage = lazy(() => import('../pages/HomePage').then((module) => ({ default: module.HomePage })));
const HelpPage = lazy(() => import('../pages/HelpPage').then((module) => ({ default: module.HelpPage })));
const SetupWizardPage = lazy(() => import('../pages/SetupWizardPage').then((module) => ({ default: module.SetupWizardPage })));
const ProfileConfigurationPage = lazy(() => import('../pages/SetupWizardPage').then((module) => ({ default: module.ProfileConfigurationPage })));
const RunsPage = lazy(() => import('../pages/RunsPage').then((module) => ({ default: module.RunsPage })));
const RunDetailPage = lazy(() => import('../pages/RunDetailPage').then((module) => ({ default: module.RunDetailPage })));
const EvaluationDetailPage = lazy(() => import('../pages/EvaluationDetailPage').then((module) => ({ default: module.EvaluationDetailPage })));
const SystemHealthPage = lazy(() => import('../pages/SystemHealthPage').then((module) => ({ default: module.SystemHealthPage })));
const AuditPage = lazy(() => import('../pages/AuditPage').then((module) => ({ default: module.AuditPage })));

const operationalFallback = <Box role="status" aria-live="polite" sx={{ minHeight: 280, display: 'grid', placeItems: 'center' }}><Box sx={{ textAlign: 'center' }}><CircularProgress aria-label="Loading page" /><Typography color="text.secondary" sx={{ mt: 2 }}>Loading page…</Typography></Box></Box>;

function PublicEntry({ kind }: { kind: 'login' | 'bootstrap' }) {
  const { status, bootstrapAvailable } = useAuth();
  if (status === 'authenticated') return <Navigate to="/" replace />;
  if (kind === 'login' && bootstrapAvailable) return <Navigate to="/bootstrap" replace />;
  if (kind === 'bootstrap' && bootstrapAvailable === false) return <Navigate to="/login" replace />;
  return kind === 'login' ? <LoginPage /> : <BootstrapPage />;
}

function ProtectedApplication() {
  const { status, bootstrapAvailable } = useAuth();
  const location = useLocation();
  if (status !== 'authenticated') {
    return <Navigate to={bootstrapAvailable ? '/bootstrap' : '/login'} replace state={{ from: location.pathname }} />;
  }
  return <ApplicationStateProvider><AuthenticatedRoutes /></ApplicationStateProvider>;
}

function AuthenticatedRoutes() {
  const { user } = useAuth();
  const { loading, error, setup, reload } = useApplicationState();
  const admin = user?.role === 'admin';

  if (loading) {
    return <Routes><Route element={<AppShell />}><Route path="*" element={<Box role="status" aria-live="polite" sx={{ minHeight: 300, display: 'grid', placeItems: 'center' }}><Box sx={{ textAlign: 'center' }}><CircularProgress aria-label="Loading application status" /><Typography color="text.secondary" sx={{ mt: 2 }}>Loading backend status…</Typography></Box></Box>} /></Route></Routes>;
  }
  if (error) {
    return <Routes><Route element={<AppShell />}><Route path="*" element={<PageError error={error} retry={reload} />} /></Route></Routes>;
  }
  if (!setup) return null;

  const setupDestination = admin ? '/setup' : '/setup-required';
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<Navigate to={setup.setupComplete ? '/home' : setupDestination} replace />} />
        <Route path="home" element={setup.setupComplete ? <Suspense fallback={operationalFallback}><HomePage /></Suspense> : <Navigate to={setupDestination} replace />} />
        <Route path="analyze" element={setup.setupComplete && admin ? <Suspense fallback={operationalFallback}><AnalyzeTicketPage /></Suspense> : <Navigate to={setup.setupComplete ? '/runs' : setupDestination} replace />} />
        <Route path="runs" element={setup.setupComplete ? <Suspense fallback={operationalFallback}><RunsPage /></Suspense> : <Navigate to={setupDestination} replace />} />
        <Route path="runs/:runId" element={setup.setupComplete ? <Suspense fallback={operationalFallback}><RunDetailPage /></Suspense> : <Navigate to={setupDestination} replace />} />
        <Route path="runs/:runId/items/:evaluationId" element={setup.setupComplete ? <Suspense fallback={operationalFallback}><EvaluationDetailPage /></Suspense> : <Navigate to={setupDestination} replace />} />
        <Route path="system-health" element={<Suspense fallback={operationalFallback}><SystemHealthPage /></Suspense>} />
        <Route path="audit" element={<Suspense fallback={operationalFallback}><AuditPage /></Suspense>} />
        <Route path="setup" element={setup.setupComplete ? <Navigate to="/home" replace /> : admin ? <Suspense fallback={operationalFallback}><SetupWizardPage /></Suspense> : <Navigate to="/setup-required" replace />} />
        <Route path="configuration" element={setup.setupComplete && admin ? <Suspense fallback={operationalFallback}><ProfileConfigurationPage /></Suspense> : <Navigate to={setup.setupComplete ? '/home' : setupDestination} replace />} />
        <Route path="setup-required" element={setup.setupComplete ? <Navigate to="/home" replace /> : admin ? <Navigate to="/setup" replace /> : <SetupIncompleteViewerPage />} />
        <Route path="help" element={<Suspense fallback={operationalFallback}><HelpPage /></Suspense>} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}

export function AppRoutes() {
  const { status, restore } = useAuth();
  if (status === 'loading') return <FullPageState loading title="Opening Engineering Intake Gate" message="Restoring your secure backend session…" />;
  if (status === 'unavailable') return <FullPageState title="Backend unavailable" message="Engineering Intake Gate could not reach its backend. No local state was used as a fallback." actionLabel="Try again" onAction={() => void restore()} />;
  return (
    <Routes>
      <Route path="/login" element={<PublicEntry kind="login" />} />
      <Route path="/bootstrap" element={<PublicEntry kind="bootstrap" />} />
      <Route path="/*" element={<ProtectedApplication />} />
    </Routes>
  );
}
