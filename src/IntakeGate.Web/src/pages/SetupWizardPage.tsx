import AddRounded from '@mui/icons-material/AddRounded';
import ArrowBackRounded from '@mui/icons-material/ArrowBackRounded';
import ArrowForwardRounded from '@mui/icons-material/ArrowForwardRounded';
import CheckCircleRounded from '@mui/icons-material/CheckCircleRounded';
import DeleteOutlineRounded from '@mui/icons-material/DeleteOutlineRounded';
import EditOutlined from '@mui/icons-material/EditOutlined';
import InfoOutlined from '@mui/icons-material/InfoOutlined';
import RefreshRounded from '@mui/icons-material/RefreshRounded';
import SaveOutlined from '@mui/icons-material/SaveOutlined';
import SecurityRounded from '@mui/icons-material/SecurityRounded';
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  AlertTitle,
  Box,
  Button,
  Card,
  CardContent,
  Checkbox,
  Chip,
  CircularProgress,
  Divider,
  FormControl,
  FormControlLabel,
  FormHelperText,
  FormLabel,
  Grid,
  InputLabel,
  Link,
  MenuItem,
  Paper,
  Radio,
  RadioGroup,
  Select,
  Stack,
  Step,
  StepLabel,
  Stepper,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { ApiError, asApiError } from '../api/api-error';
import { apiClient } from '../api/client';
import { ValidationSummary } from '../components/ValidationSummary';
import type {
  AiModelCandidate,
  AiModelDescriptor,
  AiProvider,
  AiSettings,
  AzureDevOpsQueryCandidate,
  AzureDevOpsSettings,
  CredentialMetadata,
  OnboardingDefaults,
  OnboardingDraftState,
  OnboardingDraftValues,
  ProfileState,
  SetupStatus,
} from '../api/contracts';
import { useApplicationState } from '../state/ApplicationStateContext';
import {
  cloneDraft,
  displayDate,
  numeric,
  resolveResumeStep,
  scheduleChoice,
  schedulePresets,
  setupSteps,
  type ScheduleChoice,
} from '../setup/wizard-model';

interface WizardData {
  setup: SetupStatus;
  defaults: OnboardingDefaults;
  draft: OnboardingDraftState;
  adoCredential: CredentialMetadata;
  adoSettings: AzureDevOpsSettings;
  aiSettings: AiSettings;
  aiCredentials: Record<AiProvider, CredentialMetadata>;
  profile: ProfileState;
}

export interface SetupWizardPageProps {
  mode?: 'setup' | 'edit';
}

function profileDraft(profile: ProfileState): OnboardingDraftState {
  if (!profile.exists || !profile.configurationRevision || !profile.policy || !profile.ai ||
      !profile.intakeState || !profile.schedule || !profile.processing || !profile.audit)
    throw setupError('The persisted profile is incomplete and cannot be edited safely.');
  return {
    exists: true,
    revision: profile.configurationRevision,
    values: {
      profileVersion: profile.profileVersion,
      policyUrl: profile.policy.url,
      intakeState: { ...profile.intakeState },
      aiRuntime: { timeoutSeconds: profile.ai.timeoutSeconds, pricing: profile.ai.pricing },
      schedule: {
        enabled: profile.schedule.enabled,
        expression: profile.schedule.expression,
        timezone: profile.schedule.timezone,
        initialLookback: profile.schedule.initialLookback,
      },
      processing: { ...profile.processing },
      audit: { ...profile.audit },
      exclusions: profile.exclusions?.map((item) => ({ ...item })) ?? [],
      policy: {
        id: profile.policy.id,
        version: profile.policy.version,
        criteria: profile.policy.criteria.map((item) => ({ ...item, na: { ...item.na } })),
      },
    },
    createdAtUtc: null,
    updatedAtUtc: null,
    fieldErrors: {},
    sectionErrors: {},
  };
}

const emptyCredential: CredentialMetadata = {
  configured: false,
  sourceKind: null,
  createdAtUtc: null,
  updatedAtUtc: null,
  verificationStatus: 'neverVerified',
  lastVerifiedAtUtc: null,
  verificationDiagnostic: null,
};

const providerLabel = (provider: AiProvider) => provider === 'openai' ? 'OpenAI' : 'Anthropic / Claude';
const valueOf = (value: number | string | null | undefined) => value == null ? '' : String(value);
const setupError = (message: string) => new ApiError('validation', message, { code: 'SetupInputRequired' });

const validationLabels: Readonly<Record<string, string>> = {
  'ado.connection': 'Azure DevOps connection',
  'ado.organizationUrl': 'Organization URL',
  'ado.project': 'Project',
  'ado.credential': 'Azure DevOps credential',
  'ado.environmentVariableName': 'Azure DevOps environment variable',
  'ado.savedQuery': 'Saved-query URL or GUID',
  'ai.provider': 'AI provider',
  'ai.credential': 'AI credential',
  'ai.environmentVariableName': 'AI environment variable',
  'ai.model': 'Model ID',
  policyUrl: 'Policy URL',
  'intakeState.validatedTag': 'Engineering Ready tag',
  'intakeState.incompleteTag': 'Intake Incomplete tag',
  'schedule.timezone': 'Timezone',
  'schedule.initialLookback': 'Initial lookback',
  'schedule.expression': 'Schedule expression',
  'processing.concurrency': 'Concurrency',
  'processing.retries': 'Retries',
  'processing.contentLimits.maximumTotalCharacters': 'Maximum total characters',
  'processing.contentLimits.maximumComments': 'Maximum comments',
  'processing.contentLimits.maximumExtractedTextCharacters': 'Maximum extracted text characters',
  'processing.attachmentLimits.maximumCount': 'Maximum attachments',
  'processing.attachmentLimits.maximumBytesPerAttachment': 'Bytes per attachment',
  'processing.attachmentLimits.maximumAggregateBytes': 'Aggregate attachment bytes',
  'processing.attachmentLimits.maximumPdfPages': 'Maximum PDF pages',
  'processing.attachmentLimits.maximumImageCount': 'Maximum images',
  'processing.attachmentLimits.maximumImageBytes': 'Maximum image bytes',
  'processing.attachmentLimits.maximumCsvRows': 'Maximum CSV rows',
  'processing.attachmentLimits.maximumStructuredTextDepth': 'Structured text depth',
  'aiRuntime.timeoutSeconds': 'AI timeout',
  'aiRuntime.pricing': 'AI pricing',
  exclusions: 'Exclusions',
  'audit.retentionDays': 'Retention days',
  'policy.id': 'Policy ID',
  'policy.version': 'Policy version',
  'policy.criteria': 'Intake criteria',
};

const validationLabel = (key: string) => {
  const criterion = /^policy\.criteria\[(\d+)]\.(.+)$/.exec(key);
  if (!criterion) return validationLabels[key] ?? key;
  const suffix: Record<string, string> = {
    id: 'ID', displayName: 'display name', description: 'description',
    evaluationGuidance: 'evaluation guidance', applicability: 'applicability', na: 'N/A settings',
  };
  return `Criterion ${Number(criterion[1]) + 1} ${suffix[criterion[2]!] ?? criterion[2]}`;
};

function StatusLine({ label, ready, detail }: { label: string; ready: boolean; detail?: string }) {
  return (
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' } }}>
      <Box><Typography sx={{ fontWeight: 700 }}>{label}</Typography>{detail ? <Typography variant="body2" color="text.secondary">{detail}</Typography> : null}</Box>
      <Chip icon={ready ? <CheckCircleRounded /> : <InfoOutlined />} label={ready ? 'Ready' : 'Needs attention'} color={ready ? 'success' : 'warning'} variant="outlined" />
    </Stack>
  );
}

function SectionCard({ title, description, children, id, invalid = false }: { title: string; description?: string; children: React.ReactNode; id?: string; invalid?: boolean }) {
  return (
    <Card id={id} tabIndex={invalid ? -1 : undefined} sx={invalid ? { outline: '2px solid', outlineColor: 'error.main' } : undefined}><CardContent><Stack spacing={2.5}>
      <Box><Typography component="h2" variant="h2">{title}</Typography>{description ? <Typography color="text.secondary" sx={{ mt: 0.5 }}>{description}</Typography> : null}</Box>
      {children}
    </Stack></CardContent></Card>
  );
}

function CredentialStatus({ metadata }: { metadata: CredentialMetadata }) {
  return (
    <Paper variant="outlined" sx={{ p: 2, bgcolor: 'background.default' }}>
      <Grid container spacing={2}>
        <Grid size={{ xs: 12, sm: 4 }}><Typography variant="caption" color="text.secondary">Credential</Typography><Typography sx={{ fontWeight: 700 }}>{metadata.configured ? 'Configured' : 'Not configured'}</Typography></Grid>
        <Grid size={{ xs: 12, sm: 4 }}><Typography variant="caption" color="text.secondary">Source</Typography><Typography>{metadata.sourceKind === 'environmentReference' ? 'Environment reference' : metadata.sourceKind === 'locallyEncrypted' ? 'Local encrypted secret' : '—'}</Typography></Grid>
        <Grid size={{ xs: 12, sm: 4 }}><Typography variant="caption" color="text.secondary">Verification</Typography><Typography sx={{ textTransform: 'capitalize' }}>{metadata.verificationStatus.replace(/([A-Z])/g, ' $1')}</Typography><Typography variant="caption" color="text.secondary">{displayDate(metadata.lastVerifiedAtUtc)}</Typography></Grid>
      </Grid>
    </Paper>
  );
}

