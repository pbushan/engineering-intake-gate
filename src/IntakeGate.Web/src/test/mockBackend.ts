import { vi } from 'vitest';
import type {
  AiProvider,
  AiModelPricing,
  AiSettings,
  AzureDevOpsSettings,
  CredentialMetadata,
  ControlPlaneAuditPage,
  HomeSummary,
  HomeWindowDays,
  OnboardingDefaults,
  OnboardingDraftState,
  ProfileState,
  PortableProfileDocument,
  RunDetail,
  RunHistoryPage,
  RunItemDetail,
  RunSummary,
  SafeUser,
  SetupStatus,
  SystemHealth,
  VersionInfo,
} from '../api/contracts';

export const adminUser: SafeUser = {
  id: '11111111-1111-1111-1111-111111111111',
  username: 'admin',
  displayName: 'Ada Admin',
  role: 'admin',
  createdAtUtc: '2026-09-12T12:00:00Z',
};

export const viewerUser: SafeUser = {
  ...adminUser,
  id: '22222222-2222-2222-2222-222222222222',
  username: 'viewer',
  displayName: 'Vera Viewer',
  role: 'viewer',
};

export const completeSetup: SetupStatus = {
  infrastructureReady: true,
  adminConfigured: true,
  profileConfigured: true,
  onboardingDraftExists: false,
  profileDetailsComplete: true,
  policyDetailsComplete: true,
  azureDevOpsCredentialConfigured: true,
  azureDevOpsCredentialVerified: true,
  azureDevOpsSavedQueryConfirmed: true,
  aiCredentialConfigured: true,
  aiCredentialVerified: true,
  aiModelConfigured: true,
  requiredAiCredentialSlot: 'openAiApiKey',
  runtimeActivationCurrent: true,
  setupComplete: true,
  lastVisitedStep: null,
  progressUpdatedAtUtc: null,
};

export const incompleteSetup: SetupStatus = {
  ...completeSetup,
  profileConfigured: false,
  onboardingDraftExists: false,
  profileDetailsComplete: false,
  policyDetailsComplete: false,
  azureDevOpsCredentialConfigured: false,
  azureDevOpsCredentialVerified: false,
  azureDevOpsSavedQueryConfirmed: false,
  aiCredentialConfigured: false,
  aiCredentialVerified: false,
  aiModelConfigured: false,
  requiredAiCredentialSlot: null,
  runtimeActivationCurrent: false,
  setupComplete: false,
};

export const profileState: ProfileState = {
  exists: true,
  configurationRevision: 'sha256:deterministic',
  profileId: 'example',
  profileVersion: '1.0',
  policy: null,
  azureDevOps: null,
  ai: null,
  intakeState: null,
  schedule: null,
  processing: {
    executionMode: 'DRY_RUN',
    concurrency: 1,
    retries: 1,
    contentLimits: {
      maximumTotalCharacters: 100000,
      maximumComments: 100,
      maximumExtractedTextCharacters: 75000,
    },
    attachmentLimits: {
      maximumCount: 20,
      maximumBytesPerAttachment: 10485760,
      maximumAggregateBytes: 52428800,
      maximumPdfPages: 200,
      maximumImageCount: 10,
      maximumImageBytes: 5242880,
      maximumCsvRows: 1000,
      maximumStructuredTextDepth: 32,
    },
  },
  audit: null,
  exclusions: null,
  activation: {
    runtimeActivationCurrent: true,
    restartRequired: false,
    aiRuntimeActivationPending: false,
    message: 'The persisted configuration is active for future executions.',
    generationId: 1,
    status: 'active',
  },
};

const version: VersionInfo = { application: 'Engineering Intake Gate', version: '2026.9.4', environment: 'Test' };

export const runSummary: RunSummary = {
  runId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  invocationType: 'manualIncremental',
  triggeredBy: { type: 'user', userId: adminUser.id, username: adminUser.username },
  startedAtUtc: '2026-09-12T14:00:00Z', completedAtUtc: '2026-09-12T14:00:02Z', durationMilliseconds: 2000,
  effectiveMode: 'dryRun', status: 'completed', profileId: 'example', configurationGenerationId: 7,
  ticketsDiscovered: 1, ticketsEvaluated: 1, engineeringReadyCount: 1, intakeIncompleteCount: 0,
  errorCount: 0, notEligibleCount: 0, skippedCount: 0, duplicateUpdatesSuppressedCount: 0,
  azureDevOpsMutationCount: 0,
  tokenUsage: { inputTokens: 1250, outputTokens: 250, totalTokens: 1500 },
  estimatedCost: { amount: 0.0042, currency: 'USD', pricingIdentity: 'test-price', complete: true, pricedInteractions: 1, totalInteractions: 1 }, errors: [],
};

export const runItemDetail: RunItemDetail = {
  evaluationId: 'evaluation-101', runId: runSummary.runId, parentRunId: runSummary.runId,
  workItemId: '101', evaluatedRevision: '7', azureDevOpsUrl: 'https://ado.example.invalid/org/project/_workitems/edit/101',
  decision: 'pass', decisionLabel: 'Engineering Ready', eligibility: 'eligible', eligibilityReason: null,
  processingStatus: 'completed', effectiveMode: 'dryRun', configurationGenerationId: 7,
  selectionReason: 'SavedQueryMembership', policyVersion: '1.0', policyFingerprint: 'sha256:policy',
  promptVersion: 'intake-evaluator-v1', provider: 'fake', model: 'fake-model',
  applicableCriteria: ['reproduction', 'environment'], satisfiedCriteria: ['reproduction'],
  missingCriteria: [{ criterionId: 'environment', reason: 'Environment details are missing.', requiredSupportAction: 'Provide the affected environment.' }],
  ambiguities: [{ criterionId: 'reproduction', description: 'One step is unclear.', requiredClarification: 'Clarify the final step.' }],
  engineeringSummary: 'Enough bounded context is present to begin investigation.',
  proposedEffects: [{ type: 'addTag', target: 'intakeTags', tag: 'Engineering Ready' }, { type: 'postComment', target: 'validatorComment', tag: null }],
  mutationAttempts: [], actualEffects: [], updateSuppressed: false, suppressionReason: null, materiallyChanged: true,
  tokenUsage: runSummary.tokenUsage, estimatedCost: runSummary.estimatedCost, errors: [],
};

