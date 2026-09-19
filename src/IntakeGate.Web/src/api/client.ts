import { ApiError } from './api-error';
import type {
  ApiErrorResponse,
  AiCredentialTest,
  AiModelCandidate,
  AiModelConfirmation,
  AiModelDiscovery,
  AiModelPricing,
  AiProvider,
  AiSettings,
  AnalyzeWorkItemRequest,
  AnalyzeWorkItemResponse,
  AzureDevOpsConnectionTest,
  AzureDevOpsQueryCandidate,
  AzureDevOpsQueryConfirmation,
  AzureDevOpsSettings,
  BootstrapRequest,
  BootstrapStatus,
  CredentialMetadata,
  ControlPlaneAuditPage,
  HomeSummary,
  HomeWindowDays,
  LoginRequest,
  OnboardingDefaults,
  OnboardingDraftState,
  OnboardingDraftUpdate,
  ProfileState,
  PortableProfileDocument,
  ProfileImportPreview,
  ProfileImportResult,
  ProfileUpdate,
  RunDetail,
  RunExecution,
  RunHistoryPage,
  RunItemDetail,
  RunTriggerType,
  OperationalRunStatus,
  SafeUser,
  SetupStatus,
  SetupProgress,
  SystemHealth,
  VersionInfo,
} from './contracts';

type UnauthorizedHandler = () => void;

const mutatingMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

export class ApiClient {
  private csrfToken: string | null = null;
  private csrfRequest: Promise<string> | null = null;
  private unauthorizedHandler: UnauthorizedHandler | null = null;

  public onUnauthorized(handler: UnauthorizedHandler): () => void {
    this.unauthorizedHandler = handler;
    return () => {
      if (this.unauthorizedHandler === handler) this.unauthorizedHandler = null;
    };
  }

  public resetCsrf(): void {
    this.csrfToken = null;
    this.csrfRequest = null;
  }

  public getSession(signal?: AbortSignal): Promise<SafeUser> {
    return this.request('/api/auth/session', signal ? { signal } : {});
  }

  public getBootstrapStatus(signal?: AbortSignal): Promise<BootstrapStatus> {
    return this.request('/api/auth/bootstrap/status', signal ? { signal } : {});
  }

  public async login(body: LoginRequest): Promise<SafeUser> {
    const user = await this.request<SafeUser>('/api/auth/login', { method: 'POST', body: JSON.stringify(body) });
    this.resetCsrf();
    return user;
  }

  public async bootstrap(body: BootstrapRequest): Promise<SafeUser> {
    const user = await this.request<SafeUser>('/api/auth/bootstrap', { method: 'POST', body: JSON.stringify(body) });
    this.resetCsrf();
    return user;
  }

  public async logout(): Promise<void> {
    await this.request<void>('/api/auth/logout', { method: 'POST' });
    this.resetCsrf();
  }

  public getSetupStatus(signal?: AbortSignal): Promise<SetupStatus> {
    return this.request('/api/setup/status', signal ? { signal } : {});
  }

  public getSetupDefaults(signal?: AbortSignal): Promise<OnboardingDefaults> {
    return this.request('/api/setup/defaults', signal ? { signal } : {});
  }

  public getOnboardingDraft(signal?: AbortSignal): Promise<OnboardingDraftState> {
    return this.request('/api/setup/profile-draft', signal ? { signal } : {});
  }

  public initializeOnboardingDraft(): Promise<OnboardingDraftState> {
    return this.request('/api/setup/profile-draft/initialize', { method: 'POST' });
  }

  public updateOnboardingDraft(body: OnboardingDraftUpdate): Promise<OnboardingDraftState> {
    return this.request('/api/setup/profile-draft', { method: 'PUT', body: JSON.stringify(body) });
  }

  public recordSetupProgress(step: string): Promise<SetupProgress> {
    return this.request('/api/setup/progress', { method: 'PUT', body: JSON.stringify({ step }) });
  }