export function SetupWizardPage({ mode = 'setup' }: SetupWizardPageProps) {
  const editing = mode === 'edit';
  const navigate = useNavigate();
  const application = useApplicationState();
  const headingRef = useRef<HTMLHeadingElement>(null);
  const initializeInFlight = useRef(false);
  const draftDirtyRef = useRef(false);
  const [data, setData] = useState<WizardData | null>(null);
  const [activeStep, setActiveStep] = useState(0);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [draftValues, setDraftValues] = useState<OnboardingDraftValues | null>(null);
  const [draftDirty, setDraftDirty] = useState(false);

  const [adoOrganization, setAdoOrganization] = useState('');
  const [adoProject, setAdoProject] = useState('');
  const [adoSecretMode, setAdoSecretMode] = useState<'local' | 'environment'>('local');
  const [adoSecret, setAdoSecret] = useState('');
  const [adoEnvironment, setAdoEnvironment] = useState('');
  const [queryInput, setQueryInput] = useState('');
  const [queryCandidate, setQueryCandidate] = useState<AzureDevOpsQueryCandidate | null>(null);

  const [provider, setProvider] = useState<AiProvider>('openai');
  const [aiSecretMode, setAiSecretMode] = useState<'local' | 'environment'>('local');
  const [aiSecret, setAiSecret] = useState('');
  const [aiEnvironment, setAiEnvironment] = useState('');
  const [models, setModels] = useState<AiModelDescriptor[] | null>(null);
  const [modelId, setModelId] = useState('');
  const [manualModel, setManualModel] = useState(false);
  const [modelCandidate, setModelCandidate] = useState<AiModelCandidate | null>(null);

  const fetchData = useCallback(async (): Promise<WizardData> => {
    const [setup, defaults, onboardingDraft, profile, adoCredential, adoSettings, aiSettings, openai, anthropic] = await Promise.all([
      apiClient.getSetupStatus(), apiClient.getSetupDefaults(), editing ? Promise.resolve(null) : apiClient.getOnboardingDraft(),
      apiClient.getProfile(),
      apiClient.getAzureDevOpsCredential(), apiClient.getAzureDevOpsSettings(), apiClient.getAiSettings(),
      apiClient.getAiCredential('openai'), apiClient.getAiCredential('anthropic'),
    ]);
    if (editing && !profile.exists) throw setupError('Complete initial setup before editing the profile.');
    const draft = editing ? profileDraft(profile) : onboardingDraft!;
    return { setup, defaults, draft, profile, adoCredential, adoSettings, aiSettings, aiCredentials: { openai, anthropic } };
  }, [editing]);

  const applyData = useCallback((next: WizardData, replaceDraft = false) => {
    setData(next);
    setAdoOrganization(next.adoSettings.organizationUrl ?? '');
    setAdoProject(next.adoSettings.project ?? '');
    setQueryInput((current) => current || next.adoSettings.savedQueryId || '');
    if (next.aiSettings.provider === 'openai' || next.aiSettings.provider === 'anthropic') {
      setProvider(next.aiSettings.provider);
      setModelId(next.aiSettings.model ?? '');
    }
    if (next.draft.values && (replaceDraft || !draftDirtyRef.current)) {
      setDraftValues(cloneDraft(next.draft.values));
      setDraftDirty(false);
      draftDirtyRef.current = false;
    }
  }, []);

  const refresh = useCallback(async (replaceDraft = false) => {
    const next = await fetchData();
    applyData(next, replaceDraft);
    return next;
  }, [applyData, fetchData]);

  useEffect(() => {
    let active = true;
    void fetchData().then((next) => {
      if (!active) return;
      applyData(next, true);
      setActiveStep(editing ? 0 : resolveResumeStep(next.setup, { adoSettingsReady: Boolean(next.adoSettings.organizationUrl && next.adoSettings.project) }));
      setLoading(false);
    }).catch((reason: unknown) => {
      if (!active) return;
      setError(asApiError(reason));
      setLoading(false);
    });
    return () => { active = false; };
  }, [applyData, editing, fetchData]);

  useEffect(() => {
    if (!data) return;
    headingRef.current?.focus();
    void apiClient.recordSetupProgress(setupSteps[activeStep]?.id ?? 'Welcome').catch((reason: unknown) => {
      setError(asApiError(reason));
    });
  }, [activeStep, data]);

  useEffect(() => {
    if (editing || activeStep !== 3 || !data || data.draft.exists || initializeInFlight.current) return;
    initializeInFlight.current = true;
    setBusy('initialize-draft');
    void apiClient.initializeOnboardingDraft().then(async () => {
      const next = await refresh(true);
      setNotice('Server defaults initialized a persisted onboarding draft.');
      setActiveStep(Math.min(resolveResumeStep(next.setup, { adoSettingsReady: Boolean(next.adoSettings.organizationUrl && next.adoSettings.project) }), 3));
    }).catch((reason: unknown) => setError(asApiError(reason))).finally(() => {
      initializeInFlight.current = false;
      setBusy(null);
    });
  }, [activeStep, data, editing, refresh]);

  useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => {
      if (!draftDirty) return;
      event.preventDefault();
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [draftDirty]);

  const action = async (name: string, task: () => Promise<string | void>) => {
    setBusy(name);
    setError(null);
    setNotice(null);
    try {
      const message = await task();
      if (message) setNotice(message);
    } catch (reason) {
      const nextError = asApiError(reason);
      setError(nextError);
      if (!Object.keys(nextError.fieldErrors).length && !Object.keys(nextError.sectionErrors).length)
        headingRef.current?.focus();
    } finally {
      setBusy(null);
    }
  };

  const goTo = (index: number) => {
    setError(null);
    setNotice(null);
    setActiveStep(Math.max(0, Math.min(setupSteps.length - 1, index)));
  };

  const mutateDraft = (mutation: (values: OnboardingDraftValues) => void) => {
    setDraftValues((current) => {
      if (!current) return current;
      const next = cloneDraft(current);
      mutation(next);
      return next;
    });
    setDraftDirty(true);
    draftDirtyRef.current = true;
  };

  const clearValidation = (key: string) => {
    setError((current) => current?.kind === 'validation' ? current.withoutField(key) : current);
  };

  const focusValidationTarget = (key: string) => {
    const target = document.getElementById(`setup-${key}`);
    target?.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
    if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLButtonElement)
      target.focus();
    else target?.querySelector<HTMLElement>('input, textarea, button, [tabindex="0"]')?.focus();
  };

  const saveDraft = async (): Promise<WizardData> => {
    if (!data?.draft.exists || data.draft.revision == null || !draftValues) {
      throw setupError(editing ? 'The persisted profile revision is unavailable.' : 'The onboarding draft is unavailable.');
    }
    try {
      const saved = editing
        ? await apiClient.updateProfile({
            expectedRevision: String(data.draft.revision),
            profile: draftValues,
          })
        : await apiClient.updateOnboardingDraft({ expectedRevision: data.draft.revision, values: draftValues });
      setDraftDirty(false);
      draftDirtyRef.current = false;
      if (!editing)
        setData((current) => current ? { ...current, draft: saved as OnboardingDraftState } : current);
      return await refresh(true);
    } catch (reason) {
      const apiError = asApiError(reason);
      if (apiError.status === 409) {
        const next = await refresh(true);
        setNotice(editing
          ? 'The profile or integration state changed in another session. The latest persisted values are loaded; review them before retrying.'
          : 'Setup changed in another session. The latest saved draft has been loaded; review it before saving again.');
        if (!editing)
          setActiveStep(Math.min(activeStep, resolveResumeStep(next.setup, { adoSettingsReady: Boolean(next.adoSettings.organizationUrl && next.adoSettings.project) })));
      }
      throw reason;
    }
  };

  const handleContinue = () => {
    if (!data) return;
    if (activeStep === 0) { goTo(1); return; }
    if (activeStep === 1) {
      if (!data.setup.azureDevOpsCredentialVerified || !adoOrganization || !adoProject) {
        setError(setupError('Save Azure DevOps settings and verify the credential before continuing.'));
        return;
      }
      goTo(2); return;
    }
    if (activeStep === 2) {
      if (!data.setup.aiCredentialVerified || !data.setup.aiModelConfigured ||
          provider !== data.aiSettings.provider || modelId.trim() !== data.aiSettings.model) {
        setError(setupError('Verify the selected provider and validate and confirm the selected model before continuing.'));
        return;
      }
      goTo(3); return;
    }
    if (activeStep === 3) { goTo(4); return; }
    if (activeStep === 4) {
      if (editing) { goTo(5); return; }
      void action('save-profile', async () => {
        const next = await saveDraft();
        if (!next.setup.profileDetailsComplete || !next.setup.policyDetailsComplete) {
          throw new ApiError('validation', 'Some profile and policy fields need attention.', {
            code: 'ValidationFailed',
            fieldErrors: next.draft.fieldErrors ?? {},
            sectionErrors: next.draft.sectionErrors ?? {},
          });
        }
        goTo(5);
        return 'Profile and policy draft saved.';
      });
      return;
    }
    if (activeStep === 5) {
      if (!data.setup.azureDevOpsSavedQueryConfirmed ||
          queryInput.trim().toLowerCase() !== String(data.adoSettings.savedQueryId ?? '').toLowerCase()) {
        setError(setupError('Validate and confirm the saved query before continuing.'));
        return;
      }
      goTo(6); return;
    }
    if (activeStep === 6) {
      if (editing) { goTo(7); return; }
      void action('save-schedule', async () => {
        await saveDraft();
        await refresh(true);
        goTo(7);
        return editing ? 'Schedule changes saved.' : 'Schedule saved to the onboarding draft.';
      });
    }
  };

  if (loading) return <Stack role="status" aria-live="polite" sx={{ minHeight: 360, alignItems: 'center', justifyContent: 'center' }}><CircularProgress aria-label="Loading setup wizard" /><Typography color="text.secondary" sx={{ mt: 2 }}>Loading authoritative setup state…</Typography></Stack>;
  if (!data) return <Alert severity="error"><AlertTitle>Setup could not be loaded</AlertTitle>{error?.message ?? 'Try again.'}<Button onClick={() => window.location.reload()}>Reload</Button></Alert>;

  const currentCredential = data.aiCredentials[provider] ?? emptyCredential;
  const draft = draftValues;
  const processing = draft?.processing;
  const content = processing?.contentLimits;
  const attachments = processing?.attachmentLimits;
  const criteria = draft?.policy?.criteria ?? [];

  return (
    <Stack spacing={3} sx={{ maxWidth: 1080 }}>
      <Box>
        <Typography variant="overline" color="primary.main" sx={{ fontWeight: 800 }}>{editing ? 'Administrator configuration · Beta' : 'Administrator setup'}</Typography>
        <Typography ref={headingRef} tabIndex={-1} component="h1" variant="h1">{editing ? 'Edit profile and configuration' : 'Set up Engineering Intake Gate'}</Typography>
        <Typography color="text.secondary" sx={{ mt: 1, maxWidth: 760 }}>{editing ? 'Review and update the persisted singleton profile using the same guarded workflow as initial setup. Existing values remain unchanged unless you edit and save them.' : 'Connect the services and define the intake evidence your team needs. Saved progress comes from the backend, so this wizard can safely resume after a refresh.'}</Typography>
        <Link component={RouterLink} to="/help" sx={{ display: 'inline-block', mt: 1 }}>Open setup help</Link>
      </Box>

      <Alert severity="success" icon={<SecurityRounded />}><AlertTitle>Controlled Dry Run</AlertTitle>No Azure DevOps modifications will be made. Production activation is not enabled in this release.</Alert>

      <Paper variant="outlined" sx={{ p: { xs: 1.5, md: 2.5 }, overflowX: 'auto' }}>
        <Stepper activeStep={activeStep} alternativeLabel aria-label="Setup progress" sx={{ minWidth: 760 }}>
          {setupSteps.map((step, index) => <Step key={step.id}><StepLabel error={index === activeStep && error?.kind === 'validation'}>{step.label}</StepLabel></Step>)}
        </Stepper>
      </Paper>

      {error && (Object.keys(error.fieldErrors).length || Object.keys(error.sectionErrors).length)
        ? <ValidationSummary error={error} labels={Object.fromEntries([...Object.keys(error.fieldErrors), ...Object.keys(error.sectionErrors)].map((key) => [key, validationLabel(key)]))} focusTarget={focusValidationTarget} />
        : error ? <Alert severity={error.kind === 'conflict' ? 'warning' : 'error'} role="alert" aria-live="assertive" onClose={() => setError(null)}><AlertTitle>We couldn&apos;t complete this action</AlertTitle>{error.message}</Alert> : null}
      {notice ? <Alert severity="success" role="status" aria-live="polite" onClose={() => setNotice(null)}>{notice}</Alert> : null}

      {activeStep === 0 ? <WelcomeStep /> : null}
      {activeStep === 1 ? (
        <Stack spacing={2.5}>
          <SectionCard id="setup-ado.connection" invalid={Boolean(error?.sectionErrors['ado.connection'])} title="Azure DevOps connection" description={editing ? 'Organization and project are fixed for this iteration. You can replace and verify the credential, then validate a different saved query.' : 'Save the organization and project through the backend, then verify access. The browser never connects to Azure DevOps directly.'}>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 7 }}><TextField id="setup-ado.organizationUrl" fullWidth required disabled={editing} label="Organization URL" value={adoOrganization} onChange={(event) => { setAdoOrganization(event.target.value); clearValidation('ado.organizationUrl'); }} error={Boolean(error?.fieldErrors['ado.organizationUrl'])} helperText={error?.fieldErrors['ado.organizationUrl']?.[0] ?? (editing ? 'Organization cannot be changed in Profile / Configuration.' : 'The HTTPS URL for the Azure DevOps organization.')} /></Grid>
              <Grid size={{ xs: 12, md: 5 }}><TextField id="setup-ado.project" fullWidth required disabled={editing} label="Project" value={adoProject} onChange={(event) => { setAdoProject(event.target.value); clearValidation('ado.project'); }} error={Boolean(error?.fieldErrors['ado.project'])} helperText={error?.fieldErrors['ado.project']?.[0] ?? (editing ? 'Project cannot be changed in Profile / Configuration.' : undefined)} /></Grid>
            </Grid>
            {!editing ? <Button sx={{ alignSelf: 'flex-start' }} variant="outlined" startIcon={<SaveOutlined />} disabled={busy !== null || !adoOrganization.trim() || !adoProject.trim()} onClick={() => void action('ado-settings', async () => { await apiClient.saveAzureDevOpsSettings(adoOrganization.trim(), adoProject.trim()); await refresh(); return 'Azure DevOps settings saved.'; })}>{busy === 'ado-settings' ? 'Saving…' : 'Save settings'}</Button> : null}
          </SectionCard>
          <SectionCard title="Azure DevOps credential" description="A PAT is used only by the backend. Saved credentials can be replaced but never redisplayed.">
            <CredentialStatus metadata={data.adoCredential} />
            <FormControl><FormLabel id="ado-secret-source">Credential source</FormLabel><RadioGroup row aria-labelledby="ado-secret-source" value={adoSecretMode} onChange={(event) => setAdoSecretMode(event.target.value as 'local' | 'environment')}><FormControlLabel value="local" control={<Radio />} label="Local encrypted PAT" /><FormControlLabel value="environment" control={<Radio />} label="Environment-variable reference" /></RadioGroup></FormControl>
            {adoSecretMode === 'local' ? <TextField id="setup-ado.credential" type="password" autoComplete="new-password" label={data.adoCredential.configured ? 'Replacement PAT' : 'Personal access token'} value={adoSecret} onChange={(event) => { setAdoSecret(event.target.value); clearValidation('ado.credential'); }} error={Boolean(error?.fieldErrors['ado.credential'])} helperText={error?.fieldErrors['ado.credential']?.[0] ?? 'Visible only while entering or replacing. Use the minimum read permissions needed for the saved query.'} /> : <TextField id="setup-ado.environmentVariableName" label="Environment variable name" value={adoEnvironment} onChange={(event) => { setAdoEnvironment(event.target.value); clearValidation('ado.environmentVariableName'); }} error={Boolean(error?.fieldErrors['ado.environmentVariableName'])} helperText={error?.fieldErrors['ado.environmentVariableName']?.[0] ?? 'Only the variable name is stored. The backend environment must provide its value.'} />}
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
              <Button variant="contained" disabled={busy !== null || (adoSecretMode === 'local' ? !adoSecret : !adoEnvironment)} onClick={() => void action('ado-credential', async () => {
                if (adoSecretMode === 'local') { await apiClient.replaceAzureDevOpsCredential(adoSecret); setAdoSecret(''); }
                else { await apiClient.setAzureDevOpsCredentialEnvironment(adoEnvironment.trim()); setAdoEnvironment(''); }
                await refresh();
                return data.adoCredential.configured ? 'Azure DevOps credential replaced.' : 'Azure DevOps credential configured.';
              })}>{busy === 'ado-credential' ? 'Saving…' : data.adoCredential.configured ? 'Replace credential' : 'Save credential'}</Button>
              <Button variant="outlined" disabled={busy !== null || !data.adoCredential.configured || !adoOrganization || !adoProject} onClick={() => void action('ado-test', async () => { await apiClient.testAzureDevOpsConnection(); await refresh(); return 'Azure DevOps connection verified.'; })}>{busy === 'ado-test' ? 'Testing…' : 'Test connection'}</Button>
            </Stack>
          </SectionCard>
        </Stack>
      ) : null}

      {activeStep === 2 ? (
        <Stack spacing={2.5}>
          <SectionCard title="Choose one AI provider" description="Provider selection is explicit. Switching here does not delete credentials saved for the other provider.">
            <FormControl><FormLabel id="ai-provider-label">AI provider</FormLabel><RadioGroup row aria-labelledby="ai-provider-label" value={provider} onChange={(event) => { setProvider(event.target.value as AiProvider); setModels(null); setModelCandidate(null); setModelId(''); }}><FormControlLabel value="openai" control={<Radio />} label="OpenAI" /><FormControlLabel value="anthropic" control={<Radio />} label="Anthropic / Claude" /></RadioGroup></FormControl>
          </SectionCard>
          <SectionCard title={`${providerLabel(provider)} credential`} description="The API key is sent only to the backend and is never stored in browser persistence.">
            <CredentialStatus metadata={currentCredential} />
            <FormControl><FormLabel id="ai-secret-source">Credential source</FormLabel><RadioGroup row aria-labelledby="ai-secret-source" value={aiSecretMode} onChange={(event) => setAiSecretMode(event.target.value as 'local' | 'environment')}><FormControlLabel value="local" control={<Radio />} label="Local encrypted API key" /><FormControlLabel value="environment" control={<Radio />} label="Environment-variable reference" /></RadioGroup></FormControl>
            {aiSecretMode === 'local' ? <TextField id="setup-ai.credential" type="password" autoComplete="new-password" label={currentCredential.configured ? 'Replacement API key' : 'API key'} value={aiSecret} onChange={(event) => { setAiSecret(event.target.value); clearValidation('ai.credential'); }} error={Boolean(error?.fieldErrors['ai.credential'])} helperText={error?.fieldErrors['ai.credential']?.[0]} /> : <TextField id="setup-ai.environmentVariableName" label="Environment variable name" value={aiEnvironment} onChange={(event) => { setAiEnvironment(event.target.value); clearValidation('ai.environmentVariableName'); }} error={Boolean(error?.fieldErrors['ai.environmentVariableName'])} helperText={error?.fieldErrors['ai.environmentVariableName']?.[0] ?? 'The backend environment must define this variable; its value is never returned.'} />}
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
              <Button variant="contained" disabled={busy !== null || (aiSecretMode === 'local' ? !aiSecret : !aiEnvironment)} onClick={() => void action('ai-credential', async () => {
                if (aiSecretMode === 'local') { await apiClient.replaceAiCredential(provider, aiSecret); setAiSecret(''); }
                else { await apiClient.setAiCredentialEnvironment(provider, aiEnvironment.trim()); setAiEnvironment(''); }
                await refresh(); return `${providerLabel(provider)} credential configured.`;
              })}>{busy === 'ai-credential' ? 'Saving…' : currentCredential.configured ? 'Replace credential' : 'Save credential'}</Button>
              <Button variant="outlined" disabled={busy !== null || !currentCredential.configured} onClick={() => void action('ai-test', async () => { await apiClient.testAiCredential(provider); await refresh(); return `${providerLabel(provider)} credential verified.`; })}>{busy === 'ai-test' ? 'Verifying…' : 'Verify provider'}</Button>
            </Stack>
          </SectionCard>
          <SectionCard title="Select a model" description="Discovery is assistive. Every selection, including a manually entered ID, must be validated and explicitly confirmed by the backend.">
            {data.aiSettings.modelConfirmed ? <Alert severity="success">Confirmed model: <strong>{data.aiSettings.model}</strong>{data.aiSettings.provider ? ` (${providerLabel(data.aiSettings.provider as AiProvider)})` : ''}</Alert> : null}
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
              <Button variant="outlined" startIcon={<RefreshRounded />} disabled={busy !== null || currentCredential.verificationStatus !== 'verified'} onClick={() => void action('models', async () => { const result = await apiClient.discoverAiModels(provider); setModels(result.models); return result.models.length ? `${result.models.length} models available.` : 'No models were returned. Enter a model ID manually.'; })}>{busy === 'models' ? 'Discovering…' : models ? 'Retry discovery' : 'Discover models'}</Button>
              <Button onClick={() => { setManualModel((value) => !value); setModelCandidate(null); }}>{manualModel ? 'Choose discovered model' : 'Enter model ID manually'}</Button>
            </Stack>
            {!manualModel && models ? models.length ? <FormControl fullWidth><InputLabel id="model-select-label">Model</InputLabel><Select labelId="model-select-label" label="Model" value={modelId} onChange={(event) => { setModelId(event.target.value); setModelCandidate(null); }}>{models.map((model) => <MenuItem key={`${model.provider}:${model.id}`} value={model.id}>{model.displayName ? `${model.displayName} — ${model.id}` : model.id}</MenuItem>)}</Select></FormControl> : <Alert severity="info">Model discovery returned no choices. Use manual model entry.</Alert> : null}
            {manualModel ? <TextField id="setup-ai.model" label="Model ID" value={modelId} onChange={(event) => { setModelId(event.target.value); setModelCandidate(null); clearValidation('ai.model'); }} error={Boolean(error?.fieldErrors['ai.model'])} helperText={error?.fieldErrors['ai.model']?.[0] ?? 'The backend validates availability for the selected provider; manual entry does not bypass confirmation.'} /> : null}
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
              <Button variant="outlined" disabled={busy !== null || !modelId.trim() || currentCredential.verificationStatus !== 'verified'} onClick={() => void action('model-validate', async () => { const candidate = await apiClient.validateAiModel(provider, modelId.trim()); setModelCandidate(candidate); return `Model ${candidate.model} validated. Confirm it to continue.`; })}>{busy === 'model-validate' ? 'Validating…' : 'Validate model'}</Button>
              {modelCandidate ? <Button variant="contained" disabled={busy !== null || modelCandidate.provider !== provider} onClick={() => void action('model-confirm', async () => {
                try {
                  await apiClient.confirmAiModel(modelCandidate.confirmationToken);
                } catch (reason) {
                  if (asApiError(reason).status === 409) {
                    setModelCandidate(null);
                    setNotice('The validated model selection expired or changed. Validate the model again before confirming it.');
                  }
                  throw reason;
                }
                setModelCandidate(null);
                setModels(null);
                await refresh();
                return 'AI provider and model confirmed.';
              })}>{busy === 'model-confirm' ? 'Confirming…' : `Confirm ${modelCandidate.model}`}</Button> : null}
            </Stack>
          </SectionCard>
        </Stack>
      ) : null}

      {activeStep === 3 ? <DefaultsStep defaults={data.defaults} draft={data.draft} initializing={!editing && busy === 'initialize-draft'} /> : null}
      {activeStep === 4 && draft ? (
        <ProfileStep values={draft} criteria={criteria} processing={processing} content={content} attachments={attachments} busy={busy !== null} dirty={draftDirty} validation={error?.kind === 'validation' ? error : null} clearValidation={clearValidation} mutate={mutateDraft} save={editing ? null : () => void action('save-profile-only', async () => { await saveDraft(); return 'Profile and policy draft saved.'; })} />
      ) : null}
      {activeStep === 5 ? (
        <SectionCard title="Confirm the governed saved query" description="Enter an Azure DevOps saved-query URL or GUID. The backend validates it against the fixed organization, project, and current verified credential; arbitrary WIQL is not accepted.">
          <StatusLine label="Current query" ready={data.setup.azureDevOpsSavedQueryConfirmed && queryInput.trim().toLowerCase() === String(data.adoSettings.savedQueryId ?? '').toLowerCase()} detail={data.adoSettings.savedQueryId ? `Saved query ${data.adoSettings.savedQueryId}` : 'No query confirmed'} />
          <TextField id="setup-ado.savedQuery" required label="Saved-query URL or GUID" value={queryInput} onChange={(event) => { setQueryInput(event.target.value); setQueryCandidate(null); clearValidation('ado.savedQuery'); }} error={Boolean(error?.fieldErrors['ado.savedQuery'])} helperText={error?.fieldErrors['ado.savedQuery']?.[0] ?? 'Use the saved query that defines the only work-item population this installation may inspect.'} />
          <Button sx={{ alignSelf: 'flex-start' }} variant="outlined" disabled={busy !== null || !queryInput.trim()} onClick={() => void action('query-validate', async () => { const result = await apiClient.validateSavedQuery(adoOrganization, adoProject, queryInput.trim()); setQueryCandidate(result); return result.totalCount === 0 ? 'Query is valid. No work items currently match.' : `Query is valid and returned ${String(result.totalCount)} work items.`; })}>{busy === 'query-validate' ? 'Validating…' : 'Validate query'}</Button>
          {queryCandidate ? <QueryPreview candidate={queryCandidate} confirm={() => void action('query-confirm', async () => {
            try {
              await apiClient.confirmSavedQuery(queryCandidate.confirmationToken);
            } catch (reason) {
              if (asApiError(reason).status === 409) {
                setQueryCandidate(null);
                setNotice('The query preview expired or the connection changed. Validate the saved query again before confirming it.');
              }
              throw reason;
            }
            setQueryCandidate(null);
            const next = await refresh();
            setQueryInput(String(next.adoSettings.savedQueryId ?? ''));
            return 'Saved query confirmed.';
          })} busy={busy === 'query-confirm'} /> : null}
        </SectionCard>
      ) : null}
      {activeStep === 6 && draft ? <ScheduleStep values={draft} kind={scheduleChoice(draft)} validation={error?.kind === 'validation' ? error : null} clearValidation={clearValidation} mutate={mutateDraft} /> : null}
      {activeStep === 7 ? <ReviewStep data={data} edit={goTo} editing={editing} dirty={draftDirty} finalize={() => void action('finalize', async () => {
        if (editing) {
          if (draftDirty) {
            try {
              await saveDraft();
            } catch (reason) {
              const apiError = asApiError(reason);
              if (apiError.kind === 'validation') {
                const scheduleError = [...Object.keys(apiError.fieldErrors), ...Object.keys(apiError.sectionErrors)]
                  .some((key) => key.startsWith('schedule.'));
                setActiveStep(scheduleError ? 6 : 4);
              }
              throw reason;
            }
          }
          const status = await apiClient.getSetupStatus();
          if (!status.setupComplete || !status.runtimeActivationCurrent)
            throw setupError('The profile could not be activated with the current verified integration configuration. Review the incomplete steps and retry.');
          await application.reload();
          void navigate('/home', { replace: true });
          return;
        }
        const latest = await refresh(true);
        if (latest.draft.revision == null) throw setupError('The onboarding draft is unavailable.');
        let profile;
        try {
          profile = await apiClient.finalizeSetup(latest.draft.revision);
        } catch (reason) {
          if (asApiError(reason).status === 409) {
            const corrected = await refresh(true);
            setActiveStep(resolveResumeStep(corrected.setup, { adoSettingsReady: Boolean(corrected.adoSettings.organizationUrl && corrected.adoSettings.project) }));
            setNotice('Setup changed while finalizing. The latest authoritative state is loaded; review any required correction and try again.');
          }
          throw reason;
        }
        const status = await apiClient.getSetupStatus();
        if (!status.setupComplete || !status.runtimeActivationCurrent || !profile.exists) throw setupError('Configuration was saved, but runtime activation is not current. Setup remains open.');
        await application.reload();
        void navigate('/home', { replace: true });
      })} busy={busy === 'finalize'} /> : null}

      <Divider />
      <Stack direction={{ xs: 'column-reverse', sm: 'row' }} spacing={1.5} sx={{ justifyContent: 'space-between' }}>
        <Stack direction="row" spacing={1}><Button startIcon={<ArrowBackRounded />} disabled={activeStep === 0 || busy !== null} onClick={() => goTo(activeStep - 1)}>Back</Button>{editing ? <Button disabled={busy !== null} onClick={() => { if (!draftDirty || window.confirm('Discard unsaved profile changes?')) void navigate('/home'); }}>Cancel</Button> : null}</Stack>
        {activeStep < 7 ? <Button variant="contained" endIcon={<ArrowForwardRounded />} disabled={busy !== null || (!editing && activeStep === 3 && !data.draft.exists)} onClick={handleContinue}>{activeStep === 0 ? (editing ? 'Review configuration' : 'Continue setup') : activeStep === 4 ? 'Save & continue' : activeStep === 6 ? 'Save & continue' : 'Continue'}</Button> : null}
      </Stack>
    </Stack>
  );
}