export const runDetail: RunDetail = {
  summary: runSummary, profileVersion: '1.0', savedQueryId: '11111111-1111-1111-1111-111111111111',
  savedQueryFingerprint: 'sha256:query', policyVersion: '1.0', policyFingerprint: 'sha256:policy',
  configurationFingerprint: 'sha256:configuration',
  items: [{ evaluationId: runItemDetail.evaluationId, workItemId: runItemDetail.workItemId,
    azureDevOpsUrl: runItemDetail.azureDevOpsUrl, decision: runItemDetail.decision,
    decisionLabel: runItemDetail.decisionLabel, eligibility: runItemDetail.eligibility,
    processingStatus: runItemDetail.processingStatus, updateSuppressed: runItemDetail.updateSuppressed,
    configurationGenerationId: runItemDetail.configurationGenerationId, tokenUsage: runItemDetail.tokenUsage,
    estimatedCost: runItemDetail.estimatedCost }],
};

export const runPage: RunHistoryPage = { page: 1, pageSize: 20, totalCount: 1, totalPages: 1, items: [runSummary] };

export const homeSummary: HomeSummary = {
  windowDays: 30,
  generatedAtUtc: '2026-09-13T12:00:00Z',
  windowStartInclusiveUtc: '2026-08-14T12:00:00Z',
  windowEndExclusiveUtc: '2026-09-13T12:00:00Z',
  evaluatedCount: 5,
  engineeringReadyCount: 3,
  intakeIncompleteCount: 1,
  errorCount: 1,
  notEligibleCount: 2,
  skippedCount: 1,
  duplicateUpdatesSuppressedCount: 2,
  engineeringReadyRate: { numerator: 3, denominator: 4, percentage: 75 },
  estimatedAiCost: { amount: 0.0123, currency: 'USD', evaluationsWithEstimate: 5, evaluationsWithCompleteEstimate: 5, evaluationsWithPartialEstimate: 0, evaluationsWithoutEstimate: 0, complete: true },
  recentRuns: [runSummary],
  healthWarnings: [{ code: 'RecentRunFailure:test', title: 'Recent run needs attention', message: 'A technical/system failure was persisted.', detailUrl: `/runs/${runSummary.runId}` }],
};

export const systemHealth: SystemHealth = {
  generatedAtUtc: '2026-09-12T14:30:00Z',
  application: { status: 'healthy', application: 'Engineering Intake Gate', version: '2026.9.4', environment: 'Test' },
  database: { status: 'healthy', reachable: true, currentSchemaVersion: 15, migrationCurrent: true },
  setup: { status: 'ready', complete: true, profileConfigured: true, savedQueryConfirmed: true },
  runtime: { status: 'active', activeGenerationId: 7, activationCurrent: true },
  azureDevOps: { status: 'verified', credentialConfigured: true, verificationStatus: 'verified', lastVerifiedAtUtc: '2026-09-12T14:10:00Z', verificationDiagnostic: null, settingsReady: true, provider: null, model: null },
  ai: { status: 'verified', credentialConfigured: true, verificationStatus: 'verified', lastVerifiedAtUtc: '2026-09-12T14:12:00Z', verificationDiagnostic: null, settingsReady: true, provider: 'openai', model: 'test-model' },
  scheduler: { status: 'manualOnly', scheduled: false, timezone: 'UTC', nextOccurrenceUtc: null, activeGenerationId: 7 },
  recentFailures: [{ runId: runSummary.runId, occurredAtUtc: '2026-09-12T13:00:00Z', status: 'error', category: 'ProviderUnavailable', message: 'The evaluation provider could not complete the request.' }],
  aiDiagnostics: { credentialRevisionAtUtc: '2026-09-12T14:12:00Z', activeRuntimeProvider: 'openai', activeRuntimeModel: 'test-model' },
};