  public finalizeSetup(expectedDraftRevision: number | string): Promise<ProfileState> {
    return this.request('/api/setup/finalize', {
      method: 'POST',
      body: JSON.stringify({ expectedDraftRevision }),
    });
  }

  public getAzureDevOpsCredential(signal?: AbortSignal): Promise<CredentialMetadata> {
    return this.request('/api/ado/credential', signal ? { signal } : {});
  }

  public replaceAzureDevOpsCredential(replacement: string): Promise<CredentialMetadata> {
    return this.request('/api/ado/credential/local', {
      method: 'PUT',
      body: JSON.stringify({ replacement }),
    });
  }

  public setAzureDevOpsCredentialEnvironment(environmentVariableName: string): Promise<CredentialMetadata> {
    return this.request('/api/ado/credential/environment', {
      method: 'PUT',
      body: JSON.stringify({ environmentVariableName }),
    });
  }

  public getAzureDevOpsSettings(signal?: AbortSignal): Promise<AzureDevOpsSettings> {
    return this.request('/api/ado/settings', signal ? { signal } : {});
  }

  public saveAzureDevOpsSettings(organizationUrl: string, project: string): Promise<void> {
    return this.request('/api/ado/settings', {
      method: 'PUT',
      body: JSON.stringify({ organizationUrl, project }),
    });
  }

  public testAzureDevOpsConnection(): Promise<AzureDevOpsConnectionTest> {
    return this.request('/api/ado/connection-tests', { method: 'POST' });
  }

  public validateSavedQuery(
    organizationUrl: string,
    project: string,
    savedQuery: string,
  ): Promise<AzureDevOpsQueryCandidate> {
    return this.request('/api/ado/query-candidates/validate', {
      method: 'POST',
      body: JSON.stringify({ organizationUrl, project, savedQuery }),
    });
  }

  public confirmSavedQuery(confirmationToken: string): Promise<AzureDevOpsQueryConfirmation> {
    return this.request('/api/ado/query-candidates/confirm', {
      method: 'POST',
      body: JSON.stringify({ confirmationToken }),
    });
  }

  public getAiCredential(provider: AiProvider, signal?: AbortSignal): Promise<CredentialMetadata> {
    return this.request(`/api/ai/providers/${provider}/credential`, signal ? { signal } : {});
  }

  public replaceAiCredential(provider: AiProvider, replacement: string): Promise<CredentialMetadata> {
    return this.request(`/api/ai/providers/${provider}/credential/local`, {
      method: 'PUT',
      body: JSON.stringify({ replacement }),
    });
  }

  public setAiCredentialEnvironment(provider: AiProvider, environmentVariableName: string): Promise<CredentialMetadata> {
    return this.request(`/api/ai/providers/${provider}/credential/environment`, {
      method: 'PUT',
      body: JSON.stringify({ environmentVariableName }),
    });
  }

  public testAiCredential(provider: AiProvider): Promise<AiCredentialTest> {
    return this.request(`/api/ai/providers/${provider}/credential-tests`, { method: 'POST' });
  }

  public discoverAiModels(provider: AiProvider): Promise<AiModelDiscovery> {
    return this.request(`/api/ai/providers/${provider}/models/discover`, { method: 'POST' });
  }

  public getAiSettings(signal?: AbortSignal): Promise<AiSettings> {
    return this.request('/api/ai/settings', signal ? { signal } : {});
  }

  public getAiModelPricing(signal?: AbortSignal): Promise<AiModelPricing> {
    return this.request('/api/ai/pricing', signal ? { signal } : {});
  }

  public refreshAiModelPricing(): Promise<AiModelPricing> {
    return this.request('/api/ai/pricing/refresh', { method: 'POST' });
  }

  public validateAiModel(provider: AiProvider, model: string): Promise<AiModelCandidate> {
    return this.request('/api/ai/model-candidates/validate', {
      method: 'POST',
      body: JSON.stringify({ provider, model }),
    });
  }