export function ProfileConfigurationPage() {
  return <SetupWizardPage mode="edit" />;
}

function WelcomeStep() {
  return (
    <SectionCard title="Welcome" description="A short, guided setup will make this installation ready for its first controlled evaluation.">
      <Grid container spacing={2}>
        <Grid size={{ xs: 12, md: 4 }}><Paper variant="outlined" sx={{ p: 2, height: '100%' }}><Typography variant="h3">Check intake completeness</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>The gate checks whether support and engineering intake has enough relevant evidence and context to begin investigation without avoidable clarification.</Typography></Paper></Grid>
        <Grid size={{ xs: 12, md: 4 }}><Paper variant="outlined" sx={{ p: 2, height: '100%' }}><Typography variant="h3">Connect two services</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>You’ll connect Azure DevOps and exactly one AI provider, then confirm the saved query and model.</Typography></Paper></Grid>
        <Grid size={{ xs: 12, md: 4 }}><Paper variant="outlined" sx={{ p: 2, height: '100%' }}><Typography variant="h3">Stay in Dry Run</Typography><Typography color="text.secondary" sx={{ mt: 1 }}>Controlled Dry Run evaluates intake without making Azure DevOps modifications.</Typography></Paper></Grid>
      </Grid>
      <Alert severity="info">Engineering Ready means only that the intake is sufficient to begin investigation. It does not confirm a defect, cause, ownership, severity, priority, solution, or commitment.</Alert>
    </SectionCard>
  );
}