export const auditPage: ControlPlaneAuditPage = {
  page: 1, pageSize: 20, totalCount: 2, totalPages: 1,
  items: [
    { id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', occurredAtUtc: '2026-09-12T14:20:00Z', actor: { type: 'user', userId: adminUser.id, displayName: 'admin' }, operation: 'AzureDevOpsConnectionVerificationSucceeded', targetCategory: 'AzureDevOps', targetId: 'connection', changedFields: ['verificationStatus'] },
    { id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', occurredAtUtc: '2026-09-12T14:00:00Z', actor: { type: 'system', userId: null, displayName: 'System' }, operation: 'RuntimeConfigurationGenerationActivated', targetCategory: 'RuntimeConfigurationGeneration', targetId: '7', changedFields: ['activeGenerationId'] },
  ],
};

export const onboardingDefaults: OnboardingDefaults = {
  values: {
    profileVersion: null,
    policyUrl: null,
    intakeState: null,
    aiRuntime: { timeoutSeconds: 90, pricing: [] },
    schedule: { enabled: false, expression: '', timezone: null, initialLookback: null },
    processing: {
      executionMode: 'DRY_RUN', concurrency: null, retries: null,
      contentLimits: { maximumTotalCharacters: null, maximumComments: null, maximumExtractedTextCharacters: null },
      attachmentLimits: { maximumCount: null, maximumBytesPerAttachment: null, maximumAggregateBytes: null, maximumPdfPages: null, maximumImageCount: 10, maximumImageBytes: 5242880, maximumCsvRows: 1000, maximumStructuredTextDepth: 32 },
    },
    audit: { retentionDays: null }, exclusions: [], policy: null,
  },
  serverSeededFields: ['aiRuntime.timeoutSeconds', 'processing.executionMode', 'schedule.enabled'],
  requiredAdminFields: ['policyUrl', 'schedule.timezone', 'policy.criteria[].id'],
};

export const completeDraft: OnboardingDraftState = {
  exists: true,
  revision: 3,
  values: {
    ...onboardingDefaults.values,
    profileVersion: '1.0',
    policyUrl: 'https://example.test/intake-policy',
    intakeState: { validatedTag: 'Engineering Ready', incompleteTag: 'Intake Incomplete' },
    schedule: { enabled: false, expression: '', timezone: 'America/Toronto', initialLookback: '7.00:00:00' },
    processing: {
      executionMode: 'DRY_RUN', concurrency: 2, retries: 1,
      contentLimits: { maximumTotalCharacters: 100000, maximumComments: 100, maximumExtractedTextCharacters: 75000 },
      attachmentLimits: { maximumCount: 20, maximumBytesPerAttachment: 10485760, maximumAggregateBytes: 52428800, maximumPdfPages: 200, maximumImageCount: 10, maximumImageBytes: 5242880, maximumCsvRows: 1000, maximumStructuredTextDepth: 32 },
    },
    audit: { retentionDays: 90 },
    policy: { id: 'intake-policy', version: '1.0', criteria: [{ id: 'reproduction', displayName: 'Reproduction', description: 'Steps and observed behavior', applicability: 'required', na: { allowed: false, requiresExplanation: false }, evaluationGuidance: 'Look for bounded reproduction evidence.' }] },
  },
  createdAtUtc: '2026-09-12T12:00:00Z',
  updatedAtUtc: '2026-09-12T12:30:00Z',
  fieldErrors: {},
  sectionErrors: {},
};

export const editableProfileState: ProfileState = {
  ...profileState,
  policy: {
    id: completeDraft.values!.policy!.id!, version: completeDraft.values!.policy!.version!,
    fingerprint: 'sha256:policy', url: completeDraft.values!.policyUrl!,
    criteria: completeDraft.values!.policy!.criteria!.map((item) => ({
      id: item.id!, displayName: item.displayName!, description: item.description!,
      applicability: item.applicability!,
      na: { allowed: item.na!.allowed!, requiresExplanation: item.na!.requiresExplanation! },
      evaluationGuidance: item.evaluationGuidance!,
    })),
  },
  azureDevOps: {
    organizationUrl: 'https://dev.azure.example/contoso', project: 'Engineering',
    savedQueryId: '11111111-1111-1111-1111-111111111111',
  },
  ai: { provider: 'openai', model: 'test-model', timeoutSeconds: 90, pricing: [{ provider: 'openai', model: 'test-model', inputPerMillionTokens: 2.5, outputPerMillionTokens: 10, currency: 'USD', identity: '2026-test-price' }] },
  intakeState: { validatedTag: 'Engineering Ready', incompleteTag: 'Intake Incomplete' },
  schedule: { enabled: false, expression: '', timezone: 'America/Toronto', initialLookback: '7.00:00:00', activationPending: false },
  processing: profileState.processing,
  audit: { retentionDays: 90 },
  exclusions: [{ id: 'excluded-state', field: 'System.State', operator: 'equalsAny', values: ['Removed', 'Closed'] }],
};

const draftValidation = (values: OnboardingDraftState['values']) => {
  const fieldErrors: Record<string, string[]> = {};
  const sectionErrors: Record<string, string[]> = {};
  if (!values?.policyUrl) fieldErrors.policyUrl = ['Policy URL is required.'];
  if (!values?.intakeState?.validatedTag) fieldErrors['intakeState.validatedTag'] = ['Engineering Ready tag is required.'];
  if (!values?.intakeState?.incompleteTag) fieldErrors['intakeState.incompleteTag'] = ['Intake Incomplete tag is required.'];
  if (!values?.schedule?.timezone) fieldErrors['schedule.timezone'] = ['Timezone is required.'];
  if (!values?.processing?.contentLimits?.maximumTotalCharacters)
    fieldErrors['processing.contentLimits.maximumTotalCharacters'] = ['Maximum total characters is required.'];
  if (!values?.policy?.criteria?.length) sectionErrors['policy.criteria'] = ['Add at least one intake criterion.'];
  return { fieldErrors, sectionErrors };
};

const credential = (configured = false, verified = false): CredentialMetadata => ({
  configured,
  sourceKind: configured ? 'locallyEncrypted' : null,
  createdAtUtc: configured ? '2026-09-12T12:00:00Z' : null,
  updatedAtUtc: configured ? '2026-09-12T12:00:00Z' : null,
  verificationStatus: verified ? 'verified' : 'neverVerified',
  lastVerifiedAtUtc: verified ? '2026-09-12T12:05:00Z' : null,
  verificationDiagnostic: null,
});

const adoSettings = (ready = false): AzureDevOpsSettings => ({
  profileConfigured: false,
  organizationUrl: ready ? 'https://dev.azure.example/contoso' : null,
  project: ready ? 'Engineering' : null,
  savedQueryId: ready ? '11111111-1111-1111-1111-111111111111' : null,
  configurationGeneration: 0,
  configurationFingerprint: ready ? 'sha256:query' : null,
  queryConfirmed: ready,
  queryValidatedAtUtc: ready ? '2026-09-12T12:10:00Z' : null,
  restartRequired: false,
});

const aiSettings = (ready = false): AiSettings => ({
  profileConfigured: false,
  provider: ready ? 'openai' : null,
  model: ready ? 'test-model' : null,
  modelConfirmed: ready,
  ready,
  modelValidatedAtUtc: ready ? '2026-09-12T12:15:00Z' : null,
  restartRequired: false,
  activationMessage: 'Staged for singleton profile creation.',
});

export const verifiedCredential = credential(true, true);
export const readyAzureDevOpsSettings = adoSettings(true);
export const readyAiSettings = aiSettings(true);
export const readyModelPricing: AiModelPricing = {
  provider: 'openai',
  model: 'test-model',
  available: true,
  stale: false,
  refreshFailed: false,
  inputPerMillionTokens: 4,
  cachedInputPerMillionTokens: 0.4,
  outputPerMillionTokens: 20,
  currency: 'USD',
  source: 'Official test catalog',
  sourceUri: 'https://example.test/pricing',
  catalogVersion: 'test-v1',
  effectiveAtUtc: '2026-09-12T00:00:00Z',
  lastVerifiedAtUtc: '2026-09-12T12:15:00Z',
  expiresAtUtc: '2026-09-19T12:15:00Z',
  sourceKind: 'bundledCatalog',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

export interface MockBackendOptions {
  user?: SafeUser | null;
  bootstrapAvailable?: boolean;
  setup?: SetupStatus;
  profile?: ProfileState;
  loginSucceeds?: boolean;
  bootstrapConflicts?: boolean;
  unavailable?: boolean;
  defaults?: OnboardingDefaults;
  draft?: OnboardingDraftState;
  adoCredential?: CredentialMetadata;
  adoSettings?: AzureDevOpsSettings;
  aiSettings?: AiSettings;
  aiCredentials?: Partial<Record<AiProvider, CredentialMetadata>>;
  modelPricing?: AiModelPricing;
  pricingEndpointFails?: boolean;
  pricingRefreshFails?: boolean;
  adoVerificationFails?: boolean;
  adoSettingsValidationFails?: boolean;
  aiVerificationFails?: boolean;
  discoveryUnavailable?: boolean;
  staleModelCandidate?: boolean;
  queryValidationFails?: boolean;
  queryInputValidationFails?: boolean;
  aiModelValidationFails?: boolean;
  staleQueryCandidate?: boolean;
  queryTotalCount?: number;
  queryPreviewCount?: number;
  draftValidationFails?: boolean;
  finalizeConflict?: boolean;
  finalizeValidationFails?: boolean;
  finalizeActivationFails?: boolean;
  profileUpdateConflict?: boolean;
  profileUpdateValidationFails?: boolean;
  runPage?: RunHistoryPage;
  runDetail?: RunDetail;
  runItemDetail?: RunItemDetail;
  analyzeDecision?: 'pass' | 'fail' | 'error' | 'notEligible';
  analyzeValidationFails?: boolean;
  runConflict?: boolean;
  health?: SystemHealth;
  homeSummary?: HomeSummary;
  homeSummaries?: Partial<Record<HomeWindowDays, HomeSummary>>;
  homeSummaryFails?: boolean;
  homeSummaryDelayMs?: number;
  auditPage?: ControlPlaneAuditPage;
  operationDelayMs?: number;
}

export function installMockBackend(options: MockBackendOptions = {}) {
  const state = {
    user: options.user ?? null,
    bootstrapAvailable: options.bootstrapAvailable ?? false,
    setup: options.setup ?? completeSetup,
    profile: options.profile ?? editableProfileState,
    loginSucceeds: options.loginSucceeds ?? true,
    bootstrapConflicts: options.bootstrapConflicts ?? false,
    unavailable: options.unavailable ?? false,
    rejectNextCsrf: false,
    defaults: options.defaults ?? onboardingDefaults,
    draft: options.draft ?? (options.setup?.onboardingDraftExists ? completeDraft : { exists: false, revision: null, values: null, createdAtUtc: null, updatedAtUtc: null, fieldErrors: null, sectionErrors: null }),
    adoCredential: options.adoCredential ?? credential(options.setup?.azureDevOpsCredentialConfigured, options.setup?.azureDevOpsCredentialVerified),
    adoSettings: options.adoSettings ?? adoSettings(options.setup?.azureDevOpsSavedQueryConfirmed),
    aiSettings: options.aiSettings ?? aiSettings(options.setup?.aiModelConfigured),
    modelPricing: structuredClone(options.modelPricing ?? readyModelPricing),
    aiCredentials: {
      openai: options.aiCredentials?.openai ?? credential(options.setup?.aiCredentialConfigured, options.setup?.aiCredentialVerified),
      anthropic: options.aiCredentials?.anthropic ?? credential(false, false),
    } as Record<AiProvider, CredentialMetadata>,
    health: structuredClone(options.health ?? systemHealth),
    homeSummary: structuredClone(options.homeSummary ?? homeSummary),
    pendingQueryId: null as string | null,
    pendingModel: null as { provider: AiProvider; model: string } | null,
  };
  const calls: Array<{ path: string; method: string; headers: Headers; body: string | null }> = [];

  const fetchMock = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    if (state.unavailable) throw new TypeError('network unavailable');
    const requestPath = typeof input === 'string' ? input : input instanceof URL ? `${input.pathname}${input.search}` : new URL(input.url).pathname;
    const path = requestPath.split('?')[0] ?? requestPath;
    const method = (init.method ?? 'GET').toUpperCase();
    const headers = new Headers(init.headers);
    calls.push({ path: requestPath, method, headers, body: typeof init.body === 'string' ? init.body : null });

    if (path === '/api/auth/csrf') return json({ token: `csrf-${calls.length}` });
    if (path === '/api/auth/session') return state.user ? json(state.user) : json({ error: 'Unauthenticated', message: 'Authentication is required.' }, 401);
    if (path === '/api/auth/bootstrap/status') return json({ available: state.bootstrapAvailable });
    if (path === '/api/auth/login' && method === 'POST') {
      if (!state.loginSucceeds) return json({ error: 'InvalidCredentials', message: 'account-specific unsafe detail' }, 401);
      state.user = options.user ?? adminUser;
      return json(state.user);
    }
    if (path === '/api/auth/bootstrap' && method === 'POST') {
      if (state.bootstrapConflicts) {
        state.bootstrapAvailable = false;
        return json({ error: 'BootstrapUnavailable', message: 'Bootstrap is closed.' }, 409);
      }
      if (!state.bootstrapAvailable) return json({ error: 'BootstrapUnavailable', message: 'Bootstrap is closed.' }, 409);
      state.bootstrapAvailable = false;
      state.user = adminUser;
      return json(state.user, 201);
    }
    if (path === '/api/auth/logout' && method === 'POST') {
      if (state.rejectNextCsrf) {
        state.rejectNextCsrf = false;
        return json({ error: 'InvalidAntiforgeryToken', message: 'Token invalid.' }, 400);
      }
      state.user = null;
      return new Response(null, { status: 204 });
    }
    if (!state.user) return json({ error: 'Unauthenticated', message: 'Authentication is required.' }, 401);
    if (path === '/api/setup/status') return json(state.setup);
    if (path === '/api/home/summary' && method === 'GET') {
      if (options.homeSummaryDelayMs) await new Promise((resolve) => setTimeout(resolve, options.homeSummaryDelayMs));
      if (options.homeSummaryFails) return json({ error: 'HomeSummaryUnavailable', message: 'The operational summary is temporarily unavailable.' }, 503);
      const requested = Number(new URL(requestPath, 'http://local.test').searchParams.get('windowDays') ?? 30) as HomeWindowDays;
      const selected = options.homeSummaries?.[requested] ?? state.homeSummary;
      return json({ ...selected, windowDays: requested });
    }
    if (path === '/api/support/health' && method === 'GET') return json(state.health);
    if (path === '/api/audit' && method === 'GET') return json(options.auditPage ?? auditPage);
    if (path === '/api/setup/defaults') return json(state.defaults);
    if (path === '/api/setup/profile-draft' && method === 'GET') return json(state.draft);
    if (path === '/api/setup/profile-draft/initialize' && method === 'POST') {
      state.draft = { exists: true, revision: 1, values: structuredClone(state.defaults.values), createdAtUtc: '2026-09-12T13:00:00Z', updatedAtUtc: '2026-09-12T13:00:00Z', fieldErrors: {}, sectionErrors: {} };
      state.setup = { ...state.setup, onboardingDraftExists: true };
      return json(state.draft);
    }
    if (path === '/api/setup/profile-draft' && method === 'PUT') {
      const body = JSON.parse(String(init.body)) as { expectedRevision: number; values: OnboardingDraftState['values'] };
      if (Number(body.expectedRevision) !== Number(state.draft.revision)) return json({ error: 'OnboardingDraftConflict', message: 'Setup changed; reload it.' }, 409);
      if (options.draftValidationFails) return json({ error: 'ValidationFailed', message: 'The schedule expression or timezone is invalid.', fieldErrors: { 'schedule.timezone': ['Select a backend-supported timezone.'] } }, 400);
      const validation = draftValidation(body.values);
      state.draft = { ...state.draft, revision: Number(state.draft.revision) + 1, values: structuredClone(body.values), updatedAtUtc: '2026-09-12T13:05:00Z', ...validation };
      const complete = Object.keys(validation.fieldErrors).length === 0 && Object.keys(validation.sectionErrors).length === 0;
      state.setup = { ...state.setup, onboardingDraftExists: true, profileDetailsComplete: complete, policyDetailsComplete: complete };
      return json(state.draft);
    }
    if (path === '/api/setup/progress' && method === 'PUT') {
      const body = JSON.parse(String(init.body)) as { step: string };
      state.setup = { ...state.setup, lastVisitedStep: body.step, progressUpdatedAtUtc: '2026-09-12T13:10:00Z' };
      return json({ lastVisitedStep: body.step, updatedAtUtc: state.setup.progressUpdatedAtUtc });
    }
    if (path === '/api/setup/finalize' && method === 'POST') {
      const body = JSON.parse(String(init.body)) as Record<string, unknown>;
      if (Object.keys(body).join(',') !== 'expectedDraftRevision') return json({ error: 'UnexpectedFields' }, 400);
      if (options.finalizeConflict) return json({ error: 'OnboardingDraftConflict' }, 409);
      if (options.finalizeValidationFails) return json({ error: 'InvalidOnboardingDraft' }, 400);
      if (options.finalizeActivationFails) return json({ error: 'ConfigurationActivationFailed', message: 'The configuration could not be activated safely.' }, 503);
      state.setup = { ...completeSetup };
      state.profile = { ...profileState };
      state.draft = { exists: false, revision: null, values: null, createdAtUtc: null, updatedAtUtc: null, fieldErrors: null, sectionErrors: null };
      return json(state.profile, 201);
    }
    if (path === '/api/profile' && method === 'PUT') {
      const body = JSON.parse(String(init.body)) as { expectedRevision: string; profile: NonNullable<OnboardingDraftState['values']> };
      if (options.profileUpdateConflict || body.expectedRevision !== state.profile.configurationRevision)
        return json({ error: 'ProfileConflict', message: 'The profile changed; refresh and retry.' }, 409);
      if (options.profileUpdateValidationFails)
        return json({ error: 'ValidationFailed', message: 'Some profile fields need attention.', fieldErrors: { 'schedule.timezone': ['Select a backend-supported timezone.'] } }, 400);
      const values = body.profile;
      state.profile = {
        ...state.profile,
        configurationRevision: 'sha256:updated',
        profileVersion: values.profileVersion,
        policy: values.policy && values.policyUrl ? {
          id: values.policy.id!, version: values.policy.version!, fingerprint: 'sha256:updated-policy', url: values.policyUrl,
          criteria: values.policy.criteria!.map((item) => ({ id: item.id!, displayName: item.displayName!, description: item.description!, applicability: item.applicability!, na: { allowed: item.na!.allowed!, requiresExplanation: item.na!.requiresExplanation! }, evaluationGuidance: item.evaluationGuidance! })),
        } : null,
        ai: state.profile.ai ? { ...state.profile.ai, timeoutSeconds: values.aiRuntime!.timeoutSeconds!, pricing: values.aiRuntime!.pricing! } : null,
        intakeState: values.intakeState as ProfileState['intakeState'], schedule: values.schedule ? { ...values.schedule, enabled: values.schedule.enabled, expression: values.schedule.expression!, timezone: values.schedule.timezone!, initialLookback: values.schedule.initialLookback!, activationPending: false } : null,
        processing: values.processing as ProfileState['processing'], audit: values.audit as ProfileState['audit'], exclusions: values.exclusions as ProfileState['exclusions'],
      };
      return json(state.profile);
    }
    if (path === '/api/profile/export' && method === 'GET') {
      const values = state.draft.exists && state.draft.values ? state.draft.values : completeDraft.values!;
      const { policy, ...profile } = values;
      return json({
        format: 'engineering-intake-gate-profile', version: 1,
        exportedAt: '2026-09-18T12:00:00Z', profile, policy,
      });
    }
    if (path === '/api/profile/import/validate' && method === 'POST') {
      const document = JSON.parse(String(init.body)) as PortableProfileDocument;
      if (document?.format !== 'engineering-intake-gate-profile')
        return json({ error: 'WrongProfileFormat', message: 'This file is not an Engineering Intake Gate profile.' }, 400);
      if (document?.version > 1)
        return json({ error: 'NewerProfileVersion', message: 'This profile was created by a newer version of Engineering Intake Gate and cannot be imported by this version.' }, 400);
      if (document?.version !== 1 || !document?.profile || !document?.policy)
        return json({ error: 'InvalidProfileFile', message: 'The selected file is not a valid Engineering Intake Gate profile.' }, 400);
      const complete = Boolean(document.profile.policyUrl && document.policy.id && document.policy.criteria?.length);
      return json({ document, complete,
        fieldErrors: complete ? {} : { policyUrl: ['Policy URL is required.'] },
        sectionErrors: complete ? {} : { 'policy.criteria': ['Add at least one intake criterion.'] } });
    }
    if (path === '/api/profile/import' && method === 'POST') {
      const document = JSON.parse(String(init.body)) as PortableProfileDocument;
      const values: NonNullable<OnboardingDraftState['values']> = { ...document.profile, policy: document.policy };
      const complete = Boolean(values.policyUrl && values.policy?.id && values.policy?.criteria?.length);
      if (complete && state.profile.exists) {
        state.profile = { ...state.profile, configurationRevision: 'sha256:imported',
          profileVersion: values.profileVersion, audit: values.audit as ProfileState['audit'],
          processing: values.processing as ProfileState['processing'],
          intakeState: values.intakeState as ProfileState['intakeState'],
          exclusions: values.exclusions as ProfileState['exclusions'],
          schedule: values.schedule ? { enabled: values.schedule.enabled, expression: values.schedule.expression!,
            timezone: values.schedule.timezone!, initialLookback: values.schedule.initialLookback!, activationPending: false } : null,
          ai: state.profile.ai ? { ...state.profile.ai, timeoutSeconds: values.aiRuntime!.timeoutSeconds!, pricing: values.aiRuntime!.pricing! } : null,
          policy: { id: values.policy!.id!, version: values.policy!.version!, criteria: values.policy!.criteria!.map((item) => ({
            id: item.id!, displayName: item.displayName!, description: item.description!, applicability: item.applicability!,
            na: { allowed: item.na!.allowed!, requiresExplanation: item.na!.requiresExplanation! }, evaluationGuidance: item.evaluationGuidance!,
          })), url: values.policyUrl!, fingerprint: 'sha256:imported-policy' } };
        state.draft = { exists: false, revision: null, values: null, createdAtUtc: null, updatedAtUtc: null, fieldErrors: null, sectionErrors: null };
        return json({ profileReplaced: true, profile: state.profile, draft: null });
      }
      const validation = draftValidation(values);
      state.draft = { exists: true, revision: Number(state.draft.revision ?? 0) + 1, values,
        createdAtUtc: '2026-09-18T12:00:00Z', updatedAtUtc: '2026-09-18T12:00:00Z', ...validation };
      return json({ profileReplaced: false, profile: null, draft: state.draft });
    }
    if (path === '/api/ado/credential' && method === 'GET') return json(state.adoCredential);
    if (path === '/api/ado/credential/local' && method === 'PUT') {
      state.adoCredential = credential(true, false); state.setup = { ...state.setup, azureDevOpsCredentialConfigured: true, azureDevOpsCredentialVerified: false }; return json(state.adoCredential);
    }
    if (path === '/api/ado/credential/environment' && method === 'PUT') {
      state.adoCredential = { ...credential(true, false), sourceKind: 'environmentReference' }; state.setup = { ...state.setup, azureDevOpsCredentialConfigured: true, azureDevOpsCredentialVerified: false }; return json(state.adoCredential);
    }
    if (path === '/api/ado/settings' && method === 'GET') return json(state.adoSettings);
    if (path === '/api/ado/settings' && method === 'PUT') {
      const body = JSON.parse(String(init.body)) as { organizationUrl: string; project: string };
      if (options.adoSettingsValidationFails) return json({ error: 'ValidationFailed', message: 'The Azure DevOps settings need attention.', fieldErrors: { 'ado.organizationUrl': ['Enter an absolute Azure DevOps organization URL.'] } }, 400);
      state.adoSettings = { ...state.adoSettings, organizationUrl: body.organizationUrl, project: body.project }; return new Response(null, { status: 204 });
    }
    if (path === '/api/ado/connection-tests' && method === 'POST') {
      if (options.adoVerificationFails) return json({
        error: 'AzureDevOpsAuthenticationFailed',
        message: 'Azure DevOps rejected the credential. Replace or correct it, then test again.',
        fieldErrors: { 'ado.credential': ['Azure DevOps rejected the stored credential. Replace or correct it, then test again.'] },
      }, 502);
      state.adoCredential = { ...state.adoCredential, verificationStatus: 'verified', lastVerifiedAtUtc: '2026-09-12T13:15:00Z' };
      state.health = { ...state.health, azureDevOps: { ...state.health.azureDevOps, status: 'verified', verificationStatus: 'verified', lastVerifiedAtUtc: '2026-09-12T13:15:00Z', verificationDiagnostic: null } };
      state.setup = { ...state.setup, azureDevOpsCredentialVerified: true }; return json({ succeeded: true, error: null });
    }
    if (path === '/api/ado/query-candidates/validate' && method === 'POST') {
      if (options.queryInputValidationFails) return json({ error: 'ValidationFailed', message: 'The saved query needs attention.', fieldErrors: { 'ado.savedQuery': ['Enter an Azure DevOps saved-query URL or GUID.'] } }, 400);
      if (options.queryValidationFails) return json({
        error: 'SavedQueryNotFoundOrInaccessible',
        message: 'The saved query could not be found or accessed.',
        fieldErrors: { 'ado.savedQuery': ['Check the saved-query URL or GUID and its permissions, then validate again.'] },
      }, 502);
      const body = JSON.parse(String(init.body)) as { savedQuery: string };
      state.pendingQueryId = body.savedQuery;
      return json({ confirmationToken: 'query-token', expiresAtUtc: '2026-09-12T14:00:00Z', organizationUrl: state.adoSettings.organizationUrl ?? '', project: state.adoSettings.project ?? '', savedQueryId: body.savedQuery, candidateFingerprint: 'sha256:candidate', totalCount: options.queryTotalCount ?? 2, preview: Array.from({ length: options.queryPreviewCount ?? 1 }, (_, index) => ({ id: 101 + index, title: `Safe work item ${index + 1}`, workItemType: 'Bug', state: 'New', webUrl: `https://dev.azure.example/item/${101 + index}` })) });
    }
    if (path === '/api/ado/query-candidates/confirm' && method === 'POST') {
      if (options.staleQueryCandidate) return json({ error: 'SavedQueryCandidateStale' }, 409);
      state.adoSettings = { ...state.adoSettings, savedQueryId: state.pendingQueryId, queryConfirmed: true, queryValidatedAtUtc: '2026-09-12T13:20:00Z' };
      state.pendingQueryId = null;
      state.setup = { ...state.setup, azureDevOpsSavedQueryConfirmed: true }; return json({ configurationGeneration: 0, restartRequired: false, activationMessage: 'Staged.' });
    }
    const aiMatch = /^\/api\/ai\/providers\/(openai|anthropic)\/(credential(?:\/local|\/environment)?|credential-tests|models\/discover)$/.exec(path);
    if (aiMatch) {
      const selected = aiMatch[1] as AiProvider;
      const operation = aiMatch[2];
      if (operation === 'credential' && method === 'GET') return json(state.aiCredentials[selected]);
      if ((operation === 'credential/local' || operation === 'credential/environment') && method === 'PUT') {
        state.aiCredentials[selected] = { ...credential(true, false), sourceKind: operation.endsWith('environment') ? 'environmentReference' : 'locallyEncrypted' };
        state.setup = { ...state.setup, aiCredentialConfigured: true, aiCredentialVerified: false, aiModelConfigured: false, runtimeActivationCurrent: false, requiredAiCredentialSlot: selected === 'openai' ? 'openAiApiKey' : 'anthropicApiKey' };
        return json(state.aiCredentials[selected]);
      }
      if (operation === 'credential-tests' && method === 'POST') {
        if (options.aiVerificationFails) return json({
          error: 'AiAuthenticationFailed',
          message: 'The AI provider rejected the credential. Replace or correct it, then verify again.',
          fieldErrors: { 'ai.credential': ['The AI provider rejected the stored credential. Replace or correct it, then verify again.'] },
        }, 502);
        state.aiCredentials[selected] = { ...state.aiCredentials[selected], verificationStatus: 'verified', lastVerifiedAtUtc: '2026-09-12T13:25:00Z' };
        state.health = { ...state.health, ai: { ...state.health.ai, status: 'verified', verificationStatus: 'verified', lastVerifiedAtUtc: '2026-09-12T13:25:00Z', verificationDiagnostic: null } };
        state.setup = { ...state.setup, aiCredentialConfigured: true, aiCredentialVerified: true, requiredAiCredentialSlot: selected === 'openai' ? 'openAiApiKey' : 'anthropicApiKey' };
        return json({ succeeded: true, error: null });
      }
      if (operation === 'models/discover' && method === 'POST') {
        if (options.discoveryUnavailable) return json({ error: 'AiProviderUnavailable', message: 'The AI provider is temporarily unavailable. Try again later; the credential was not marked invalid.' }, 503);
        return json({ succeeded: true, models: [{ provider: selected, id: `${selected}-model`, displayName: `${selected} model` }], error: null });
      }
    }
    if (path === '/api/ai/settings') return json(state.aiSettings);
    if (path === '/api/ai/pricing' && method === 'GET') {
      if (options.pricingEndpointFails) return json({ error: 'PricingUnavailable', message: 'Pricing lookup failed.' }, 503);
      return json(state.modelPricing);
    }
    if (path === '/api/ai/pricing/refresh' && method === 'POST') {
      if (options.pricingRefreshFails) {
        state.modelPricing = { ...state.modelPricing, stale: true, refreshFailed: true };
      } else {
        state.modelPricing = { ...state.modelPricing, stale: false, refreshFailed: false,
          lastVerifiedAtUtc: '2026-09-19T12:00:00Z', expiresAtUtc: '2026-09-26T12:00:00Z' };
      }
      return json(state.modelPricing);
    }
    if (path === '/api/ai/model-candidates/validate' && method === 'POST') {
      const body = JSON.parse(String(init.body)) as { provider: AiProvider; model: string };
      if (options.aiModelValidationFails) return json({ error: 'ValidationFailed', message: 'The model ID needs attention.', fieldErrors: { 'ai.model': ['Enter a valid model ID.'] } }, 400);
      state.pendingModel = body;
      return json({ confirmationToken: 'model-token', expiresAtUtc: '2026-09-12T14:00:00Z', provider: body.provider, model: body.model, displayName: body.model });
    }
    if (path === '/api/ai/model-candidates/confirm' && method === 'POST') {
      if (options.staleModelCandidate) return json({ error: 'AiModelCandidateStale' }, 409);
      state.aiSettings = { ...aiSettings(true), provider: state.pendingModel?.provider ?? 'openai', model: state.pendingModel?.model ?? 'test-model', profileConfigured: true };
      state.modelPricing = { ...state.modelPricing, provider: state.aiSettings.provider!, model: state.aiSettings.model! };
      state.pendingModel = null;
      state.setup = { ...state.setup, aiModelConfigured: true }; return json({ profileConfigured: false, restartRequired: false, activationMessage: 'Staged.' });
    }
    if (path === '/api/runs/work-items' && method === 'POST') {
      if (state.user.role !== 'admin') return json({ error: 'Forbidden', message: 'Administrator access is required.' }, 403);
      if (options.operationDelayMs) await new Promise((resolve) => setTimeout(resolve, options.operationDelayMs));
      if (options.runConflict) return json({ error: 'ActiveRunInProgress', message: 'Another run already holds the active profile execution lease.' }, 409);
      if (options.analyzeValidationFails) return json({ error: 'InvalidWorkItemIdentity', message: 'The work-item URL is outside the configured boundary.', fieldErrors: { workItemIdentity: ['Enter a work-item URL for the configured Azure DevOps organization and project.'] } }, 400);
      const decision = options.analyzeDecision ?? 'pass';
      const body = JSON.parse(String(init.body)) as { workItemId: number | null; workItemUrl: string | null };
      const id = body.workItemId ?? Number(/\/([0-9]+)\/?$/.exec(body.workItemUrl ?? '')?.[1] ?? 101);
      return json({ runId: runSummary.runId, evaluationId: runItemDetail.evaluationId, workItemId: id, decision,
        decisionLabel: decision === 'pass' ? 'Engineering Ready' : decision === 'fail' ? 'Intake Incomplete' : decision === 'notEligible' ? 'Not Eligible' : 'Technical/System Failure',
        eligibility: decision === 'notEligible' ? 'notEligible' : decision === 'error' ? 'unknown' : 'eligible',
        processingStatus: decision === 'error' ? 'error' : 'completed', effectiveMode: 'dryRun', configurationGenerationId: 7,
        detailUrl: `/api/runs/${runSummary.runId}`, itemDetailUrl: `/api/runs/${runSummary.runId}/items/${runItemDetail.evaluationId}` });
    }
    if (path === '/api/runs' && method === 'POST') {
      if (state.user.role !== 'admin') return json({ error: 'Forbidden', message: 'Administrator access is required.' }, 403);
      if (options.operationDelayMs) await new Promise((resolve) => setTimeout(resolve, options.operationDelayMs));
      if (options.runConflict) return json({ error: 'ActiveRunInProgress', message: 'Another run already holds the active profile execution lease.' }, 409);
      return json({ runId: runSummary.runId, invocationType: 'manualIncremental', status: 'completed', effectiveMode: 'dryRun', configurationGenerationId: 7, detailUrl: `/api/runs/${runSummary.runId}` });
    }
    if (path === '/api/runs' && method === 'GET') return json(options.runPage ?? runPage);
    if (path === `/api/runs/${runSummary.runId}` && method === 'GET') return json(options.runDetail ?? runDetail);
    if (path === `/api/runs/${runSummary.runId}/items/${runItemDetail.evaluationId}` && method === 'GET') return json(options.runItemDetail ?? runItemDetail);
    if (path === '/api/profile') return json(state.profile);
    if (path === '/api/version') return json(version);
    return json({ error: 'NotFound', message: 'Not found.' }, 404);
  });

  vi.stubGlobal('fetch', fetchMock);
  return { state, calls, fetchMock };
}