  public confirmAiModel(confirmationToken: string): Promise<AiModelConfirmation> {
    return this.request('/api/ai/model-candidates/confirm', {
      method: 'POST',
      body: JSON.stringify({ confirmationToken }),
    });
  }

  public getProfile(signal?: AbortSignal): Promise<ProfileState> {
    return this.request('/api/profile', signal ? { signal } : {});
  }

  public updateProfile(body: ProfileUpdate): Promise<ProfileState> {
    return this.request('/api/profile', { method: 'PUT', body: JSON.stringify(body) });
  }

  public exportProfile(): Promise<PortableProfileDocument> {
    return this.request('/api/profile/export');
  }

  public validateProfileImport(document: unknown): Promise<ProfileImportPreview> {
    return this.request('/api/profile/import/validate', { method: 'POST', body: JSON.stringify(document) });
  }

  public importProfile(document: PortableProfileDocument, expectedProfileRevision: string | null,
    expectedDraftRevision: number | null): Promise<ProfileImportResult> {
    const query = new URLSearchParams();
    if (expectedProfileRevision) query.set('expectedProfileRevision', expectedProfileRevision);
    if (expectedDraftRevision != null) query.set('expectedDraftRevision', String(expectedDraftRevision));
    return this.request(`/api/profile/import?${query.toString()}`, { method: 'POST', body: JSON.stringify(document) });
  }

  public getVersion(signal?: AbortSignal): Promise<VersionInfo> {
    return this.request('/api/version', signal ? { signal } : {});
  }

  public getSystemHealth(signal?: AbortSignal): Promise<SystemHealth> {
    return this.request('/api/support/health', signal ? { signal } : {});
  }

  public getHomeSummary(windowDays: HomeWindowDays = 30, signal?: AbortSignal): Promise<HomeSummary> {
    return this.request(`/api/home/summary?windowDays=${windowDays}`, signal ? { signal } : {});
  }

  public listAudit(query: {
    page: number;
    pageSize: number;
    occurredFromUtc?: string;
    occurredToUtc?: string;
    actor?: string;
    operation?: string;
    targetCategory?: string;
    targetId?: string;
  }, signal?: AbortSignal): Promise<ControlPlaneAuditPage> {
    const search = new URLSearchParams({ page: String(query.page), pageSize: String(query.pageSize) });
    if (query.occurredFromUtc) search.set('occurredFromUtc', query.occurredFromUtc);
    if (query.occurredToUtc) search.set('occurredToUtc', query.occurredToUtc);
    if (query.actor) search.set('actor', query.actor);
    if (query.operation) search.set('operation', query.operation);
    if (query.targetCategory) search.set('targetCategory', query.targetCategory);
    if (query.targetId) search.set('targetId', query.targetId);
    return this.request(`/api/audit?${search.toString()}`, signal ? { signal } : {});
  }

  public analyzeWorkItem(body: AnalyzeWorkItemRequest): Promise<AnalyzeWorkItemResponse> {
    return this.request('/api/runs/work-items', { method: 'POST', body: JSON.stringify(body) });
  }

  public runProfileNow(): Promise<RunExecution> {
    return this.request('/api/runs', { method: 'POST' });
  }

  public listRuns(query: {
    page: number;
    pageSize: number;
    startedFromUtc?: string;
    startedToUtc?: string;
    invocationType?: RunTriggerType;
    status?: OperationalRunStatus;
    workItemId?: string;
  }, signal?: AbortSignal): Promise<RunHistoryPage> {
    const search = new URLSearchParams({ page: String(query.page), pageSize: String(query.pageSize) });
    if (query.startedFromUtc) search.set('startedFromUtc', query.startedFromUtc);
    if (query.startedToUtc) search.set('startedToUtc', query.startedToUtc);
    if (query.invocationType) search.set('invocationType', query.invocationType);
    if (query.status) search.set('status', query.status);
    if (query.workItemId) search.set('workItemId', query.workItemId);
    return this.request(`/api/runs?${search.toString()}`, signal ? { signal } : {});
  }