function DefaultsStep({ defaults, draft, initializing }: { defaults: OnboardingDefaults; draft: OnboardingDraftState; initializing: boolean }) {
  return (
    <Stack spacing={2.5}>
      <SectionCard title="System defaults" description="These backend-provided values seed this profile. Changing them later requires an explicit profile update.">
        {initializing ? <Stack role="status" direction="row" spacing={1.5}><CircularProgress size={22} /><Typography>Initializing the persisted draft from server defaults…</Typography></Stack> : <StatusLine label="Onboarding draft" ready={draft.exists} detail={draft.exists ? `Persisted revision ${String(draft.revision)}` : 'Not initialized'} />}
        <Grid container spacing={1.5}>{defaults.serverSeededFields.map((field) => <Grid key={field} size={{ xs: 12, sm: 6 }}><Paper variant="outlined" sx={{ p: 1.5 }}><Typography variant="body2" sx={{ fontFamily: 'monospace' }}>{field}</Typography><Typography variant="caption" color="text.secondary">Seeded by the backend</Typography></Paper></Grid>)}</Grid>
      </SectionCard>
      <SectionCard title="Information you’ll provide" description="These fields have no generic product default and must be supplied by an Administrator.">
        <Box component="ul" sx={{ m: 0, pl: 3, columns: { md: 2 } }}>{defaults.requiredAdminFields.map((field) => <Typography component="li" variant="body2" key={field} sx={{ breakInside: 'avoid', mb: 0.75 }}>{field}</Typography>)}</Box>
      </SectionCard>
    </Stack>
  );
}

interface ProfileStepProps {
  values: OnboardingDraftValues;
  criteria: NonNullable<NonNullable<OnboardingDraftValues['policy']>['criteria']>;
  processing: OnboardingDraftValues['processing'] | undefined;
  content: NonNullable<OnboardingDraftValues['processing']>['contentLimits'] | undefined;
  attachments: NonNullable<OnboardingDraftValues['processing']>['attachmentLimits'] | undefined;
  busy: boolean;
  dirty: boolean;
  validation: ApiError | null;
  clearValidation: (key: string) => void;
  mutate: (mutation: (values: OnboardingDraftValues) => void) => void;
  save: (() => void) | null;
}

function ProfileStep({ values, criteria, processing, content, attachments, busy, dirty, validation, clearValidation, mutate, save }: ProfileStepProps) {
  const setNumber = (target: (next: number | null) => void) => (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => target(numeric(event.target.value));
  const ensurePolicy = (draft: OnboardingDraftValues) => { draft.policy ??= { id: '', version: '', criteria: [] }; draft.policy.criteria ??= []; return draft.policy; };
  const ensureIntake = (draft: OnboardingDraftValues) => { draft.intakeState ??= { validatedTag: '', incompleteTag: '' }; return draft.intakeState; };
  const message = (key: string) => validation?.fieldErrors[key]?.[0];
  const invalid = (key: string) => Boolean(message(key));
  const change = (key: string, mutation: (draft: OnboardingDraftValues) => void) => {
    clearValidation(key);
    mutate(mutation);
  };
  const evidenceInvalid = Object.keys(validation?.fieldErrors ?? {}).some((key) =>
    key.startsWith('processing.contentLimits.') || key.startsWith('processing.attachmentLimits.'));
  const pricing = values.aiRuntime?.pricing ?? [];
  const exclusions = values.exclusions ?? [];
  const [evidenceExpanded, setEvidenceExpanded] = useState(false);
  return (
    <Stack spacing={2.5}>
      <SectionCard id="setup-profile" invalid={Object.keys(validation?.fieldErrors ?? {}).some((key) => ['policyUrl', 'intakeState.', 'schedule.'].some((prefix) => key.startsWith(prefix)))} title="Profile basics" description="Define the profile’s policy reference, intake tags, time zone, and initial discovery boundary.">
        <Grid container spacing={2}>
          <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth label="Profile version (optional)" value={values.profileVersion ?? ''} onChange={(event) => mutate((draft) => { draft.profileVersion = event.target.value || null; })} /></Grid>
          <Grid size={{ xs: 12, sm: 8 }}><TextField id="setup-policyUrl" fullWidth required type="url" label="Policy URL" value={values.policyUrl ?? ''} onChange={(event) => change('policyUrl', (draft) => { draft.policyUrl = event.target.value || null; })} error={invalid('policyUrl')} helperText={message('policyUrl') ?? 'A durable HTTP or HTTPS reference for this policy.'} /></Grid>
          <Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-intakeState.validatedTag" fullWidth required label="Engineering Ready tag" value={values.intakeState?.validatedTag ?? ''} onChange={(event) => change('intakeState.validatedTag', (draft) => { ensureIntake(draft).validatedTag = event.target.value || null; })} error={invalid('intakeState.validatedTag')} helperText={message('intakeState.validatedTag')} /></Grid>
          <Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-intakeState.incompleteTag" fullWidth required label="Intake Incomplete tag" value={values.intakeState?.incompleteTag ?? ''} onChange={(event) => change('intakeState.incompleteTag', (draft) => { ensureIntake(draft).incompleteTag = event.target.value || null; })} error={invalid('intakeState.incompleteTag')} helperText={message('intakeState.incompleteTag')} /></Grid>
          <Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-schedule.timezone" fullWidth required label="Timezone" value={values.schedule?.timezone ?? ''} onChange={(event) => change('schedule.timezone', (draft) => { if (draft.schedule) draft.schedule.timezone = event.target.value || null; })} error={invalid('schedule.timezone')} helperText={message('schedule.timezone') ?? 'Use a backend-supported TimeZoneInfo identifier, for example America/Toronto.'} /></Grid>
          <Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-schedule.initialLookback" fullWidth required label="Initial lookback" value={values.schedule?.initialLookback ?? ''} onChange={(event) => change('schedule.initialLookback', (draft) => { if (draft.schedule) draft.schedule.initialLookback = event.target.value || null; })} error={invalid('schedule.initialLookback')} helperText={message('schedule.initialLookback') ?? '.NET duration format, for example 7.00:00:00 for seven days.'} /></Grid>
        </Grid>
      </SectionCard>
      <SectionCard id="setup-policy.criteria" invalid={Boolean(validation?.sectionErrors['policy.criteria']) || Object.keys(validation?.fieldErrors ?? {}).some((key) => key.startsWith('policy.'))} title="Intake policy" description="A criterion describes evidence needed for Engineering to begin investigation. Keep each item bounded and specific.">
        <Grid container spacing={2}><Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-policy.id" fullWidth required label="Policy ID" value={values.policy?.id ?? ''} onChange={(event) => change('policy.id', (draft) => { ensurePolicy(draft).id = event.target.value || null; })} error={invalid('policy.id')} helperText={message('policy.id')} /></Grid><Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-policy.version" fullWidth required label="Policy version" value={values.policy?.version ?? ''} onChange={(event) => change('policy.version', (draft) => { ensurePolicy(draft).version = event.target.value || null; })} error={invalid('policy.version')} helperText={message('policy.version')} /></Grid></Grid>
        {validation?.sectionErrors['policy.criteria']?.[0] ? <FormHelperText error role="alert">{validation.sectionErrors['policy.criteria'][0]}</FormHelperText> : null}
        {criteria.map((criterion, index) => (
          <Paper component="fieldset" variant="outlined" key={index} sx={{ p: 2, m: 0 }}>
            <Stack spacing={2}><Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}><Typography component="legend" variant="h3">Criterion {index + 1}</Typography><Button color="error" startIcon={<DeleteOutlineRounded />} aria-label={`Remove criterion ${index + 1}`} onClick={() => mutate((draft) => { ensurePolicy(draft).criteria?.splice(index, 1); })}>Remove</Button></Stack>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, sm: 5 }}><TextField id={`setup-policy.criteria[${index}].id`} fullWidth required label="Criterion ID" value={criterion.id ?? ''} onChange={(event) => change(`policy.criteria[${index}].id`, (draft) => { const item = ensurePolicy(draft).criteria?.[index]; if (item) item.id = event.target.value || null; })} error={invalid(`policy.criteria[${index}].id`)} helperText={message(`policy.criteria[${index}].id`)} /></Grid>
                <Grid size={{ xs: 12, sm: 7 }}><TextField id={`setup-policy.criteria[${index}].displayName`} fullWidth required label="Display name" value={criterion.displayName ?? ''} onChange={(event) => change(`policy.criteria[${index}].displayName`, (draft) => { const item = ensurePolicy(draft).criteria?.[index]; if (item) item.displayName = event.target.value || null; })} error={invalid(`policy.criteria[${index}].displayName`)} helperText={message(`policy.criteria[${index}].displayName`)} /></Grid>
                <Grid size={12}><TextField id={`setup-policy.criteria[${index}].description`} fullWidth required multiline minRows={2} label="Description" value={criterion.description ?? ''} onChange={(event) => change(`policy.criteria[${index}].description`, (draft) => { const item = ensurePolicy(draft).criteria?.[index]; if (item) item.description = event.target.value || null; })} error={invalid(`policy.criteria[${index}].description`)} helperText={message(`policy.criteria[${index}].description`)} /></Grid>
                <Grid size={12}><TextField id={`setup-policy.criteria[${index}].evaluationGuidance`} fullWidth required multiline minRows={2} label="Evaluation guidance" value={criterion.evaluationGuidance ?? ''} onChange={(event) => change(`policy.criteria[${index}].evaluationGuidance`, (draft) => { const item = ensurePolicy(draft).criteria?.[index]; if (item) item.evaluationGuidance = event.target.value || null; })} error={invalid(`policy.criteria[${index}].evaluationGuidance`)} helperText={message(`policy.criteria[${index}].evaluationGuidance`)} /></Grid>
                <Grid size={{ xs: 12, sm: 5 }}><FormControl fullWidth required><InputLabel id={`applicability-${index}`}>Applicability</InputLabel><Select labelId={`applicability-${index}`} label="Applicability" value={criterion.applicability ?? 'required'} onChange={(event) => mutate((draft) => { const item = ensurePolicy(draft).criteria?.[index]; if (item) item.applicability = event.target.value; })}><MenuItem value="required">Required</MenuItem><MenuItem value="contextual">Contextual</MenuItem></Select></FormControl></Grid>
                <Grid size={{ xs: 12, sm: 7 }}><FormControlLabel control={<Checkbox checked={criterion.na?.allowed ?? false} onChange={(event) => mutate((draft) => { const item = ensurePolicy(draft).criteria?.[index]; if (item) { item.na ??= { allowed: false, requiresExplanation: false }; item.na.allowed = event.target.checked; if (!event.target.checked) item.na.requiresExplanation = false; } })} />} label="Not applicable is allowed" /><FormControlLabel control={<Checkbox checked={criterion.na?.requiresExplanation ?? false} disabled={!criterion.na?.allowed} onChange={(event) => mutate((draft) => { const item = ensurePolicy(draft).criteria?.[index]; if (item) { item.na ??= { allowed: true, requiresExplanation: false }; item.na.requiresExplanation = event.target.checked; } })} />} label="Require an N/A explanation" /></Grid>
              </Grid>
            </Stack>
          </Paper>
        ))}
        <Button id="setup-policy.criteria-action" startIcon={<AddRounded />} variant="outlined" sx={{ alignSelf: 'flex-start' }} onClick={() => { clearValidation('policy.criteria'); mutate((draft) => { ensurePolicy(draft).criteria?.push({ id: '', displayName: '', description: '', applicability: 'required', na: { allowed: false, requiresExplanation: false }, evaluationGuidance: '' }); }); }}>Add criterion</Button>
      </SectionCard>
      <SectionCard id="setup-exclusions" invalid={Boolean(validation?.sectionErrors.exclusions)} title="Exclusions" description="Exclude bounded work-item field values before evaluation. Rules use the supported equals-any operator only.">
        {validation?.sectionErrors.exclusions?.[0] ? <FormHelperText error role="alert">{validation.sectionErrors.exclusions[0]}</FormHelperText> : null}
        {exclusions.map((exclusion, index) => (
          <Paper component="fieldset" variant="outlined" key={index} sx={{ p: 2, m: 0 }}>
            <Stack spacing={2}>
              <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}><Typography component="legend" variant="h3">Exclusion {index + 1}</Typography><Button color="error" startIcon={<DeleteOutlineRounded />} aria-label={`Remove exclusion ${index + 1}`} onClick={() => mutate((draft) => { draft.exclusions?.splice(index, 1); })}>Remove</Button></Stack>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth required label="Exclusion ID" value={exclusion.id ?? ''} onChange={(event) => mutate((draft) => { const item = draft.exclusions?.[index]; if (item) item.id = event.target.value || null; })} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth required label="Work-item field" value={exclusion.field ?? ''} onChange={(event) => mutate((draft) => { const item = draft.exclusions?.[index]; if (item) item.field = event.target.value || null; })} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth disabled label="Operator" value="equalsAny" /></Grid>
                <Grid size={12}><TextField fullWidth required label="Values (one per line)" multiline minRows={2} value={(exclusion.values ?? []).join('\n')} onChange={(event) => mutate((draft) => { const item = draft.exclusions?.[index]; if (item) item.values = event.target.value.split('\n').map((value) => value.trim()).filter(Boolean); })} helperText="A work item is excluded when this field equals any listed value." /></Grid>
              </Grid>
            </Stack>
          </Paper>
        ))}
        <Button startIcon={<AddRounded />} variant="outlined" sx={{ alignSelf: 'flex-start' }} onClick={() => { clearValidation('exclusions'); mutate((draft) => { draft.exclusions ??= []; draft.exclusions.push({ id: '', field: '', operator: 'equalsAny', values: [] }); }); }}>Add exclusion</Button>
      </SectionCard>
      <SectionCard title="Processing" description="The backend validates concurrency, retry, content, and attachment bounds. Execution remains Controlled Dry Run.">
        <Alert severity="info">Execution mode: <strong>Controlled Dry Run</strong>. This wizard cannot enable Production.</Alert>
        <Grid container spacing={2}>
          <Grid size={{ xs: 12, sm: 4 }}><TextField id="setup-processing.concurrency" fullWidth required type="number" slotProps={{ htmlInput: { min: 1 } }} label="Concurrency" value={valueOf(processing?.concurrency)} onChange={(event) => { clearValidation('processing.concurrency'); setNumber((next) => mutate((draft) => { if (draft.processing) draft.processing.concurrency = next; }))(event); }} error={invalid('processing.concurrency')} helperText={message('processing.concurrency')} /></Grid>
          <Grid size={{ xs: 12, sm: 4 }}><TextField id="setup-processing.retries" fullWidth required type="number" slotProps={{ htmlInput: { min: 0 } }} label="Retries" value={valueOf(processing?.retries)} onChange={(event) => { clearValidation('processing.retries'); setNumber((next) => mutate((draft) => { if (draft.processing) draft.processing.retries = next; }))(event); }} error={invalid('processing.retries')} helperText={message('processing.retries')} /></Grid>
          <Grid size={{ xs: 12, sm: 4 }}><TextField id="setup-aiRuntime.timeoutSeconds" fullWidth required type="number" slotProps={{ htmlInput: { min: 1 } }} label="AI timeout (seconds)" value={valueOf(values.aiRuntime?.timeoutSeconds)} onChange={(event) => { clearValidation('aiRuntime.timeoutSeconds'); setNumber((next) => mutate((draft) => { if (draft.aiRuntime) draft.aiRuntime.timeoutSeconds = next; }))(event); }} error={invalid('aiRuntime.timeoutSeconds')} helperText={message('aiRuntime.timeoutSeconds')} /></Grid>
        </Grid>
      </SectionCard>
      <SectionCard id="setup-aiRuntime.pricing" invalid={Boolean(validation?.sectionErrors['aiRuntime.pricing'])} title="AI pricing" description="Optional per-million-token pricing supports bounded cost estimates for the configured models.">
        {validation?.sectionErrors['aiRuntime.pricing']?.[0] ? <FormHelperText error role="alert">{validation.sectionErrors['aiRuntime.pricing'][0]}</FormHelperText> : null}
        {pricing.map((item, index) => (
          <Paper component="fieldset" variant="outlined" key={index} sx={{ p: 2, m: 0 }}>
            <Stack spacing={2}>
              <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}><Typography component="legend" variant="h3">Pricing entry {index + 1}</Typography><Button color="error" startIcon={<DeleteOutlineRounded />} aria-label={`Remove pricing entry ${index + 1}`} onClick={() => mutate((draft) => { draft.aiRuntime?.pricing?.splice(index, 1); })}>Remove</Button></Stack>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth required label="Pricing provider" value={item.provider} onChange={(event) => mutate((draft) => { const entry = draft.aiRuntime?.pricing?.[index]; if (entry) entry.provider = event.target.value; })} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth required label="Pricing model" value={item.model} onChange={(event) => mutate((draft) => { const entry = draft.aiRuntime?.pricing?.[index]; if (entry) entry.model = event.target.value; })} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth required label="Currency" value={item.currency} onChange={(event) => mutate((draft) => { const entry = draft.aiRuntime?.pricing?.[index]; if (entry) entry.currency = event.target.value; })} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth required type="number" label="Input per million tokens" value={valueOf(item.inputPerMillionTokens)} onChange={setNumber((next) => mutate((draft) => { const entry = draft.aiRuntime?.pricing?.[index]; if (entry) entry.inputPerMillionTokens = next ?? 0; }))} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth required type="number" label="Output per million tokens" value={valueOf(item.outputPerMillionTokens)} onChange={setNumber((next) => mutate((draft) => { const entry = draft.aiRuntime?.pricing?.[index]; if (entry) entry.outputPerMillionTokens = next ?? 0; }))} /></Grid>
                <Grid size={{ xs: 12, sm: 4 }}><TextField fullWidth label="Pricing identity (optional)" value={item.identity ?? ''} onChange={(event) => mutate((draft) => { const entry = draft.aiRuntime?.pricing?.[index]; if (entry) entry.identity = event.target.value || null; })} /></Grid>
              </Grid>
            </Stack>
          </Paper>
        ))}
        <Button startIcon={<AddRounded />} variant="outlined" sx={{ alignSelf: 'flex-start' }} onClick={() => { clearValidation('aiRuntime.pricing'); mutate((draft) => { draft.aiRuntime ??= { timeoutSeconds: 60, pricing: [] }; draft.aiRuntime.pricing ??= []; draft.aiRuntime.pricing.push({ provider: 'openai', model: '', inputPerMillionTokens: 0, outputPerMillionTokens: 0, currency: 'USD', identity: null }); }); }}>Add pricing entry</Button>
      </SectionCard>
      <Accordion id="setup-evidence-limits" expanded={evidenceExpanded || evidenceInvalid} onChange={(_, expanded) => setEvidenceExpanded(expanded)} sx={evidenceInvalid ? { outline: '2px solid', outlineColor: 'error.main' } : undefined}><AccordionSummary expandIcon={<ArrowForwardRounded sx={{ transform: 'rotate(90deg)' }} />}><Box><Typography variant="h3">Evidence limits</Typography><Typography variant="body2" color="text.secondary">Required bounded content and attachment processing values{evidenceInvalid ? ' — contains validation errors' : ''}</Typography></Box></AccordionSummary><AccordionDetails><Grid container spacing={2}>
        {[
          ['Maximum total characters', 'maximumTotalCharacters', content?.maximumTotalCharacters], ['Maximum comments', 'maximumComments', content?.maximumComments], ['Maximum extracted text characters', 'maximumExtractedTextCharacters', content?.maximumExtractedTextCharacters],
        ].map(([label, key, value]) => { const fieldKey = `processing.contentLimits.${String(key)}`; return <Grid key={String(key)} size={{ xs: 12, sm: 4 }}><TextField id={`setup-${fieldKey}`} fullWidth required type="number" label={String(label)} value={valueOf(value)} onChange={(event) => { clearValidation(fieldKey); setNumber((next) => mutate((draft) => { const limits = draft.processing?.contentLimits; if (limits) (limits as Record<string, unknown>)[String(key)] = next; }))(event); }} error={invalid(fieldKey)} helperText={message(fieldKey)} /></Grid>; })}
        {[
          ['Maximum attachments', 'maximumCount', attachments?.maximumCount], ['Bytes per attachment', 'maximumBytesPerAttachment', attachments?.maximumBytesPerAttachment], ['Aggregate attachment bytes', 'maximumAggregateBytes', attachments?.maximumAggregateBytes], ['Maximum PDF pages', 'maximumPdfPages', attachments?.maximumPdfPages], ['Maximum images', 'maximumImageCount', attachments?.maximumImageCount], ['Maximum image bytes', 'maximumImageBytes', attachments?.maximumImageBytes], ['Maximum CSV rows', 'maximumCsvRows', attachments?.maximumCsvRows], ['Structured text depth', 'maximumStructuredTextDepth', attachments?.maximumStructuredTextDepth],
        ].map(([label, key, value]) => { const fieldKey = `processing.attachmentLimits.${String(key)}`; return <Grid key={String(key)} size={{ xs: 12, sm: 4 }}><TextField id={`setup-${fieldKey}`} fullWidth required type="number" label={String(label)} value={valueOf(value)} onChange={(event) => { clearValidation(fieldKey); setNumber((next) => mutate((draft) => { const limits = draft.processing?.attachmentLimits; if (limits) (limits as Record<string, unknown>)[String(key)] = next; }))(event); }} error={invalid(fieldKey)} helperText={message(fieldKey)} /></Grid>; })}
      </Grid></AccordionDetails></Accordion>
      <SectionCard title="Audit retention"><TextField id="setup-audit.retentionDays" sx={{ maxWidth: 320 }} required type="number" label="Retention days" value={valueOf(values.audit?.retentionDays)} onChange={(event) => { clearValidation('audit.retentionDays'); setNumber((next) => mutate((draft) => { if (draft.audit) draft.audit.retentionDays = next; }))(event); }} error={invalid('audit.retentionDays')} helperText={message('audit.retentionDays')} /></SectionCard>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>{save ? <Button startIcon={<SaveOutlined />} variant="outlined" disabled={busy || !dirty} onClick={save}>Save draft</Button> : null}<Typography variant="body2" color="text.secondary">{dirty ? 'Unsaved changes' : 'All profile changes saved'}</Typography></Stack>
    </Stack>
  );
}