  public getRun(runId: string, signal?: AbortSignal): Promise<RunDetail> {
    return this.request(`/api/runs/${encodeURIComponent(runId)}`, signal ? { signal } : {});
  }

  public getRunItem(runId: string, evaluationId: string, signal?: AbortSignal): Promise<RunItemDetail> {
    return this.request(
      `/api/runs/${encodeURIComponent(runId)}/items/${encodeURIComponent(evaluationId)}`,
      signal ? { signal } : {},
    );
  }

  private async request<T>(path: string, init: RequestInit = {}, allowCsrfRetry = true): Promise<T> {
    const method = (init.method ?? 'GET').toUpperCase();
    const headers = new Headers(init.headers);
    headers.set('Accept', 'application/json');
    if (init.body !== undefined) headers.set('Content-Type', 'application/json');
    if (mutatingMethods.has(method)) headers.set('X-CSRF-TOKEN', await this.getCsrfToken());

    let response: Response;
    try {
      response = await fetch(path, { ...init, method, headers, credentials: 'include' });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') throw error;
      throw ApiError.network();
    }

    if (response.ok) {
      if (response.status === 204) return undefined as T;
      return (await response.json()) as T;
    }

    const body = await this.readError(response);
    if (response.status === 400 && body?.error === 'InvalidAntiforgeryToken' && allowCsrfRetry) {
      this.resetCsrf();
      return this.request<T>(path, init, false);
    }
    if (response.status === 401) this.unauthorizedHandler?.();
    throw ApiError.fromResponse(response.status, body);
  }

  private async getCsrfToken(): Promise<string> {
    if (this.csrfToken !== null) return this.csrfToken;
    this.csrfRequest ??= this.fetchCsrfToken();
    try {
      this.csrfToken = await this.csrfRequest;
      return this.csrfToken;
    } finally {
      this.csrfRequest = null;
    }
  }

  private async fetchCsrfToken(): Promise<string> {
    let response: Response;
    try {
      response = await fetch('/api/auth/csrf', {
        method: 'GET',
        headers: { Accept: 'application/json' },
        credentials: 'include',
      });
    } catch {
      throw ApiError.network();
    }
    if (!response.ok) throw ApiError.fromResponse(response.status, await this.readError(response));
    const body = (await response.json()) as { token?: unknown };
    if (typeof body.token !== 'string' || body.token.length === 0) {
      throw new ApiError('unexpected', 'The security token response was invalid.', { code: 'InvalidCsrfResponse' });
    }
    return body.token;
  }

  private async readError(response: Response): Promise<Partial<ApiErrorResponse> | null> {
    const contentType = response.headers.get('content-type') ?? '';
    if (!contentType.includes('application/json')) return null;
    try {
      const value = (await response.json()) as unknown;
      if (typeof value !== 'object' || value === null) return null;
      const candidate = value as Record<string, unknown>;
      return {
        ...(typeof candidate.error === 'string' ? { error: candidate.error } : {}),
        ...(typeof candidate.message === 'string' ? { message: candidate.message } : {}),
        ...(Array.isArray(candidate.details) ? { details: candidate.details.filter((item): item is string => typeof item === 'string') } : {}),
        ...(typeof candidate.fieldErrors === 'object' && candidate.fieldErrors !== null ? { fieldErrors: candidate.fieldErrors as NonNullable<ApiErrorResponse['fieldErrors']> } : {}),
        ...(typeof candidate.sectionErrors === 'object' && candidate.sectionErrors !== null ? { sectionErrors: candidate.sectionErrors as NonNullable<ApiErrorResponse['sectionErrors']> } : {}),
      };
    } catch {
      return null;
    }
  }
}

export const apiClient = new ApiClient();