function QueryPreview({ candidate, confirm, busy }: { candidate: AzureDevOpsQueryCandidate; confirm: () => void; busy: boolean }) {
  return (
    <Stack spacing={2} aria-live="polite">
      <Alert severity="success">{candidate.totalCount === 0 ? 'Query is valid. No work items currently match.' : `${String(candidate.totalCount)} work items match. Showing up to the first 10.`}</Alert>
      {candidate.preview.length ? <TableContainer component={Paper} variant="outlined"><Table size="small" aria-label="Saved query preview"><TableHead><TableRow><TableCell>ID</TableCell><TableCell>Title</TableCell><TableCell>Type</TableCell><TableCell>State</TableCell></TableRow></TableHead><TableBody>{candidate.preview.slice(0, 10).map((item) => <TableRow key={String(item.id)}><TableCell>{item.webUrl ? <Link href={item.webUrl} target="_blank" rel="noreferrer">{String(item.id)}</Link> : String(item.id)}</TableCell><TableCell>{item.title}</TableCell><TableCell>{item.workItemType}</TableCell><TableCell>{item.state}</TableCell></TableRow>)}</TableBody></Table></TableContainer> : null}
      <Button variant="contained" sx={{ alignSelf: 'flex-start' }} disabled={busy} onClick={confirm}>{busy ? 'Confirming…' : 'Confirm query'}</Button>
    </Stack>
  );
}

function ScheduleStep({ values, kind, validation, clearValidation, mutate }: { values: OnboardingDraftValues; kind: ScheduleChoice; validation: ApiError | null; clearValidation: (key: string) => void; mutate: (mutation: (values: OnboardingDraftValues) => void) => void }) {
  const choose = (choice: ScheduleChoice) => {
    if (choice === 'custom') { mutate((draft) => { if (draft.schedule) { draft.schedule.enabled = true; draft.schedule.expression = ''; } }); return; }
    mutate((draft) => { if (draft.schedule) { draft.schedule.enabled = schedulePresets[choice].enabled; draft.schedule.expression = schedulePresets[choice].expression; } });
  };
  return (
    <SectionCard title="Optional schedule" description="Choose when the existing backend scheduler should run. The backend validates Cronos and TimeZoneInfo semantics.">
      <FormControl fullWidth><InputLabel id="schedule-choice-label">Frequency</InputLabel><Select labelId="schedule-choice-label" label="Frequency" value={kind} onChange={(event) => choose(event.target.value)}><MenuItem value="manual">Manual only</MenuItem><MenuItem value="hourly">Hourly</MenuItem><MenuItem value="daily">Daily</MenuItem><MenuItem value="weekly">Weekly</MenuItem><MenuItem value="custom">Custom</MenuItem></Select><FormHelperText>Manual Only creates no automatic schedule.</FormHelperText></FormControl>
      {kind === 'custom' ? <TextField id="setup-schedule.expression" required label="Six-field cron expression" value={values.schedule?.expression ?? ''} onChange={(event) => { clearValidation('schedule.expression'); mutate((draft) => { if (draft.schedule) draft.schedule.expression = event.target.value || null; }); }} error={Boolean(validation?.fieldErrors['schedule.expression'])} helperText={validation?.fieldErrors['schedule.expression']?.[0] ?? 'Validated by the backend; this browser does not calculate run times.'} /> : null}
      <Grid container spacing={2}><Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-schedule.timezone" fullWidth required label="Timezone" value={values.schedule?.timezone ?? ''} onChange={(event) => { clearValidation('schedule.timezone'); mutate((draft) => { if (draft.schedule) draft.schedule.timezone = event.target.value || null; }); }} error={Boolean(validation?.fieldErrors['schedule.timezone'])} helperText={validation?.fieldErrors['schedule.timezone']?.[0]} /></Grid><Grid size={{ xs: 12, sm: 6 }}><TextField id="setup-schedule.initialLookback" fullWidth required label="Initial lookback" value={values.schedule?.initialLookback ?? ''} onChange={(event) => { clearValidation('schedule.initialLookback'); mutate((draft) => { if (draft.schedule) draft.schedule.initialLookback = event.target.value || null; }); }} error={Boolean(validation?.fieldErrors['schedule.initialLookback'])} helperText={validation?.fieldErrors['schedule.initialLookback']?.[0]} /></Grid></Grid>
      <Alert severity="info">{kind === 'manual' ? 'Manual Only: no automatic executions will be scheduled.' : `Schedule expression: ${values.schedule?.expression ?? ''}. The backend applies it in ${values.schedule?.timezone ?? 'the configured timezone'}.`}</Alert>
    </SectionCard>
  );
}

function ReviewStep({ data, edit, finalize, busy, editing, dirty }: { data: WizardData; edit: (index: number) => void; finalize: () => void; busy: boolean; editing: boolean; dirty: boolean }) {
  const values = data.draft.values;
  const selectedProvider = data.aiSettings.provider === 'anthropic' ? 'anthropic' : 'openai';
  const rows = [
    ['Azure DevOps', `${data.adoSettings.organizationUrl ?? 'Not configured'} · ${data.adoSettings.project ?? 'No project'}`, data.setup.azureDevOpsCredentialVerified && data.setup.azureDevOpsSavedQueryConfirmed, 1],
    ['AI provider and model', `${providerLabel(selectedProvider)} · ${data.aiSettings.model ?? 'No model'}`, data.setup.aiCredentialVerified && data.setup.aiModelConfigured, 2],
    ['Profile', `${values?.profileVersion || 'Unversioned'} · ${values?.intakeState?.validatedTag ?? 'No ready tag'} / ${values?.intakeState?.incompleteTag ?? 'No incomplete tag'}`, data.setup.profileDetailsComplete, 4],
    ['Policy', `${values?.policy?.id ?? 'No policy'} · ${String(values?.policy?.criteria?.length ?? 0)} criteria · ${values?.policyUrl ?? 'No URL'}`, data.setup.policyDetailsComplete, 4],
    ['Saved query', data.adoSettings.savedQueryId ?? 'Not confirmed', data.setup.azureDevOpsSavedQueryConfirmed, 5],
    ['Schedule', values?.schedule?.enabled ? `${values.schedule.expression} · ${values.schedule.timezone}` : `Manual Only · ${values?.schedule?.timezone ?? 'No timezone'}`, Boolean(values?.schedule?.timezone && values.schedule.initialLookback), 6],
  ] as const;
  const allReady = data.setup.azureDevOpsCredentialVerified && data.setup.azureDevOpsSavedQueryConfirmed && data.setup.aiCredentialVerified && data.setup.aiModelConfigured && data.setup.profileDetailsComplete && data.setup.policyDetailsComplete && data.draft.revision != null;
  return (
    <Stack spacing={2.5}>
      <SectionCard title={editing ? 'Review profile changes' : 'Review authoritative setup'} description={editing ? 'Review the current persisted integration state and your profile changes before saving. No secret values are returned or displayed.' : 'This summary was refreshed from backend-owned draft, integration, credential metadata, query, and readiness state. No secret values are returned or displayed.'}>
        {rows.map(([label, detail, ready, step]) => <Paper variant="outlined" sx={{ p: 2 }} key={label}><Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' } }}><Box><Typography variant="h3">{label}</Typography><Typography color="text.secondary">{detail}</Typography></Box><Stack direction="row" spacing={1}><Chip label={ready ? 'Ready' : 'Incomplete'} color={ready ? 'success' : 'warning'} variant="outlined" /><Button size="small" startIcon={<EditOutlined />} onClick={() => edit(step)}>Edit</Button></Stack></Stack></Paper>)}
        <Alert severity="success"><AlertTitle>Controlled Dry Run</AlertTitle>No Azure DevOps modifications will be made. Production activation is unavailable.</Alert>
      </SectionCard>
      <SectionCard title={editing ? 'Save configuration' : 'Finish setup'} description={editing ? 'The profile revision is checked for conflicts, the complete candidate is validated, and a new immutable runtime generation is activated for future runs.' : 'Finalization sends only the expected onboarding draft revision. The backend reconstructs, validates, creates, and activates the singleton configuration.'}>
        <StatusLine label={editing ? 'Configuration' : 'Setup inputs'} ready={allReady} detail={allReady ? (dirty ? 'Unsaved profile changes are ready to save.' : 'All required backend checks are current.') : 'Return to incomplete steps before finalizing.'} />
        <Button variant="contained" size="large" disabled={busy || !allReady} onClick={finalize}>{busy ? 'Activating…' : editing ? (dirty ? 'Save and activate changes' : 'Finish review') : 'Finish setup and activate Dry Run'}</Button>
      </SectionCard>
    </Stack>
  );
}
